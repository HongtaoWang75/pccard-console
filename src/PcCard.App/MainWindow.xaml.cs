using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PcCard.App.Activation;
using PcCard.Core;
using PcCard.Core.Cloud;
using PcCard.Core.Devices;
using PcCard.Core.Logging;
using PcCard.Core.Models;
using PcCard.Core.Probe;
using PcCard.Core.Scheduling;
using PcCard.Core.Storage;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PcCard.App;

public sealed partial class MainWindow : Window
{
    private static readonly FileLog Log = new("gui");

    private readonly DispatcherTimer _timer = new();
    private readonly ObservableCollection<TaskItemVm> _tasks = new();
    private readonly ObservableCollection<LogItemVm> _logs = new();

    private DateTime _fastUntil = DateTime.MinValue;

    // ── 动作后置（点击后置禁用）──
    // 指令发出后全部动作键保持禁用，直到下一次刷新确认状态（或超时）才恢复。
    // ★ 这不是纯视觉优化：piid1 是"按键动作"语义，连点 = 连按电源键，会打断刚启动的机器。
    private ActionKind? _pendingKind;
    private DateTime _pendingMinUntil;
    private DateTime _pendingDeadline;
    private DateTime _footerHoldUntil; // 动作相关提示在此期间不被常规页脚覆盖
    private bool _busy;
    private bool _firstRefreshDone;
    private bool _reallyClosing;
    private bool _loadingTasks;
    private bool _loadingDevices;
    private string? _lastDeviceType;

    // 三个主操作键的可用性（规范 §4：当前可执行的那个才可用）
    private bool _canOn = true;
    private bool _canOff = true;
    private bool _canReboot;

    public MainWindow()
    {
        InitializeComponent();
        Title = "开机卡控制台";

        AppWindow.Resize(new SizeInt32(980, 700));
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            AppWindow.Move(new PointInt32(
                Math.Max(0, (area.Width - 980) / 2),
                Math.Max(0, (area.Height - 700) / 3)));
        }
        catch
        {
            // 取不到显示器信息就用默认位置
        }

        AppWindow.Closing += OnAppWindowClosing;
        TasksList.ItemsSource = _tasks;
        LogsList.ItemsSource = _logs;

        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        VersionText.Text = $"PcCard v{AppPaths.Version}";
        if (!IsElevated())
            TasksWarnText.Visibility = Visibility.Visible;

        RootGrid.Loaded += (_, _) =>
        {
            LoadDeviceCombo();
            LoadTasks();
            LoadLogs();
            ApplyButtonStates();
            _ = RefreshAsync(manual: true);
        };
    }

    // ═══════════════════════ 对外（App/托盘 调用） ═══════════════════════

    public void ShowFromTray()
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch (Exception ex)
        {
            Log.Warn($"ShowFromTray 失败：{ex.Message}");
        }
    }

    public void ForceClose()
    {
        _reallyClosing = true;
        Close();
    }

    public void HandleActivation(string raw)
    {
        var args = StartupArgs.FromActivationMessage(raw);
        if (args.SelfCheck)
        {
            _ = Task.Run(async () =>
            {
                string text;
                try
                {
                    text = await RunSelfCheckAsync(args.PngPath, args.TimeoutSec);
                }
                catch (Exception ex)
                {
                    text = "SELFCHECK-FAIL: " + ex;
                }
                Log.Info(text.Replace(Environment.NewLine, " | "));
                try
                {
                    if (!string.IsNullOrEmpty(args.PngPath))
                        File.WriteAllText(args.PngPath + ".txt", text, System.Text.Encoding.UTF8);
                }
                catch
                {
                    // 尽力而为
                }
            });
        }
        else
        {
            ShowFromTray();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClosing) return;
        args.Cancel = true; // 关窗 = 隐藏进托盘，进程常驻
        sender.Hide();
    }

    // ═══════════════════════ 状态刷新 ═══════════════════════

    public async Task RefreshAsync(bool manual = false)
    {
        if (_busy) return;

        if (DateTime.UtcNow > _fastUntil && _timer.Interval != TimeSpan.FromSeconds(20))
            _timer.Interval = TimeSpan.FromSeconds(20);

        _busy = true;
        SetBusyUi(true);
        try
        {
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

                // ★ 名称/IP/token/在线状态跟云端自动同步（米家里改名这边立刻跟上）
                if (ConfigStore.SyncFromCloud(list))
                {
                    LoadDeviceCombo();                       // 重建下拉框（内部有守卫，无幽灵回写）
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
            if (_pendingKind is ActionKind pk)
            {
                var now = DateTime.UtcNow;
                var minDone = now >= _pendingMinUntil;
                var satisfied = pk switch
                {
                    ActionKind.PowerOn => state.PowerOn == true ? true : state.PowerOn == false ? false : (bool?)null,
                    ActionKind.PowerOff or ActionKind.ForcePowerOff
                        => state.PowerOn == false ? true : state.PowerOn == true ? false : (bool?)null,
                    _ => null, // 重启类：重启前后都是"开机"，云端状态信号不可靠 → 固定等待
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
                else
                {
                    var left = (int)Math.Max(0, (_pendingDeadline - now).TotalSeconds);
                    SetFooter($"已发送「{pk.ToDisplayName()}」指令 · 等待状态更新…（最长 {left}s）", warn: false);
                    _footerHoldUntil = DateTime.UtcNow.AddSeconds(10);
                }
            }

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
            _busy = false;
            SetBusyUi(false);
            _firstRefreshDone = true;
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
            _canOn = _canOff = _canReboot = false;  // 卡片离线：全禁
        }
        else if (state.PowerOn == true)
        {
            BigStateText.Text = "已开机";
            BigStateText.Foreground = Res("PcOn");
            StatusStripe.Fill = Res("PcAccent");
            _canOn = false;                         // 已开机 →「开机」无意义
            _canOff = true;
            _canReboot = true;                      // 重启/强制类仅开机态可用（与插件一致）
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
            _canOn = _canOff = true;                // 未知：开关两键都可点
            _canReboot = false;                     // 重启要求确知开机态
        }

        var lastOp = OperationLog.ReadRecent(1).FirstOrDefault();
        var lastText = lastOp is null ? "无记录" : lastOp.Time.Replace('T', ' ');
        MetaText.Text = $"设备类型 {state.DeviceType ?? _lastDeviceType ?? "—"} · " +
                        $"旁路信号 {(tunnel ? "疑似开机" : "无")} · 上次操作 {lastText}";

        ApplyButtonStates();
    }

    private void ApplyButtonStates()
    {
        // 主色填充由作用域资源固定（规范色 #017D7B），
        // 可用性只用 IsEnabled 表达：不可用 = 静音的禁用外观（规范 §3/§4）
        // ★ 动作后置期间（_pendingKind 非空）全部动作键一律禁用 —— 防连按
        var gate = !_busy && _pendingKind is null;
        PowerOnBtn.IsEnabled = _canOn && gate;
        PowerOffBtn.IsEnabled = _canOff && gate;
        RebootBtn.IsEnabled = _canReboot && gate;
    }

    // ═══════════════════════ 开关机操作 ═══════════════════════

    private bool ForceMode => ForceCheck.IsChecked == true;

    private async void OnPowerOnClick(object sender, RoutedEventArgs e) => await DoActionAsync(ActionKind.PowerOn);

    private async void OnPowerOffClick(object sender, RoutedEventArgs e)
        => await DoActionAsync(ForceMode ? ActionKind.ForcePowerOff : ActionKind.PowerOff);

    private async void OnRebootClick(object sender, RoutedEventArgs e)
        => await DoActionAsync(ForceMode ? ActionKind.ForceReboot : ActionKind.Reboot);

    /// <summary>强制勾选切换时同步按钮文案（关机/重启 → 强制关机/强制重启）。</summary>
    private void OnForceChanged(object sender, RoutedEventArgs e)
    {
        PowerOffLabel.Text = ForceMode ? "强制关机" : "关机";
        RebootLabel.Text = ForceMode ? "强制重启" : "重启";
    }

    private async Task DoActionAsync(ActionKind kind)
    {
        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            await ShowInfoAsync("未就绪", "还没有导入设备清单（pccard device import devices.json）。");
            return;
        }

        // 需要二次确认的动作（重启/强制类走确认；普通重启 = 一键直接执行）
        var (confirmTitle, confirmText, confirmOk) = kind switch
        {
            ActionKind.PowerOff => ("关闭电脑", "确定要关闭电脑吗？\n将模拟按下电源键，系统会正常关机。", "关机"),
            ActionKind.ForcePowerOff => ("强制关机",
                "确定要强制关机吗？\n将模拟长按电源键强制断电 —— 未保存的数据会丢失！", "强制关机"),
            ActionKind.ForceReboot => ("强制重启",
                "确定要强制重启吗？\n将模拟长按电源键强制断电后重启 —— 未保存的数据会丢失！", "强制重启"),
            _ => (null, null, null),
        };
        if (confirmTitle is not null)
        {
            var confirm = new ContentDialog
            {
                Title = confirmTitle,
                Content = new TextBlock { Text = confirmText, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirmOk,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var cred = CredentialVault.Load();
        if (cred is null || !cred.IsComplete)
        {
            await ShowInfoAsync("未就绪", "云凭据未导入。");
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
                    Action = kind.ToWireName(),
                    Did = dev.Did,
                    Ok = true,
                    DurationMs = sw.ElapsedMilliseconds,
                });
                SetFooter($"已发送「{kind.ToDisplayName()}」指令 {DateTime.Now:HH:mm:ss}（等待状态确认）", warn: false);
                // 进入动作后置：按钮保持禁用，直到下一次刷新确认状态（重启类固定等待）
                var isReboot = kind is ActionKind.Reboot or ActionKind.ForceReboot;
                _pendingKind = kind;
                _pendingMinUntil = DateTime.UtcNow.AddSeconds(isReboot ? 60 : 12);
                _pendingDeadline = DateTime.UtcNow.AddSeconds(isReboot ? 90 : 120);
            }
            catch (Exception ex)
            {
                OperationLog.Append(new OperationEntry
                {
                    Source = "gui",
                    Action = kind.ToWireName(),
                    Did = dev.Did,
                    Ok = false,
                    Message = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds,
                });
                await ShowInfoAsync("操作失败", ex.Message);
            }

            // 指令后进入快刷窗口（卡片动作有延迟，云侧状态要几秒才更新）
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
        var (ok, msg) = WindowsTaskScheduler.CreateOrUpdate(vm.Model, AppPaths.CliExe, DateTime.Now);
        if (ok)
        {
            TaskStore.Upsert(vm.Model);
            SetFooter($"任务「{vm.Model.Name}」已{(vm.Model.Enabled ? "启用" : "停用")}", warn: false);
        }
        else
        {
            SetFooter($"计划任务更新失败：{msg}", warn: true);
            _loadingTasks = true;
            vm.Enabled = !vm.Enabled; // 回滚开关（不再触发写入）
            _loadingTasks = false;
        }
    }

    private async void OnAddTaskClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "任务名", Text = "定时开机" };
        var actionBox = new ComboBox { Header = "动作", SelectedIndex = 0, MinWidth = 150 };
        actionBox.Items.Add("开机");
        actionBox.Items.Add("关机");
        actionBox.Items.Add("重启");
        var forceBox = new CheckBox
        {
            Content = "强制（仅关机/重启生效）",
            IsEnabled = false,
            FontSize = 13,
        };
        actionBox.SelectionChanged += (_, _) => forceBox.IsEnabled = actionBox.SelectedIndex != 0;
        var triggerBox = new ComboBox { Header = "触发方式", SelectedIndex = 0, MinWidth = 150 };
        triggerBox.Items.Add("每天");
        triggerBox.Items.Add("每周");
        triggerBox.Items.Add("单次");
        var timeBox = new TextBox { Header = "时间（HH:mm）", Text = "08:00" };
        var onceBox = new TextBox
        {
            Header = "单次时间（yyyy-MM-ddTHH:mm）",
            Text = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm"),
            Visibility = Visibility.Collapsed,
        };

        var dayChecks = new List<(CheckBox Box, string DayName)>();
        var daysPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        string[] dayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        string[] dayLabels = { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < dayNames.Length; i++)
        {
            var cb = new CheckBox { Content = dayLabels[i] };
            dayChecks.Add((cb, dayNames[i]));
            daysPanel.Children.Add(cb);
        }

        triggerBox.SelectionChanged += (_, _) =>
        {
            var idx = triggerBox.SelectedIndex;
            timeBox.Visibility = idx == 2 ? Visibility.Collapsed : Visibility.Visible;
            daysPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            onceBox.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        };

        var form = new StackPanel { Spacing = 10, Width = 380 };
        form.Children.Add(nameBox);
        form.Children.Add(actionBox);
        form.Children.Add(forceBox);
        form.Children.Add(triggerBox);
        form.Children.Add(timeBox);
        form.Children.Add(daysPanel);
        form.Children.Add(onceBox);

        var dialog = new ContentDialog
        {
            Title = "新增定时任务",
            Content = form,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            await ShowInfoAsync("未就绪", "还没有导入设备清单。");
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? "未命名任务" : nameBox.Text.Trim();
        var force = forceBox.IsChecked == true;
        var action = actionBox.SelectedIndex switch
        {
            0 => ActionKind.PowerOn,
            1 => force ? ActionKind.ForcePowerOff : ActionKind.PowerOff,
            _ => force ? ActionKind.ForceReboot : ActionKind.Reboot,
        };
        var task = new ScheduleTask
        {
            Name = name,
            Action = action,
            DeviceDid = dev.Did,
            Time = timeBox.Text.Trim(),
        };
        switch (triggerBox.SelectedIndex)
        {
            case 1:
                task.Trigger = TriggerKind.Weekly;
                task.DaysOfWeek = dayChecks.Where(d => d.Box.IsChecked == true).Select(d => d.DayName).ToList();
                if (task.DaysOfWeek.Count == 0)
                {
                    await ShowInfoAsync("参数有误", "「每周」至少选一个星期数。");
                    return;
                }
                break;
            case 2:
                task.Trigger = TriggerKind.Once;
                task.OnceAt = onceBox.Text.Trim();
                break;
            default:
                task.Trigger = TriggerKind.Daily;
                break;
        }

        var (ok, msg) = WindowsTaskScheduler.CreateOrUpdate(task, AppPaths.CliExe, DateTime.Now);
        if (!ok)
        {
            await ShowInfoAsync("创建失败", $"schtasks 返回：\n{msg}");
            return;
        }
        TaskStore.Upsert(task);
        LoadTasks();
        SetFooter($"任务「{name}」已创建，下次触发 {TaskXmlBuilder.NextBoundary(task, DateTime.Now):MM-dd HH:mm}", warn: false);
    }

    private async void OnTaskDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var t = TaskStore.Find(id);
        if (t is null) return;

        var dialog = new ContentDialog
        {
            Title = "删除定时任务",
            Content = new TextBlock { Text = $"确定删除「{t.Name}」吗？", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        WindowsTaskScheduler.Delete(t);
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

            // ★ 初始化只用「存储的原值」精确匹配，绝不用 GetSelectedDevice 的兜底 ——
            //   曾经的写法（兜底选第一台）配上事件回写形成自污染环：任何一次读取失配
            //   都会把「第一台设备」悄悄写回 state.json（阿里云上因此被换成离线卡）。
            var stored = ConfigStore.LoadState().SelectedDid;
            var item = stored is null
                ? doc.Devices.FirstOrDefault()
                : doc.Devices.FirstOrDefault(d => d.Did == stored);

            if (item is not null && item.Did != stored)
            {
                // 首次使用 / 存储的设备已不在清单：显式落盘一次（此后事件回读即为稳定态）
                ConfigStore.SetSelectedDid(item.Did);
            }
            DeviceCombo.SelectedItem = item;
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices) return;
        if (DeviceCombo.SelectedItem is DeviceRecord d)
        {
            // 只在真实变化时回写（初始化引发的幽灵事件一律不落盘）
            if (ConfigStore.LoadState().SelectedDid == d.Did) return;
            ConfigStore.SetSelectedDid(d.Did);
            _pendingKind = null; // 换设备即放弃旧设备的等待态
            await RefreshAsync(manual: true);
        }
    }

    private void LoadLogs()
    {
        _logs.Clear();
        foreach (var entry in OperationLog.ReadRecent(100))
            _logs.Add(new LogItemVm(entry));
        LogsEmptyText.Visibility = _logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync(manual: true);

    // ═══════════════════════ 设备设置（童锁 / 指示灯 / 通电自动开机）═══════════════════════

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await ShowSettingsDialogAsync();

    private async Task ShowSettingsDialogAsync()
    {
        var dev = ConfigStore.GetSelectedDevice();
        if (dev is null)
        {
            await ShowInfoAsync("未就绪", "还没有导入设备清单。");
            return;
        }
        var cred = CredentialVault.Load();
        if (cred is null || !cred.IsComplete)
        {
            await ShowInfoAsync("未就绪", "云凭据未导入。");
            return;
        }

        ComputerPowerCard.CardSwitchState st;
        try
        {
            using var cloud = new MiCloudClient(cred);
            var card = new ComputerPowerCard(cloud, dev.Did);
            st = await card.GetSwitchesAsync();
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("读取失败", $"无法读取设备开关：{ex.Message}");
            return;
        }

        var loading = true; // 载入初值期间吞掉 Toggled
        var panel = new StackPanel { Spacing = 16, Width = 420 };

        void AddRow(string title, string subtitle, ComputerPowerCard.CardSwitch kind, bool? value)
        {
            var ts = new ToggleSwitch
            {
                OffContent = "",
                OnContent = "",
                MinWidth = 0,
                IsOn = value == true,
                IsEnabled = value is not null,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var texts = new StackPanel { Spacing = 2 };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 14 });
            texts.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Res("PcTextTertiary"),
                TextWrapping = TextWrapping.Wrap,
            });
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 0);
            Grid.SetColumn(ts, 1);
            row.Children.Add(texts);
            row.Children.Add(ts);
            panel.Children.Add(row);

            var known = value; // 最近一次已确认值
            ts.Toggled += async (_, _) =>
            {
                if (loading || known is null || ts.IsOn == known) return;
                var target = ts.IsOn;
                ts.IsEnabled = false;
                try
                {
                    using var cloud = new MiCloudClient(cred);
                    var card = new ComputerPowerCard(cloud, dev.Did);
                    await card.SetSwitchAsync(kind, target);
                    known = target;
                    OperationLog.Append(new OperationEntry
                    {
                        Source = "gui",
                        Action = $"switch:{kind}",
                        Did = dev.Did,
                        Ok = true,
                        Message = target ? "on" : "off",
                    });
                    SetFooter($"设置「{title}」已{(target ? "打开" : "关闭")}", warn: false);
                }
                catch (Exception ex)
                {
                    loading = true;
                    ts.IsOn = known.Value; // 回滚开关
                    loading = false;
                    OperationLog.Append(new OperationEntry
                    {
                        Source = "gui",
                        Action = $"switch:{kind}",
                        Did = dev.Did,
                        Ok = false,
                        Message = ex.Message,
                    });
                    SetFooter($"设置「{title}」失败：{ex.Message}", warn: true);
                }
                finally
                {
                    ts.IsEnabled = true;
                }
            };
        }

        AddRow("童锁", "打开后机箱电源按钮不可用（云端控制不受影响）",
            ComputerPowerCard.CardSwitch.ChildLock, st.ChildLock);
        AddRow("指示灯", "关闭后设备指示灯熄灭",
            ComputerPowerCard.CardSwitch.Indicator, st.Indicator);
        AddRow("通电自动开机", "设备重新通电后自动给电脑开机（适合停电恢复场景）",
            ComputerPowerCard.CardSwitch.AutoBoot, st.AutoBoot);
        loading = false;

        var dlg = new ContentDialog
        {
            Title = $"设备设置 · {dev.DisplayName}",
            Content = panel,
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ═══════════════════════ 自检（--selfcheck） ═══════════════════════

    /// <summary>
    /// 自渲染截图（RenderTargetBitmap 走 XAML 合成结果，断开态 RDP 会话也能出图）
    /// + 关键诊断信息。
    /// </summary>
    public async Task<string> RunSelfCheckAsync(string? pngPath, int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        while (!_firstRefreshDone && sw.Elapsed.TotalSeconds < timeoutSec)
            await Task.Delay(200);

        ShowFromTray(); // 确保窗口处于显示态（合成管线需要）
        await Task.Delay(700); // 等布局稳定

        if (string.IsNullOrEmpty(pngPath))
            pngPath = Path.Combine(Path.GetTempPath(), "pccard-selfcheck.png");

        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(RootGrid);
        var pixels = await rtb.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = DataReader.FromBuffer(pixels))
        {
            reader.ReadBytes(bytes);
        }
        using (var ms = new MemoryStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            File.WriteAllBytes(pngPath, ms.ToArray());
        }

        using var p = Process.GetCurrentProcess();
        var lines = new List<string>
        {
            "SELFCHECK-OK",
            $"png: {pngPath}",
            $"render: {rtb.PixelWidth}x{rtb.PixelHeight}",
            $"session: {p.SessionId}",
            $"memoryMB: {p.WorkingSet64 / 1048576}",
            $"gcMB: {GC.GetTotalMemory(false) / 1048576}",
            $"windowVisible: {AppWindow.IsVisible}",
            $"stateText: {BigStateText.Text}",
            $"footer: {FooterText.Text}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    // ═══════════════════════ 小工具 ═══════════════════════

    private void SetBusyUi(bool busy)
    {
        BusyRing.IsActive = busy;
        RefreshBtn.IsEnabled = !busy;
        ApplyButtonStates();
    }

    private void SetFooter(string text, bool warn)
    {
        FooterText.Text = (warn ? "⚠ " : "") + text;
        FooterText.Foreground = Res(warn ? "PcWarn" : "PcTextTertiary");
        FooterDot.Fill = Res(warn ? "PcWarn" : "PcSuccess");
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>ISO 时间戳 → 友好格式（去 T / 去时区尾巴）。</summary>
    private static string FormatStamp(string? iso)
        => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : (iso ?? "—");

    private async Task ShowInfoAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsUserAnAdmin();

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
        return DateTime.TryParse(utcIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
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
    public LogItemVm(OperationEntry e)
    {
        TimeFull = e.Time.Replace('T', ' ');
        SourceLabel = e.Source.StartsWith("task:", StringComparison.Ordinal) ? "定时" : "手动";
        ActionText = WireToChinese(e.Action);
        Ok = e.Ok;
        ErrorText = e.Message ?? "";
    }

    public string TimeFull { get; }
    public string SourceLabel { get; }
    public string ActionText { get; }
    public bool Ok { get; }
    public string ErrorText { get; }

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
        _ => wire,
    };
}
