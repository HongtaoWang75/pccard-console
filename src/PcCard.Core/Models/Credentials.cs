using System.Text.Json.Serialization;

namespace PcCard.Core.Models;

/// <summary>
/// 小米云 API 凭据（sid=xiaomiio，直连 api.io.mi.com 用）。
/// 字段与 acquire_qr.py / 浏览器 DevTools 手工抓取产出的 credentials.json 对齐。
/// </summary>
public sealed class Credentials
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("cUserId")]
    public string? CUserId { get; set; }

    /// <summary>签名密钥（base64）。</summary>
    [JsonPropertyName("ssecurity")]
    public string Ssecurity { get; set; } = "";

    [JsonPropertyName("serviceToken")]
    public string ServiceToken { get; set; } = "";

    /// <summary>长期令牌：用于 serviceToken 过期后的静默续期（不带密码）。</summary>
    [JsonPropertyName("passToken")]
    public string? PassToken { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>登录时的 User-Agent 与 pass_o（保持与云端会话一致，可选）。</summary>
    [JsonPropertyName("ua")]
    public string? Ua { get; set; }

    [JsonPropertyName("pass_o")]
    public string? PassO { get; set; }

    [JsonPropertyName("region")]
    public string Region { get; set; } = "cn";

    [JsonPropertyName("sid")]
    public string? Sid { get; set; } = "xiaomiio";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("capturedAt")]
    public string? CapturedAt { get; set; }

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(UserId)
        && !string.IsNullOrWhiteSpace(Ssecurity)
        && !string.IsNullOrWhiteSpace(ServiceToken);
}
