using System.Diagnostics;
using System.Text.Json;
using PcCard.Core;
using PcCard.Core.Cloud;
using PcCard.Core.Devices;
using PcCard.Core.Logging;
using PcCard.Core.Models;
using PcCard.Core.Probe;
using PcCard.Core.Scheduling;
using PcCard.Core.Storage;

namespace PcCard.Cli;

/// <summary>
/// pccard.exe —— 无 UI 的任务执行器 / 命令行。
/// 两副面孔：
///   1) schtasks 定时任务的入口（<c>pccard task run &lt;id&gt;</c>，SYSTEM / Session 0）
///   2) 运维与调试命令行（doctor / status / power / credential / device …）
/// </summary>
public static class Program
{
    private static readonly FileLog Log = new("cli");

    public static int Main(string[] args)
    {
        SetupOutput();
        if (args.Contains("--console")) ConsoleAttach.TryAttach();
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (CliError ex)
        {
            Console.Error.WriteLine($"[x] {ex.Message}");
            Log.Error(ex.Message);
            return ex.Code;
        }
        catch (MiCloudException ex)
        {
            var code = ex.Kind switch
            {
                MiCloudErrorKind.AuthInvalid => 6,
                MiCloudErrorKind.DeviceOffline => 5,
                MiCloudErrorKind.NetError => 7,
                _ => 4,
            };
            Console.Error.WriteLine($"[x] 云接口错误（{ex.Kind}, code={ex.Code}）：{ex.Message}");
            Log.Error($"cloud error {ex.Kind} code={ex.Code}: {ex.Message}");
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[x] {ex.GetType().Name}: {ex.Message}");
            Log.Error("unhandled", ex);
            return 1;
        }
    }

    /// <summary>
    /// 显式接管 stdout/stderr 为 UTF-8。
    /// ★ 不能只设 Console.OutputEncoding：WinExe 无控制台句柄时那个 setter 会静默失败，
    ///   被重定向的输出会落回系统 ANSI（中文变乱码）。
    /// </summary>
    private static void SetupOutput()
    {
        try
        {
            var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), enc) { AutoFlush = true });
        }
        catch
        {
            // 极端情况下拿不到标准流也别崩
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 2;
        }
        var rest = args.Skip(1).ToArray();
        return args[0].ToLowerInvariant() switch
        {
            "version" => CmdVersion(),
            "doctor" => await CmdDoctorAsync().ConfigureAwait(false),
            "credential" or "cred" => CmdCredential(rest),
            "device" => await CmdDeviceAsync(rest).ConfigureAwait(false),
            "config" => CmdConfig(rest),
            "status" => await CmdStatusAsync(rest).ConfigureAwait(false),
            "power" => await CmdPowerAsync(rest).ConfigureAwait(false),
            "switches" => await CmdSwitchesAsync(rest).ConfigureAwait(false),
            "task" => await CmdTaskAsync(rest).ConfigureAwait(false),
            "help" or "--help" or "-h" => PrintHelpReturn(),
            _ => Unknown(args[0]),
        };
    }

    // ─────────────────────────── 简单命令 ───────────────────────────

    private static int CmdVersion()
    {
        Console.WriteLine($"pccard {AppPaths.Version}");
        Console.WriteLine($"exe        : {Environment.ProcessPath}");
        Console.WriteLine($"data root  : {AppPaths.DataRoot}");
        return 0;
    }

    private static int CmdCredential(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (sub)
        {
            case "import":
            {
                string json;
                if (args.Contains("--stdin"))
                {
                    json = Console.In.ReadToEnd();
                }
                else
                {
                    var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                               ?? throw new CliError(2, "用法：pccard credential import <credentials.json> 或 --stdin");
                    json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                }
                var cred = JsonSerializer.Deserialize<Credentials>(json, JsonFile.Options)
                           ?? throw new CliError(2, "credentials.json 解析失败");
                if (!cred.IsComplete)
                    throw new CliError(2, "凭据缺字段（需要 userId / ssecurity / serviceToken）");
                CredentialVault.Save(cred);
                Console.WriteLine($"[✓] 凭据已加密保存（userId={cred.UserId}, sid={cred.Sid}, source={cred.Source}）");
                return 0;
            }
            case "status":
            {
                if (!CredentialVault.Exists)
                {
                    Console.WriteLine("凭据：未导入");
                    return 3;
                }
                var cred = CredentialVault.Load();
                if (cred is null)
                {
                    Console.WriteLine("凭据：文件存在但解密/解析失败（DPAPI 损坏或跨机器拷贝）");
                    return 3;
                }
                Console.WriteLine($"凭据：已导入  userId={cred.UserId}  sid={cred.Sid}  " +
                                  $"passToken={(string.IsNullOrEmpty(cred.PassToken) ? "无" : "有")}  " +
                                  $"capturedAt={cred.CapturedAt}");
                return 0;
            }
            case "delete":
                CredentialVault.Delete();
                Console.WriteLine("[✓] 凭据已删除");
                return 0;
            default:
                throw new CliError(2, "用法：pccard credential import|status|delete");
        }
    }

    private static async Task<int> CmdDeviceAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        switch (sub)
        {
            case "sync":
            {
                var cred = CredentialVault.Load() ?? throw new CliError(3, "未导入云凭据");
                if (!cred.IsComplete) throw new CliError(3, "云凭据不完整");
                using var cloud = new MiCloudClient(cred);
                var list = await cloud.GetDeviceListAsync().ConfigureAwait(false);
                var changed = ConfigStore.SyncFromCloud(list);
                Console.WriteLine($"[✓] 云端同步完成（{(changed ? "有更新，已写回 devices.json" : "无变化")}）");
                foreach (var d in ConfigStore.LoadDevices()?.Devices ?? new List<DeviceRecord>())
                {
                    var cd = list.FirstOrDefault(x => x.Did == d.Did);
                    Console.WriteLine($"    did={d.Did}  {d.DisplayName}  online={cd?.IsOnline}  ip={cd?.LocalIp ?? d.LocalIp ?? "—"}");
                }
                return 0;
            }

            case "import":
            {
                var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                           ?? throw new CliError(2, "用法：pccard device import <devices.json>");
                var doc = ConfigStore.ImportDevices(path);
                Console.WriteLine($"[✓] 已导入 {doc.Devices.Count} 台设备到 {AppPaths.DevicesPath}");
                foreach (var d in doc.Devices)
                    Console.WriteLine($"    did={d.Did}  {d.DisplayName}  model={d.Model}");
                return 0;
            }
            case "list":
            {
                var doc = ConfigStore.LoadDevices();
                if (doc is null || doc.Devices.Count == 0)
                {
                    Console.WriteLine("设备清单为空（pccard device import <devices.json>）");
                    return 3;
                }
                var selected = ConfigStore.GetSelectedDevice();
                foreach (var d in doc.Devices)
                {
                    var mark = d.Did == selected?.Did ? " ← 当前选中" : "";
                    Console.WriteLine($"    did={d.Did}  {d.DisplayName}  model={d.Model}{mark}");
                }
                return 0;
            }
            case "select":
            {
                var did = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                          ?? throw new CliError(2, "用法：pccard device select <did>");
                var doc = ConfigStore.LoadDevices() ?? throw new CliError(3, "还未导入设备清单");
                if (doc.Devices.All(d => d.Did != did))
                    throw new CliError(3, $"设备清单里没有 did={did}");
                ConfigStore.SetSelectedDid(did);
                Console.WriteLine($"[✓] 已选中 did={did}");
                return 0;
            }
            default:
                throw new CliError(2, "用法：pccard device import|list|select");
        }
    }

    private static int CmdConfig(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        if (sub == "tunnel")
        {
            var ep = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                     ?? throw new CliError(2, "用法：pccard config tunnel <host:port>");
            var state = ConfigStore.LoadState();
            state.TunnelEndpoint = ep;
            ConfigStore.SaveState(state);
            Console.WriteLine($"[✓] 旁路探测端点已设为 {ep}");
            return 0;
        }
        if (sub is null)
        {
            var state = ConfigStore.LoadState();
            Console.WriteLine($"选中设备    : {state.SelectedDid ?? "(未选)"}");
            Console.WriteLine($"旁路端点    : {state.TunnelEndpoint}");
            return 0;
        }
        throw new CliError(2, "用法：pccard config [tunnel <host:port>]");
    }

    // ─────────────────────────── 需要上云的命令 ───────────────────────────

    private static (MiCloudClient Cloud, ComputerPowerCard Card, DeviceRecord Device) RequireContext()
    {
        var cred = CredentialVault.Load()
                   ?? throw new CliError(3, "未导入云凭据：pccard credential import ...");
        if (!cred.IsComplete)
            throw new CliError(3, "云凭据不完整");
        var dev = ConfigStore.GetSelectedDevice()
                  ?? throw new CliError(3, "未导入设备清单：pccard device import <devices.json>");
        var cloud = new MiCloudClient(cred);
        return (cloud, new ComputerPowerCard(cloud, dev.Did), dev);
    }

    private static async Task<int> CmdStatusAsync(string[] args)
    {
        var (cloud, card, dev) = RequireContext();
        using (cloud)
        {
            var state = await card.GetStateAsync().ConfigureAwait(false);
            bool? cardOnline = null;
            var displayName = dev.DisplayName;
            try
            {
                var list = await cloud.GetDeviceListAsync().ConfigureAwait(false);
                var cloudDev = list.FirstOrDefault(d => d.Did == dev.Did);
                cardOnline = cloudDev?.IsOnline;
                // 名称等元数据以云端为准（米家改名即刻生效），并顺带回写本地目录
                if (!string.IsNullOrEmpty(cloudDev?.Name)) displayName = cloudDev!.Name!;
                ConfigStore.SyncFromCloud(list);
            }
            catch (MiCloudException)
            {
                // 在线状态拿不到不影响主功能
            }
            var tunnelEp = ConfigStore.LoadState().TunnelEndpoint;
            var tunnel = await RdpTunnelProbe.IsEndpointOpenAsync(tunnelEp).ConfigureAwait(false);

            if (args.Contains("--json"))
            {
                var o = new
                {
                    device = new { did = dev.Did, name = displayName, model = dev.Model },
                    cardOnline,
                    powerOn = state.PowerOn,
                    deviceType = state.DeviceType,
                    supportsResetMenu = state.SupportsResetMenu,
                    tunnelSuspectedPcOn = tunnel,
                };
                Console.WriteLine(JsonSerializer.Serialize(o, JsonFile.Options));
                return 0;
            }

            Console.WriteLine($"设备        : {displayName}（did={dev.Did}）");
            Console.WriteLine($"卡片在线    : {Fmt(cardOnline)}");
            Console.WriteLine($"电脑状态    : {(state.PowerOn == true ? "开机" : state.PowerOn == false ? "关机" : "未知")}");
            Console.WriteLine($"设备类型    : {state.DeviceType ?? "未知"}（重启菜单：{(state.SupportsResetMenu ? "支持" : "不支持")}）");
            Console.WriteLine($"旁路信号    : {Fmt(tunnel)}（frp 隧道 {tunnelEp} 探测，仅供参考）");
            return 0;
        }
    }

    private static async Task<int> CmdPowerAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant()
                     ?? throw new CliError(2, "用法：pccard power on|off|reboot [--force]");
        var force = args.Contains("--force");
        var (cloud, card, dev) = RequireContext();
        using (cloud)
        {
            var sw = Stopwatch.StartNew();
            var kind = action switch
            {
                "on" => ActionKind.PowerOn,
                "off" => force ? ActionKind.ForcePowerOff : ActionKind.PowerOff,
                "reboot" => force ? ActionKind.ForceReboot : ActionKind.Reboot,
                _ => throw new CliError(2, "用法：pccard power on|off|reboot [--force]"),
            };
            var ok = false;
            string? err = null;
            try
            {
                await card.ExecuteAsync(kind).ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                throw;
            }
            finally
            {
                OperationLog.Append(new OperationEntry
                {
                    Source = "cli",
                    DeviceName = dev.DisplayName,
                    Action = kind.ToWireName(),
                    Did = dev.Did,
                    Ok = ok,
                    Message = err,
                    DurationMs = sw.ElapsedMilliseconds,
                });
            }
            Console.WriteLine($"[✓] {kind.ToDisplayName()}指令已发送（did={dev.Did}，耗时 {sw.ElapsedMilliseconds} ms）");
            return 0;
        }
    }

    /// <summary>设备设置开关：读取 / 写入（童锁 / 指示灯 / 通电自动开机）。</summary>
    private static async Task<int> CmdSwitchesAsync(string[] args)
    {
        var (cloud, card, dev) = RequireContext();
        using (cloud)
        {
            if (args.Length == 0)
            {
                var st = await card.GetSwitchesAsync().ConfigureAwait(false);
                Console.WriteLine($"设备：{dev.DisplayName}（did={dev.Did}）");
                Console.WriteLine($"    童锁（机箱按钮不可用）: {Fmt(st.ChildLock)}");
                Console.WriteLine($"    指示灯                : {Fmt(st.Indicator)}");
                Console.WriteLine($"    通电自动开机          : {Fmt(st.AutoBoot)}");
                return 0;
            }

            var value = args.ElementAtOrDefault(1)?.ToLowerInvariant() switch
            {
                "on" or "true" or "1" => (bool?)true,
                "off" or "false" or "0" => (bool?)false,
                _ => null,
            };
            if (value is null)
                throw new CliError(2, "用法：pccard switches [childlock|indicator|autoboot] on|off");

            var kind = args[0].ToLowerInvariant() switch
            {
                "childlock" or "child-lock" or "lock" => ComputerPowerCard.CardSwitch.ChildLock,
                "indicator" or "light" => ComputerPowerCard.CardSwitch.Indicator,
                "autoboot" or "auto-boot" or "autopower" => ComputerPowerCard.CardSwitch.AutoBoot,
                _ => throw new CliError(2, "未知开关名：childlock | indicator | autoboot"),
            };

            var sw = Stopwatch.StartNew();
            await card.SetSwitchAsync(kind, value.Value).ConfigureAwait(false);
            var back = await card.GetSwitchesAsync().ConfigureAwait(false);
            var readBack = kind switch
            {
                ComputerPowerCard.CardSwitch.ChildLock => back.ChildLock,
                ComputerPowerCard.CardSwitch.Indicator => back.Indicator,
                _ => back.AutoBoot,
            };
            Console.WriteLine($"[✓] {kind} = {(value.Value ? "on" : "off")}（读回{(readBack == value ? "一致" : $"不一致：{readBack}")}，{sw.ElapsedMilliseconds} ms）");
            return 0;
        }
    }

    // ─────────────────────────── 定时任务 ───────────────────────────

    private static async Task<int> CmdTaskAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "list":
            {
                var tasks = TaskStore.Load();
                if (tasks.Count == 0)
                {
                    Console.WriteLine("（没有定时任务）");
                    return 0;
                }
                foreach (var t in tasks)
                    Console.WriteLine($"    {t.Id}  {(t.Enabled ? "启用" : "停用")}  {t.Name}  " +
                                      $"{t.Action.ToDisplayName()}  {DescribeTrigger(t)}  last={t.LastResult ?? "-"}");
                return 0;
            }

            case "add":
                return CmdTaskAdd(rest);

            case "rm":
            {
                var id = rest.FirstOrDefault() ?? throw new CliError(2, "用法：pccard task rm <id>");
                var t = TaskStore.Find(id) ?? throw new CliError(3, $"任务不存在：{id}");
                var (ok, msg) = WindowsTaskScheduler.Delete(t);
                var removed = TaskStore.Remove(id);
                Console.WriteLine($"[{(ok || !removed ? "✓" : "!")}] 系统任务删除：{msg}；定义删除：{removed}");
                return 0;
            }

            case "run":
                return await CmdTaskRunAsync(rest).ConfigureAwait(false);

            default:
                throw new CliError(2, "用法：pccard task list|add|rm|run");
        }
    }

    private static int CmdTaskAdd(string[] args)
    {
        var name = GetOpt(args, "--name") ?? "未命名任务";
        var actionStr = GetOpt(args, "--action") ?? "power-on";
        var time = GetOpt(args, "--time") ?? "08:00";
        var days = GetOpt(args, "--days")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var onceAt = GetOpt(args, "--once");
        var did = GetOpt(args, "--did");

        var action = actionStr.ToLowerInvariant() switch
        {
            "power-on" or "on" => ActionKind.PowerOn,
            "power-off" or "off" => ActionKind.PowerOff,
            "reboot" => ActionKind.Reboot,
            "force-reboot" => ActionKind.ForceReboot,
            "force-power-off" => ActionKind.ForcePowerOff,
            _ => throw new CliError(2, $"未知动作：{actionStr}"),
        };

        var dev = did is null ? ConfigStore.GetSelectedDevice() : null;
        var targetDid = did ?? dev?.Did ?? throw new CliError(3, "未指定 --did 且未导入/选中设备");

        var task = new ScheduleTask
        {
            Name = name,
            Action = action,
            DeviceDid = targetDid,
            Time = time,
            DaysOfWeek = days ?? new List<string>(),
            OnceAt = onceAt,
            Trigger = onceAt is not null ? TriggerKind.Once
                : days is { Count: > 0 } ? TriggerKind.Weekly
                : TriggerKind.Daily,
            RepeatEveryMinutes = GetOpt(args, "--every") is { } e && int.TryParse(e, out var em) ? em : null,
            RepeatDurationHours = GetOpt(args, "--duration") is { } d && int.TryParse(d, out var dh) ? dh : null,
        };

        var (ok, msg) = WindowsTaskScheduler.CreateOrUpdate(task, AppPaths.CliExe, DateTime.Now);
        if (!ok)
            throw new CliError(1, $"schtasks 创建失败：{msg}");
        TaskStore.Upsert(task);
        Console.WriteLine($"[✓] 任务已创建：{task.Id}  {task.Name}  {DescribeTrigger(task)}");
        Console.WriteLine($"    下次触发：{TaskXmlBuilder.NextBoundary(task, DateTime.Now):yyyy-MM-dd HH:mm:ss}");
        return 0;
    }

    /// <summary>schtasks 的入口：执行一条任务并记录结果（全程无窗口）。</summary>
    private static async Task<int> CmdTaskRunAsync(string[] args)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"))
                 ?? throw new CliError(2, "用法：pccard task run <id>");
        var task = TaskStore.Find(id);
        if (task is null)
        {
            Log.Error($"task run: 未找到任务 {id}");
            return 3;
        }
        if (!task.Enabled)
        {
            Log.Info($"task run: 任务已停用，跳过 {id}");
            return 0;
        }

        Log.Info($"task run start: {task.Id} {task.Name} action={task.Action.ToWireName()} did={task.DeviceDid}");
        var sw = Stopwatch.StartNew();
        var ok = false;
        string? err = null;
        string? errCode = null;
        try
        {
            var cred = CredentialVault.Load() ?? throw new CliError(3, "未导入云凭据");
            using var cloud = new MiCloudClient(cred);
            var card = new ComputerPowerCard(cloud, task.DeviceDid);
            await card.ExecuteAsync(task.Action).ConfigureAwait(false);
            ok = true;
        }
        catch (MiCloudException ex)
        {
            err = $"{ex.Kind}: {ex.Message}";
            errCode = ex.Code.ToString();
            Log.Error($"task run failed: {task.Id} cloud code={ex.Code}", ex);
        }
        catch (Exception ex)
        {
            err = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error($"task run failed: {task.Id}", ex);
        }

        var taskDeviceName = ConfigStore.LoadDevices()?.Devices
            .FirstOrDefault(d => d.Did == task.DeviceDid)?.DisplayName;

        OperationLog.Append(new OperationEntry
        {
            Source = $"task:{task.Name}",
            DeviceName = taskDeviceName,
            Action = task.Action.ToWireName(),
            Did = task.DeviceDid,
            Ok = ok,
            Code = errCode,
            Message = err,
            DurationMs = sw.ElapsedMilliseconds,
        });
        TaskStore.UpdateLastRun(task.Id, ok, err ?? "ok");
        Log.Info($"task run end: {task.Id} ok={ok} elapsed={sw.ElapsedMilliseconds}ms");
        return ok ? 0 : 1;
    }

    // ─────────────────────────── doctor ───────────────────────────

    private static async Task<int> CmdDoctorAsync()
    {
        var problems = 0;
        void Check(string label, bool ok, string detail)
        {
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label}：{detail}");
            if (!ok) problems++;
        }

        Console.WriteLine("[doctor] 环境自检");
        Check("版本", true, $"pccard {AppPaths.Version}");
        Check("数据目录", TryDataDir(), AppPaths.DataRoot);

        var cred = CredentialVault.Load();
        Check("云凭据", cred?.IsComplete == true,
            cred is null ? "未导入" : $"userId={cred.UserId} sid={cred.Sid} capturedAt={cred.CapturedAt}");

        var doc = ConfigStore.LoadDevices();
        Check("设备清单", doc?.Devices.Count > 0,
            doc is null ? "未导入" : $"{doc.Devices.Count} 台（App v{devicemeta(doc)}）");

        var dev = ConfigStore.GetSelectedDevice();
        Check("选中设备", dev is not null, dev is null ? "无" : $"{dev.DisplayName}（did={dev.Did}）");

        if (cred?.IsComplete == true && dev is not null)
        {
            try
            {
                using var cloud = new MiCloudClient(cred);
                var list = await cloud.GetDeviceListAsync().ConfigureAwait(false);
                var mine = list.FirstOrDefault(d => d.Did == dev.Did);
                Check("云端连通", true, $"共 {list.Count} 台设备");
                Check("目标设备在云端可见", mine is not null, mine is null ? "未找到" : $"online={mine.IsOnline}");
                if (mine is not null)
                {
                    var card = new ComputerPowerCard(cloud, dev.Did);
                    try
                    {
                        var st = await card.GetStateAsync().ConfigureAwait(false);
                        Check("属性读取", true,
                            $"powerOn={st.PowerOn} deviceType={st.DeviceType} resetMenu={st.SupportsResetMenu}");
                    }
                    catch (MiCloudException ex)
                    {
                        Check("属性读取", false, $"{ex.Kind}: {ex.Message}");
                    }
                }
            }
            catch (MiCloudException ex)
            {
                Check("云端连通", false, $"{ex.Kind}（code={ex.Code}）：{ex.Message}");
            }
        }

        var (trayOk, trayMsg) = WindowsTaskScheduler.Exists(AppPaths.TrayTaskName)
            ? (true, "已注册")
            : (false, "未注册（安装器会注册；开发期可忽略）");
        Check("托盘自启任务", trayOk, trayMsg);

        var tunnelEp = ConfigStore.LoadState().TunnelEndpoint;
        var tunnel = await RdpTunnelProbe.IsEndpointOpenAsync(tunnelEp).ConfigureAwait(false);
        Console.WriteLine($"    · 旁路信号：frp 隧道 {tunnelEp} {(tunnel ? "OPEN（电脑疑似开机）" : "CLOSED")}");

        Console.WriteLine(problems == 0 ? "\n[✓] 全部通过" : $"\n[x] {problems} 项异常");
        return problems == 0 ? 0 : 1;

        static string devicemeta(DeviceCatalogDocument d) => d.Meta?.AppVersion ?? "?";
    }

    private static bool TryDataDir()
    {
        try
        {
            var probe = Path.Combine(AppPaths.DataRoot, ".writetest");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    private static string DescribeTrigger(ScheduleTask t) => t.Trigger switch
    {
        TriggerKind.Daily => $"每天 {t.Time}",
        TriggerKind.Weekly => $"每周（{string.Join("/", t.DaysOfWeek)}）{t.Time}",
        TriggerKind.Once => $"单次 {t.OnceAt}",
        _ => t.Trigger.ToString(),
    };

    private static string? GetOpt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Fmt(bool? b) => b switch { true => "是", false => "否", null => "未知" };

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"[x] 未知命令：{cmd}");
        PrintHelp();
        return 2;
    }

    private static int PrintHelpReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            pccard —— 开机卡控制台的任务执行器 / 命令行

              pccard doctor                      环境自检（凭据/设备/云端连通/隧道）
              pccard status [--json]             读取电脑开关状态（云端）
              pccard power on|off|reboot [--force]
                                                 立即开关机/重启（--force=强制档，Basic 卡为实验性）
              pccard switches [<childlock|indicator|autoboot> on|off]
                                                 设备设置开关（无参=读取三个开关状态）
              pccard credential import --stdin   导入云凭据（JSON 经 stdin，避免明文进命令行）
              pccard credential status|delete
              pccard device import <devices.json>
              pccard device list|select <did>|sync   （sync：从云端拉最新名称/IP/在线状态）
              pccard task list
              pccard task add --name X --action power-on --time 08:00 [--days Monday,Friday]
                              [--once 2026-10-12T20:00] [--every 30 --duration 2] [--did X]
              pccard task rm <id>
              pccard task run <id>               ← schtasks 的调用入口
              pccard version
            """);
    }

    private sealed class CliError : Exception
    {
        public int Code { get; }
        public CliError(int code, string message) : base(message) => Code = code;
    }
}
