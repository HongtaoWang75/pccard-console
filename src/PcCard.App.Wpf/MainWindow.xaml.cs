using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcCard.AppWpf.Activation;
using PcCard.Core;
using PcCard.Core.Cloud;
using PcCard.Core.Devices;
using PcCard.Core.Logging;
using PcCard.Core.Models;
using PcCard.Core.Probe;
using PcCard.Core.Scheduling;
using PcCard.Core.Storage;

namespace PcCard.AppWpf;

/// <summary>
/// 主窗口（WPF 版，功能与 WinUI 版对齐）。
/// ★ 为什么是 WPF：WinUI 3 在当前 WindowsAppSDK 下于 RDP 会话重连时必然
///   fail-fast 崩溃（Microsoft.UI.Xaml.dll +0x3a9dbd，E_UNEXPECTED），
///   本实现是 docs/runbook 里的架构保险。PcCard.Core 完全复用。
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly FileLog Log = new("gui");

    private readonly DispatcherTimer _timer = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<TaskItemVm> _tasks = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<LogItemVm> _logs = new();

    private DateTime _fastUntil = DateTime.MinValue;

    // ── 动作后置（点击后置禁用）—— piid1 是「按键动作」语义，连点=连按电源键
    private ActionKind? _pendingKind;
    private DateTime _pendingMinUntil;
    private DateTime _pendingDeadline;
    private DateTime _footerHoldUntil;
    private bool _busy;
    private bool _firstRefreshDone;
    private bool _reallyClosing;
    private bool _loadingTasks;
    private bool _loadingDevices;
    private bool _actionInFlight;
    private string? _lastDeviceType;

    private bool _canOn = true;
    private bool _canOff = true;
    private bool _canReboot;

    /// <summary>截图用克隆构造：不启动定时器/加载/关闭钩子，供 --selfcheck 离屏渲染。</summary>
    internal MainWindow(bool renderOnly)
    {
        InitializeComponent();
        Title = "开机卡控制台";
        VersionText.Text = $"PcCard v{AppPaths.Version}";
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        if (renderOnly) return;

        if (!IsElevated())
            TasksWarnText.Visibility = Visibility.Visible;

        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += (_, _) => SafeRun("定时刷新", () => RefreshAsync());
        _timer.Start();

        TasksList.ItemsSource = _tasks;
        LogsList.ItemsSource = _logs;

        Loaded += (_, _) =>
        {
            LoadDeviceCombo();
            LoadTasks();
            LoadLogs();
            ApplyButtonStates();
            SafeRun("首次刷新", () => RefreshAsync(manual: true));
        };
        Closing += OnClosing;
    }

    public MainWindow() : this(false)
    {
    }

    // ── Win10/11 深色标题栏：WPF 不会自动跟随深色主题 ────────────────
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            // Win10 2004+ 用属性 20；1903-1909 用 19。先试 20，失败再试 19。
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch
        {
            // 非关键：个别系统不支持该属性时保持默认标题栏
        }
    }

    /// <summary>把实时窗口的可视状态复制到截图克隆（文本/颜色/可见性/使能/列表数据源）。</summary>
    internal void CopyVisualStateFrom(MainWindow src)
    {
        DeviceNameText.Text = src.DeviceNameText.Text;
        OnlineDot.Fill = src.OnlineDot.Fill;
        OnlineText.Text = src.OnlineText.Text;
        BigStateText.Text = src.BigStateText.Text;
        BigStateText.Foreground = src.BigStateText.Foreground;
        StatusStripe.Fill = src.StatusStripe.Fill;
        MetaText.Text = src.MetaText.Text;
        OfflineWarnText.Visibility = src.OfflineWarnText.Visibility;
        TasksWarnText.Visibility = src.TasksWarnText.Visibility;
        FooterText.Text = src.FooterText.Text;
        FooterText.Foreground = src.FooterText.Foreground;
        FooterDot.Fill = src.FooterDot.Fill;
        PowerOnBtn.IsEnabled = src.PowerOnBtn.IsEnabled;
        PowerOffBtn.IsEnabled = src.PowerOffBtn.IsEnabled;
        RebootBtn.IsEnabled = src.RebootBtn.IsEnabled;
        ForceCheck.IsChecked = src.ForceCheck.IsChecked;
        PowerOffLabel.Text = src.PowerOffLabel.Text;
        RebootLabel.Text = src.RebootLabel.Text;
        BusyRing.Visibility = src.BusyRing.Visibility;
        TasksList.ItemsSource = src.TasksList.ItemsSource;
        LogsList.ItemsSource = src.LogsList.ItemsSource;
        LogsHeader.Text = src.LogsHeader.Text;
        TasksEmptyText.Visibility = src.TasksEmptyText.Visibility;
        LogsEmptyText.Visibility = src.LogsEmptyText.Visibility;
    }

    // ═══════════════════════ 托盘/激活 ═══════════════════════

    public void ShowFromTray()
    {
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        catch (Exception ex)
        {
            Log.Warn($"ShowFromTray 失败：{ex.Message}");
        }
    }

    public void TriggerRefresh() => SafeRun("刷新", () => RefreshAsync(manual: true));

    public void ForceClose()
    {
        _reallyClosing = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true; // 关窗 = 隐藏进托盘，进程常驻
        Hide();
    }

    public void HandleActivation(string raw)
    {
        var args = StartupArgs.FromActivationMessage(raw);
        if (args.SelfCheck)
        {
            SafeRun("自检", async () =>
            {
                var text = await RunSelfCheckAsync(args.PngPath, args.TimeoutSec);
                Log.Info(text.Replace(Environment.NewLine, " | "));
                App.WriteSidecar(args.PngPath, text);
            });
        }
        else
        {
            ShowFromTray();
        }
    }

    // ═══════════════════════ 状态刷新 ═══════════════════════

    public async Task RefreshAsync(bool manual = false)
    {
        if (_busy) return;

        if (DateTime.UtcNow > _fastUntil && _timer.Interval != TimeSpan.FromSeconds(20))
            _timer.Interval = TimeSpan.FromSeconds(20);

        _busy = true;
        try
        {
            SetBusyUi(true);

            var cred = CredentialVault.Load();
            if (cred is null || !cred.IsComplete)
            {
                SetFooter("凭据未导入 —— 运行 pccard credential import 后点「刷新」", warn: true);
                return;
            }
            if (DateTime.UtcNow >= _footerHoldUntil)
                SetFooter($"米家账号已登录（userId {cred.UserId}）· 导入于 {FormatStamp(cred.CapturedAt)}", warn: false);

            var dev = ConfigStore.GetSelectedDevice();
            if (dev is null)
            {
                SetFooter("未导入设备清单 —— 运行 pccard device import devices.json", warn: true);
                return;
            }

            using var cloud = new MiCloudClient(cred);
            var card = new ComputerPowerCard(cloud, dev.Did);

            bool? online = null;
            try
            {
                var list = await cloud.GetDeviceListAsync();
                online = list.FirstOrDefault(d => d.Did == dev.Did)?.IsOnline;

                // 名称/IP/token/在线状态跟云端自动同步
                if (ConfigStore.SyncFromCloud(list))
                {
                    LoadDeviceCombo();
                    dev = ConfigStore.GetSelectedDevice() ?? dev;
                }
            }
            catch (MiCloudException)
            {
                // 拿不到在线状态不挡主流程
            }

            var tunnelEp = ConfigStore.LoadState().TunnelEndpoint;
            var tunnel = await RdpTunnelProbe.IsEndpointOpenAsync(tunnelEp);

            CardState state;
            try
            {
                state = await card.GetStateAsync();
            }
            catch (MiCloudException ex) when (ex.Kind == MiCloudErrorKind.DeviceOffline)
            {
                state = new CardState(null, _lastDeviceType);
            }

            // ── 动作后置：按钮保持禁用，直到状态与期望一致（或超时）才解锁 ──
            ReleasePendingIfDue(state, quiet: false);

            UpdateStatusUi(dev, state, online, tunnel);
            LoadLogs();
        }
        catch (MiCloudException ex)
        {
            SetFooter($"云接口错误（{ex.Kind}）：{ex.Message}", warn: true);
            Log.Error("refresh cloud error", ex);
        }
        catch (Exception ex)
        {
            SetFooter($"刷新失败：{ex.Message}", warn: true);
            Log.Error("refresh failed", ex);
        }
        finally
        {
            // ★ 兜底释放：即使刷新因异常永远走不到成功路径，动作键也不能永久禁用
            ReleasePendingIfDue(null, quiet: true);
            _busy = false;
            SetBusyUi(false);
            _firstRefreshDone = true;
        }
    }

    /// <summary>
    /// 动作后置的释放判定（幂等）。state 为 null（异常兜底路径）时只按截止时间强制释放；
    /// quiet=true 且未到释放条件时不碰页脚（避免覆盖错误提示）。
    /// </summary>
    private void ReleasePendingIfDue(CardState? state, bool quiet)
    {
        if (_pendingKind is not ActionKind pk) return;

        var now = DateTime.UtcNow;
        var minDone = now >= _pendingMinUntil;
        bool? satisfied = state is null ? null : pk switch
        {
            ActionKind.PowerOn => state.PowerOn == true ? true : state.PowerOn == false ? false : null,
            ActionKind.PowerOff or ActionKind.ForcePowerOff
                => state.PowerOn == false ? true : state.PowerOn == true ? false : null,
            _ => null,
        };
        var isRebootKind = pk is ActionKind.Reboot or ActionKind.ForceReboot;
        var released = (isRebootKind && minDone)
                    || (minDone && satisfied == true)
                    || now >= _pendingDeadline;

        if (released)
        {
            if (satisfied == true)
                SetFooter($"「{pk.ToDisplayName()}」已确认完成 · {DateTime.Now:HH:mm:ss}", warn: false);
            else if (isRebootKind && minDone)
                SetFooter($"「{pk.ToDisplayName()}」已发送（重启类无云端状态可确认）· 按钮已解锁", warn: false);
            else
                SetFooter($"「{pk.ToDisplayName()}」已发送（状态未按时确认，按钮已解锁）", warn: true);
            _footerHoldUntil = DateTime.UtcNow.AddSeconds(30);
            _pendingKind = null;
        }
        else if (!quiet)
        {
            var left = (int)Math.Max(0, (_pendingDeadline - now).TotalSeconds);
            SetFooter($"已发送「{pk.ToDisplayName()}」指令 · 等待状态更新…（最长 {left}s）", warn: false);
            _footerHoldUntil = DateTime.UtcNow.AddSeconds(10);
        }
    }

    private void UpdateStatusUi(DeviceRecord dev, CardState state, bool? online, bool tunnel)
    {
        DeviceNameText.Text = dev.DisplayName;

        var offline = online == false;
        OnlineDot.Fill = Res(online switch { true => "PcSuccess", false => "PcWarn", _ => "PcUnknown" });
        OnlineText.Text = online switch { true => "开机卡在线", false => "卡片离线", _ => "状态未知" };
        OfflineWarnText.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;

        if (state.DeviceType is not null) _lastDeviceType = state.DeviceType;

        if (offline)
        {
            BigStateText.Text = "状态未知";
            BigStateText.Foreground = Res("PcTextTertiary");
            StatusStripe.Fill = Res("PcWarn");
            _canOn = _canOff = _canReboot = false;
        }
        else if (state.PowerOn == true)
        {
            BigStateText.Text = "已开机";
            BigStateText.Foreground = Res("PcOn");
            StatusStripe.Fill = Res("PcAccent");
            _canOn = false;
            _canOff = true;
            _canReboot = true;
        }
        else if (state.PowerOn == false)
        {
            BigStateText.Text = "已关机";
            BigStateText.Foreground = Res("PcOff");
            StatusStripe.Fill = Res("PcOffStripe");
            _canOn = true;
            _canOff = false;
            _canReboot = false;
        }
        else
        {
            BigStateText.Text = "状态未知";
            BigStateText.Foreground = Res("PcTextTertiary");
            StatusStripe.Fill = Res("PcUnknown");
            _canOn = _canOff = true;
            _canReboot = false;
        }

        var lastOp = OperationLog.ReadRecent(1).FirstOrDefault();
        var lastText = lastOp is null ? "无记录" : lastOp.Time.Replace('T', ' ');
        MetaText.Text = $"设备类型 {state.DeviceType ?? _lastDeviceType ?? "—"} · " +
                        $"旁路信号 {(tunnel ? "疑似开机" : "无")} · 上次操作 {lastText}";

        ApplyButtonStates();
    }

    private void ApplyButtonStates()
    {
        // 防连按由「动作在飞」+「动作后置」承担；刷新中不动作键（慢网络下不该长期变灰）
        var gate = !_actionInFlight && _pendingKind is null;
        PowerOnBtn.IsEnabled = _canOn && gate;
        PowerOffBtn.IsEnabled = _canOff && gate;
        RebootBtn.IsEnabled = _canReboot && gate;
    }

    // ═══════════════════════ 开关机操作 ═══════════════════════

    private bool ForceMode => ForceCheck.IsChecked == true;

    private void OnPowerOnClick(object sender, RoutedEventArgs e)
        => SafeRun("开机", () => DoActionAsync(ActionKind.PowerOn));

    private void OnPowerOffClick(object sender, RoutedEventArgs e)
        => SafeRun(ForceMode ? "强制关机" : "关机",
            () => DoActionAsync(ForceMode ? ActionKind.ForcePowerOff : ActionKind.PowerOff));

    private void OnRebootClick(object sender, RoutedEventArgs e)
        => SafeRun(ForceMode ? "强制重启" : "重启",
            () => DoActionAsync(ForceMode ? ActionKind.ForceReboot : ActionKind.Reboot));

    private void OnForceChanged(object sender, RoutedEventArgs e)
    {
        PowerOffLabel.Text = ForceMode ? "强制关机" : "关机";
        RebootLabel.Text = ForceMode ? "强制重启" : "重启";
    }

    private async Task DoActionAsync(ActionKind kind)
    {
        if (_actionInFlight)
        {
            SetFooter("上一个操作还在进行中，请稍候…", warn: false);
            return;
        }
        _actionInFlight = true;
        ApplyButtonStates();
        try
        {
            await DoActionCoreAsync(kind);
        }
        finally
        {
            _actionInFlight = false;
            ApplyButtonStates();
        }
    }

    private async Task DoActionCoreAsync(ActionKind kind)
    {
        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            InfoDialog.Show(this, "未就绪", "还没有导入设备清单（pccard device import devices.json）。");
            return;
        }

        var (confirmTitle, confirmText, confirmOk) = kind switch
        {
            ActionKind.PowerOff => ("关闭电脑", "确定要关闭电脑吗？\n将模拟按下电源键，系统会正常关机。", "关机"),
            ActionKind.ForcePowerOff => ("强制关机",
                "确定要强制关机吗？\n将模拟长按电源键强制断电 —— 未保存的数据会丢失！", "强制关机"),
            ActionKind.ForceReboot => ("强制重启",
                "确定要强制重启吗？\n将模拟长按电源键强制断电后重启 —— 未保存的数据会丢失！", "强制重启"),
            _ => (null, null, null),
        };
        if (confirmTitle is not null &&
            !ConfirmDialog.Ask(this, confirmTitle, confirmText!, confirmOk!))
            return;

        var cred = CredentialVault.Load();
        if (cred is null || !cred.IsComplete)
        {
            InfoDialog.Show(this, "未就绪", "云凭据未导入。");
            return;
        }

        SetBusyUi(true);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cloud = new MiCloudClient(cred);
            var card = new ComputerPowerCard(cloud, dev.Did);
            try
            {
                await card.ExecuteAsync(kind);
                OperationLog.Append(new OperationEntry
                {
                    Source = "gui",
                    DeviceName = dev.DisplayName,
                    Action = kind.ToWireName(),
                    Did = dev.Did,
                    Ok = true,
                    DurationMs = sw.ElapsedMilliseconds,
                });
                SetFooter($"已发送「{kind.ToDisplayName()}」指令 {DateTime.Now:HH:mm:ss}（等待状态确认）", warn: false);
                var isReboot = kind is ActionKind.Reboot or ActionKind.ForceReboot;
                _pendingKind = kind;
                _pendingMinUntil = DateTime.UtcNow.AddSeconds(isReboot ? 60 : 12);
                _pendingDeadline = DateTime.UtcNow.AddSeconds(isReboot ? 90 : 120);
                ApplyButtonStates();
            }
            catch (Exception ex)
            {
                OperationLog.Append(new OperationEntry
                {
                    Source = "gui",
                    DeviceName = dev.DisplayName,
                    Action = kind.ToWireName(),
                    Did = dev.Did,
                    Ok = false,
                    Message = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds,
                });
                InfoDialog.Show(this, "操作失败", ex.Message);
            }

            _fastUntil = DateTime.UtcNow.AddSeconds(
                kind is ActionKind.Reboot or ActionKind.ForceReboot ? 150 : 90);
            _timer.Interval = TimeSpan.FromSeconds(5);
            await Task.Delay(3000);
        }
        finally
        {
            SetBusyUi(false);
            await RefreshAsync();
            LoadLogs();
        }
    }

    // ═══════════════════════ 定时任务 ═══════════════════════

    private void LoadTasks()
    {
        _loadingTasks = true;
        try
        {
            _tasks.Clear();
            foreach (var t in TaskStore.Load())
                _tasks.Add(new TaskItemVm(t, ApplyTaskToggle));
        }
        finally
        {
            _loadingTasks = false;
        }
        TasksEmptyText.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTaskToggle(TaskItemVm vm)
    {
        if (_loadingTasks) return; // 回滚（写回 Enabled）时不要再触发写入
        SafeRun("任务开关", async () =>
        {
            var (ok, msg) = await Task.Run(() => WindowsTaskScheduler.CreateOrUpdate(vm.Model, AppPaths.CliExe, DateTime.Now));
            if (ok)
            {
                TaskStore.Upsert(vm.Model);
                SetFooter($"任务「{vm.Model.Name}」已{(vm.Model.Enabled ? "启用" : "停用")}", warn: false);
            }
            else
            {
                SetFooter($"计划任务更新失败：{msg}", warn: true);
                _loadingTasks = true;
                vm.Enabled = !vm.Enabled; // 回滚开关
                _loadingTasks = false;
            }
        });
    }

    private void OnAddTaskClick(object sender, RoutedEventArgs e) => SafeRun("新增任务", AddTaskAsync);

    private async Task AddTaskAsync()
    {
        var dialog = new AddTaskDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Task is null) return;

        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            InfoDialog.Show(this, "未就绪", "还没有导入设备清单。");
            return;
        }

        var task = dialog.Task;
        task.DeviceDid = dev.Did;

        var (ok, msg) = await Task.Run(() => WindowsTaskScheduler.CreateOrUpdate(task, AppPaths.CliExe, DateTime.Now));
        if (!ok)
        {
            InfoDialog.Show(this, "创建失败", $"schtasks 返回：\n{msg}");
            return;
        }
        TaskStore.Upsert(task);
        LoadTasks();
        SetFooter($"任务「{task.Name}」已创建，下次触发 {TaskXmlBuilder.NextBoundary(task, DateTime.Now):MM-dd HH:mm}", warn: false);
    }

    private void OnTaskDelete(object sender, RoutedEventArgs e) => SafeRun("删除任务", () => DeleteTaskAsync(sender));

    private async Task DeleteTaskAsync(object sender)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var t = TaskStore.Find(id);
        if (t is null) return;

        if (!ConfirmDialog.Ask(this, "删除定时任务", $"确定删除「{t.Name}」吗？", "删除")) return;

        await Task.Run(() => WindowsTaskScheduler.Delete(t));
        TaskStore.Remove(id);
        LoadTasks();
        SetFooter($"任务「{t.Name}」已删除", warn: false);
    }

    // ═══════════════════════ 设备与日志 ═══════════════════════

    private void LoadDeviceCombo()
    {
        var doc = ConfigStore.LoadDevices();
        if (doc is null || doc.Devices.Count == 0) return;

        _loadingDevices = true;
        try
        {
            DeviceCombo.DisplayMemberPath = nameof(DeviceRecord.DisplayName);
            DeviceCombo.ItemsSource = doc.Devices;

            // ★ 初始化只用「存储的原值」精确匹配（防止幽灵事件把第一台写回 state.json）
            var stored = ConfigStore.LoadState().SelectedDid;
            var item = stored is null
                ? doc.Devices.FirstOrDefault()
                : doc.Devices.FirstOrDefault(d => d.Did == stored);

            if (item is not null && item.Did != stored)
                ConfigStore.SetSelectedDid(item.Did);
            DeviceCombo.SelectedItem = item;
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e) => SafeRun("切换设备", async () =>
    {
        if (_loadingDevices) return;
        if (DeviceCombo.SelectedItem is DeviceRecord d)
        {
            if (ConfigStore.LoadState().SelectedDid == d.Did) return;
            ConfigStore.SetSelectedDid(d.Did);
            _pendingKind = null; // 换设备即放弃旧设备的等待态
            await RefreshAsync(manual: true);
        }
    });

    private void LoadLogs()
    {
        _logs.Clear();

        var dev = ConfigStore.GetSelectedDevice();

        // Did → 显示名（给 2026-10-20 之前没有 DeviceName 的旧日志回填）
        var names = new Dictionary<string, string>();
        foreach (var d in ConfigStore.LoadDevices()?.Devices ?? new List<DeviceRecord>())
            if (!string.IsNullOrEmpty(d.Did))
                names[d.Did] = d.DisplayName;

        // 日志只显示「当前选中设备」的操作记录；切换设备时 RefreshAsync 会重跑本方法
        foreach (var entry in OperationLog.ReadRecent(500))
        {
            if (_logs.Count >= 100) break;
            if (dev is not null && entry.Did != dev.Did) continue;
            _logs.Add(new LogItemVm(entry, names));
        }

        LogsEmptyText.Visibility = _logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogsHeader.Text = dev is null
            ? "操作日志"
            : $"操作日志 · {LogItemVm.ShortDeviceName(dev.DisplayName)}";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => SafeRun("刷新", () => RefreshAsync(manual: true));

    // ═══════════════════════ 设备设置（童锁 / 指示灯 / 通电自动开机）═══════════════════════

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SafeRun("设备设置", ShowSettingsDialogAsync);

    private async Task ShowSettingsDialogAsync()
    {
        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            InfoDialog.Show(this, "未就绪", "还没有导入设备清单。");
            return;
        }
        var cred = CredentialVault.Load();
        if (cred is null || !cred.IsComplete)
        {
            InfoDialog.Show(this, "未就绪", "云凭据未导入。");
            return;
        }

        SettingsBtn.IsEnabled = false;
        BusyRing.Visibility = Visibility.Visible;
        try
        {
            ComputerPowerCard.CardSwitchState st;
            try
            {
                using var cloud = new MiCloudClient(cred);
                var card = new ComputerPowerCard(cloud, dev.Did);
                st = await card.GetSwitchesAsync();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "读取失败", $"无法读取设备开关：{ex.Message}");
                return;
            }

            var dialog = new SettingsDialog(
                dev.DisplayName, st,
                async (kind, value) =>
                {
                    using var cloud = new MiCloudClient(cred);
                    var card = new ComputerPowerCard(cloud, dev.Did);
                    await card.SetSwitchAsync(kind, value);
                    OperationLog.Append(new OperationEntry
                    {
                        Source = "gui",
                        DeviceName = dev.DisplayName,
                        Action = $"switch:{kind}",
                        Did = dev.Did,
                        Ok = true,
                        Message = value ? "on" : "off",
                    });
                })
            { Owner = this };
            dialog.ShowDialog();
        }
        finally
        {
            BusyRing.Visibility = Visibility.Hidden;
            SettingsBtn.IsEnabled = true;
        }
    }

    // ═══════════════════════ 自检（--selfcheck） ═══════════════════════

    /// <summary>WPF RenderTargetBitmap 走软件光栅化，断开的 RDP 会话也能出图。</summary>
    public async Task<string> RunSelfCheckAsync(string? pngPath, int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        while (!_firstRefreshDone && sw.Elapsed.TotalSeconds < timeoutSec)
            await Task.Delay(200);

        ShowFromTray();
        await Task.Delay(600); // 等布局稳定

        if (string.IsNullOrEmpty(pngPath))
            pngPath = Path.Combine(Path.GetTempPath(), "pccard-selfcheck.png");

        RootGrid.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualHeight));

        // ★ 离屏渲染：克隆一个未挂窗口的 MainWindow，把实时状态复制进去再光栅化。
        //   直接 Render(已挂窗口的 RootGrid) 依赖窗口合成目标，锁屏/断开 RDP 时会得到
        //   全透明图（实测）；未挂载的视觉树由 RenderTargetBitmap 直接渲染，稳定出图。
        BitmapSource bitmap;
        try
        {
            var clone = new MainWindow(renderOnly: true);
            clone.CopyVisualStateFrom(this);
            var root = (FrameworkElement)clone.Content;
            clone.Content = null; // 断开与克隆窗口的父子关系，才能独立 Measure/Arrange/Render
            root.Width = w;
            root.Height = h;
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            var offscreen = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            offscreen.Render(root);
            bitmap = offscreen;
        }
        catch (Exception ex)
        {
            Log.Warn($"离屏渲染失败，回退窗口渲染：{ex.Message}");
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(RootGrid);
            bitmap = rtb;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(pngPath))
            encoder.Save(fs);

        using var p = Process.GetCurrentProcess();
        var lines = new List<string>
        {
            "SELFCHECK-OK",
            $"png: {pngPath}",
            $"render: {w}x{h}",
            $"session: {p.SessionId}",
            $"memoryMB: {p.WorkingSet64 / 1048576}",
            $"gcMB: {GC.GetTotalMemory(false) / 1048576}",
            $"windowVisible: {IsVisible}",
            $"rtbProbe: {RtbProbe()}",
            $"stateText: {BigStateText.Text}",
            $"footer: {FooterText.Text}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>内建 RTB 探针：渲染 100×100 红块并统计非零字节（诊断"渲染管线是否工作"）。</summary>
    private static string RtbProbe()
    {
        try
        {
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 100));
            var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var pixels = new byte[100 * 100 * 4];
            rtb.CopyPixels(pixels, 400, 0);
            var nz = 0;
            foreach (var b in pixels)
            {
                if (b != 0) nz++;
            }
            return $"{nz}/40000 nonzero";
        }
        catch (Exception ex)
        {
            return "probe failed: " + ex.Message;
        }
    }

    // ═══════════════════════ 小工具 ═══════════════════════

    private void SetBusyUi(bool busy)
    {
        try
        {
            BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            RefreshBtn.IsEnabled = !busy;
        }
        catch
        {
            // 尽力而为
        }
        ApplyButtonStates();
    }

    private void SetFooter(string text, bool warn)
    {
        FooterText.Text = (warn ? "⚠ " : "") + text;
        FooterText.Foreground = Res(warn ? "PcWarn" : "PcTextTertiary");
        FooterDot.Fill = Res(warn ? "PcWarn" : "PcSuccess");
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>async 统一安全网：未捕获异常 → 记日志 + 页脚提示。</summary>
    private async void SafeRun(string name, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error($"「{name}」未处理异常", ex);
            try
            {
                SetFooter($"{name}失败：{ex.Message}", warn: true);
            }
            catch
            {
                // 尽力而为
            }
        }
    }

    private static string FormatStamp(string? iso)
        => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : (iso ?? "—");

    [DllImport("shell32.dll")]
    private static extern bool IsUserAnAdmin();

    private static bool IsElevated()
    {
        try
        {
            return IsUserAnAdmin();
        }
        catch
        {
            return false;
        }
    }
}

// ═══════════════════════ 列表行 ViewModel ═══════════════════════

public sealed class TaskItemVm : INotifyPropertyChanged
{
    private readonly Action<TaskItemVm>? _onToggle;

    public TaskItemVm(ScheduleTask model, Action<TaskItemVm>? onToggle)
    {
        Model = model;
        _onToggle = onToggle;
    }

    public ScheduleTask Model { get; }
    public string Id => Model.Id;
    public string Title => string.IsNullOrWhiteSpace(Model.Name) ? "（未命名）" : Model.Name;
    public string RuleAndAction => $"{DescribeTrigger(Model)} · {Model.Action.ToDisplayName()}";

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            Raise(nameof(Enabled));
            _onToggle?.Invoke(this);
        }
    }

    public string ResultPillText
    {
        get
        {
            if (Model.LastResult is null or "") return "尚未运行";
            var when = FormatUtcLocal(Model.LastRunUtc);
            if (Model.LastResult == "ok") return string.IsNullOrEmpty(when) ? "成功" : $"成功 · {when}";
            var reason = string.IsNullOrEmpty(Model.LastMessage) ? "失败"
                : "失败：" + (Model.LastMessage.Length > 24 ? Model.LastMessage[..24] + "…" : Model.LastMessage);
            return string.IsNullOrEmpty(when) ? reason : $"{reason} · {when}";
        }
    }

    public Brush ResultPillBrush => (Brush)Application.Current.Resources[Model.LastResult switch
    {
        "ok" => "PcSuccessSoft",
        "fail" => "PcDangerSoft",
        _ => "PcNeutralSoft",
    }];

    public Brush ResultPillTextBrush => (Brush)Application.Current.Resources[Model.LastResult switch
    {
        "ok" => "PcSuccess",
        "fail" => "PcDanger",
        _ => "PcTextSecondary",
    }];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatUtcLocal(string? utcIso)
    {
        if (string.IsNullOrEmpty(utcIso)) return "";
        return DateTime.TryParse(utcIso, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var t)
            ? t.ToLocalTime().ToString("MM-dd HH:mm")
            : "";
    }

    private static string DescribeTrigger(ScheduleTask t) => t.Trigger switch
    {
        TriggerKind.Daily => $"每天 {t.Time}",
        TriggerKind.Weekly => $"每周{string.Join("、", t.DaysOfWeek.Select(DayShort))} {t.Time}",
        TriggerKind.Once => $"单次 {(t.OnceAt ?? "").Replace('T', ' ')}",
        _ => t.Trigger.ToString(),
    };

    private static string DayShort(string day) => day switch
    {
        "Monday" => "一",
        "Tuesday" => "二",
        "Wednesday" => "三",
        "Thursday" => "四",
        "Friday" => "五",
        "Saturday" => "六",
        "Sunday" => "日",
        _ => day,
    };
}

public sealed class LogItemVm
{
    public LogItemVm(OperationEntry e, IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        TimeFull = e.Time.Replace('T', ' ');
        SourceLabel = e.Source.StartsWith("task:", StringComparison.Ordinal) ? "定时" : "手动";

        // 设备名：新日志直接有；旧日志按 Did 从当前设备清单回填；再不行显示 did
        DeviceFull = !string.IsNullOrWhiteSpace(e.DeviceName)
            ? e.DeviceName!
            : (deviceNames is not null && deviceNames.TryGetValue(e.Did, out var n)
                ? n
                : (string.IsNullOrEmpty(e.Did) ? "—" : $"did:{e.Did}"));
        DeviceLabel = ShortDeviceName(DeviceFull);

        ActionText = WireToChinese(e.Action);
        Ok = e.Ok;
        ErrorText = e.Message ?? "";
    }

    public string TimeFull { get; }
    public string DeviceLabel { get; }
    public string DeviceFull { get; }
    public string SourceLabel { get; }
    public string ActionText { get; }
    public bool Ok { get; }
    public string ErrorText { get; }

    /// <summary>列宽有限，去掉统一后缀「的电脑开机卡」，只留机器名。</summary>
    public static string ShortDeviceName(string name)
        => name.EndsWith("的电脑开机卡", StringComparison.Ordinal)
            ? name[..^"的电脑开机卡".Length]
            : name;

    public string ResultText => Ok
        ? "成功"
        : "失败" + (string.IsNullOrEmpty(ErrorText) ? "" : "：" + (ErrorText.Length > 48 ? ErrorText[..48] + "…" : ErrorText));

    public Brush ResultBrush => (Brush)Application.Current.Resources[Ok ? "PcSuccess" : "PcDanger"];

    private static string WireToChinese(string wire) => wire switch
    {
        "power-on" => "开机",
        "power-off" => "关机",
        "reboot" => "重启",
        "force-reboot" => "强制重启",
        "force-power-off" => "强制关机",
        _ => wire.StartsWith("switch:") ? "设置" : wire,
    };
}
