namespace PcCard.Core.Models;

/// <summary>
/// 开机卡支持的动作。协议映射（来自米家 RN 插件 xiaomi.kjkcommon.mtk1 的逆向）：
///   siid2/piid1 ← PowerOn(bool true) / PowerOff(bool false)
///   siid2/piid5 ← Reboot(1) / ForceReboot(2) / ForcePowerOff(3)
/// </summary>
public enum ActionKind
{
    PowerOn,
    PowerOff,
    Reboot,
    ForceReboot,
    ForcePowerOff,
}

public static class ActionKindExtensions
{
    public static string ToWireName(this ActionKind a) => a switch
    {
        ActionKind.PowerOn => "power-on",
        ActionKind.PowerOff => "power-off",
        ActionKind.Reboot => "reboot",
        ActionKind.ForceReboot => "force-reboot",
        ActionKind.ForcePowerOff => "force-power-off",
        _ => a.ToString().ToLowerInvariant(),
    };

    public static string ToDisplayName(this ActionKind a) => a switch
    {
        ActionKind.PowerOn => "开机",
        ActionKind.PowerOff => "关机",
        ActionKind.Reboot => "重启",
        ActionKind.ForceReboot => "强制重启",
        ActionKind.ForcePowerOff => "强制关机",
        _ => a.ToString(),
    };

    /// <summary>强制类动作仅在电脑处于开机态时可用（插件同款门禁）。</summary>
    public static bool RequiresPoweredOn(this ActionKind a) =>
        a is ActionKind.Reboot or ActionKind.ForceReboot or ActionKind.ForcePowerOff;
}
