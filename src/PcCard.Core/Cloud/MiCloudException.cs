namespace PcCard.Core.Cloud;

public enum MiCloudErrorKind
{
    /// <summary>登录态失效（serviceToken/ssecurity 过期）→ 需要重新导入凭据或刷新。</summary>
    AuthInvalid,

    /// <summary>设备当前不在线（云侧报设备离线）。</summary>
    DeviceOffline,

    /// <summary>被限流。</summary>
    RateLimited,

    /// <summary>网络层错误。</summary>
    NetError,

    /// <summary>其它协议错误（原样带 code/message）。</summary>
    Protocol,
}

public sealed class MiCloudException : Exception
{
    public MiCloudErrorKind Kind { get; }
    public int Code { get; }

    public MiCloudException(MiCloudErrorKind kind, int code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// 云返回码 → 语义。
    /// ★ 已知码来自 Phase A 实测：
    ///   401 = 签名/登录态失效；-704042011 = 设备侧无响应（离线设备的属性读取）。
    ///   -704 开头一family 都是设备侧错误 → 归为设备离线/无响应。
    /// </summary>
    public static MiCloudException FromCode(int code, string message) => new(code switch
    {
        401 => MiCloudErrorKind.AuthInvalid,
        70016 => MiCloudErrorKind.AuthInvalid,
        -9999 => MiCloudErrorKind.DeviceOffline,
        <= -704_000_000 => MiCloudErrorKind.DeviceOffline, // 如 -704042011（实测）
        _ => MiCloudErrorKind.Protocol,
    }, code, message);
}
