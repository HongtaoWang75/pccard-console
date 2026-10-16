using System.Text.Json;
using PcCard.Core.Cloud;
using PcCard.Core.Models;

namespace PcCard.Core.Devices;

/// <summary>
/// 开机卡状态快照。
///
/// deviceType（siid2/piid6）的档位语义来自官方 RN 插件（xiaomi.kjkcommon.mtk1）：
///   Profession / Standard → 显示重启菜单（重启/强制重启/强制关机）+ 童锁/来电自启开关
///   Classic               → 显示简化重启菜单；童锁/来电自启隐藏
///   Basic                 → 重启菜单隐藏（我们的卡就是 Basic）
/// </summary>
public sealed record CardState(bool? PowerOn, string? DeviceType)
{
    /// <summary>强制/重启菜单是否可用（Basic 不可用）。</summary>
    public bool SupportsResetMenu => DeviceType is "Profession" or "Standard" or "Classic";

    public bool SupportsChildLock => DeviceType is "Profession" or "Standard";
    public bool SupportsAutoBoot => DeviceType is "Profession" or "Standard";
}

/// <summary>
/// 「电脑开机卡」的语义封装：把 miotspec 的 siid/piid 细节收在一处。
/// 协议事实（来自插件 bundle 逆向 + Phase A 实测）：
///   siid2/piid1 = bool 开/关机（写 true=开机，false=关机）
///   siid2/piid5 = uint8 强制操作枚举（1=重启 2=强制重启 3=强制关机，仅非 Basic 型号可用）
///   siid2/piid6 = string 设备类型（Basic/Classic/Standard/Profession）
/// </summary>
public sealed class ComputerPowerCard
{
    public const int Siid = 2;
    public const int PiidPower = 1;
    public const int PiidReset = 5;
    public const int PiidDeviceType = 6;

    // 设备设置开关（插件逆向：设置页 sendSpec(7|8|9, value)；Basic 档官方 UI 隐藏童锁/来电自启，协议全支持——已实测）
    public const int PiidChildLock = 7;   // 童锁：打开后机箱物理电源按钮不可用（云端控制不受影响）
    public const int PiidIndicator = 8;   // 指示灯：关闭后设备指示灯熄灭
    public const int PiidAutoBoot = 9;    // 通电自动开机：设备恢复供电后自动给电脑开机

    public enum CardSwitch
    {
        ChildLock,
        Indicator,
        AutoBoot,
    }

    public sealed record CardSwitchState(bool? ChildLock, bool? Indicator, bool? AutoBoot);

    private readonly MiCloudClient _cloud;

    public ComputerPowerCard(MiCloudClient cloud, string did)
    {
        _cloud = cloud;
        Did = did;
    }

    public string Did { get; }

    /// <summary>读取当前的开关状态与设备类型。</summary>
    public async Task<CardState> GetStateAsync(CancellationToken ct = default)
    {
        using var doc = await _cloud.PropGetAsync(Did, new[] { (Siid, PiidPower), (Siid, PiidDeviceType) }, ct)
            .ConfigureAwait(false);
        return ParseState(doc);
    }

    /// <summary>开机（on=true）/ 关机（on=false）。</summary>
    public async Task<bool> SetPowerAsync(bool on, CancellationToken ct = default)
    {
        using var doc = await _cloud.PropSetAsync(Did, Siid, PiidPower, on, ct).ConfigureAwait(false);
        return CheckPropResults(doc);
    }

    /// <summary>
    /// 执行动作。
    /// ★ 关于强制/重启档：官方插件对 Basic 型号隐藏此菜单（SupportsResetMenu=false），
    ///   但按用户明确要求在控制台开放。云端/设备不支持时会以明确的错误返回
    ///   （要么报错、要么无动作），不会产生歧义 —— 属预期行为。
    ///   保留的唯一前置校验：重启类仅在电脑处于开机态时可用（与插件一致）。
    /// </summary>
    public async Task ExecuteAsync(ActionKind kind, CancellationToken ct = default)
    {
        if (kind == ActionKind.PowerOn)
        {
            await SetPowerAsync(true, ct).ConfigureAwait(false);
            return;
        }
        if (kind == ActionKind.PowerOff)
        {
            await SetPowerAsync(false, ct).ConfigureAwait(false);
            return;
        }

        // 重启 / 强制重启 / 强制关机（siid2/piid5：1/2/3）
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (state.PowerOn != true)
            throw new InvalidOperationException($"「{kind.ToDisplayName()}」仅在电脑处于开机状态时可用");

        var value = kind switch
        {
            ActionKind.Reboot => 1,
            ActionKind.ForceReboot => 2,
            ActionKind.ForcePowerOff => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var doc = await _cloud.PropSetAsync(Did, Siid, PiidReset, value, ct).ConfigureAwait(false);
        CheckPropResults(doc);
    }

    /// <summary>读取三个设置开关的当前状态（读不到的项为 null，如设备不支持）。</summary>
    public async Task<CardSwitchState> GetSwitchesAsync(CancellationToken ct = default)
    {
        using var doc = await _cloud.PropGetAsync(Did, new[]
        {
            (Siid, PiidChildLock), (Siid, PiidIndicator), (Siid, PiidAutoBoot),
        }, ct).ConfigureAwait(false);

        bool? child = null, indicator = null, autoBoot = null;
        var lastErrCode = 0;
        if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var piid = item.TryGetProperty("piid", out var p) && p.TryGetInt32(out var pi) ? pi : -1;
                var code = item.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                if (code != 0)
                {
                    lastErrCode = code;
                    continue;
                }
                bool? v = item.TryGetProperty("value", out var vv)
                          && vv.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? vv.GetBoolean()
                    : null;
                switch (piid)
                {
                    case PiidChildLock: child = v; break;
                    case PiidIndicator: indicator = v; break;
                    case PiidAutoBoot: autoBoot = v; break;
                }
            }
        }

        if (child is null && indicator is null && autoBoot is null && lastErrCode != 0)
            throw MiCloudException.FromCode(lastErrCode, "设备无响应（开关读取失败）");
        return new CardSwitchState(child, indicator, autoBoot);
    }

    /// <summary>写一个设置开关。</summary>
    public async Task SetSwitchAsync(CardSwitch which, bool value, CancellationToken ct = default)
    {
        var piid = which switch
        {
            CardSwitch.ChildLock => PiidChildLock,
            CardSwitch.Indicator => PiidIndicator,
            _ => PiidAutoBoot,
        };
        using var doc = await _cloud.PropSetAsync(Did, Siid, piid, value, ct).ConfigureAwait(false);
        CheckPropResults(doc);
    }

    private static CardState ParseState(JsonDocument doc)
    {
        bool? power = null;
        string? type = null;
        var lastErrCode = 0;

        if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var piid = item.TryGetProperty("piid", out var p) && p.TryGetInt32(out var pi) ? pi : -1;
                var code = item.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                var hasValue = item.TryGetProperty("value", out var v);

                if (code != 0)
                {
                    lastErrCode = code;
                    continue;
                }
                if (piid == PiidPower && hasValue)
                    power = v.ValueKind == JsonValueKind.True;
                else if (piid == PiidDeviceType && hasValue && v.ValueKind == JsonValueKind.String)
                    type = v.GetString();
            }
        }

        if (power is null && type is null && lastErrCode != 0)
            throw MiCloudException.FromCode(lastErrCode, "设备无响应（属性读取全部失败）");

        return new CardState(power, type);
    }

    private static bool CheckPropResults(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return true; // 少数写法不带 result，顶层 code==0 已由客户端保证

        foreach (var item in arr.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            if (code != 0)
                throw MiCloudException.FromCode(code, "设备拒绝了写入");
        }
        return true;
    }
}
