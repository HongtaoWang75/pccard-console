using System.Text.Json.Serialization;

namespace PcCard.Core.Models;

/// <summary>
/// 一台设备（来自提取脚本产出的 devices.json，字段为 snake_case）。
/// </summary>
public sealed class DeviceRecord
{
    [JsonPropertyName("did")]
    public string Did { get; set; } = "";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>本地控制 token（32 hex）。云通道用不着，留作本地 miIO 备用。</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("token_format")]
    public string? TokenFormat { get; set; }

    [JsonPropertyName("local_ip")]
    public string? LocalIp { get; set; }

    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("ssid")]
    public string? Ssid { get; set; }

    [JsonPropertyName("spec_urn")]
    public string? SpecUrn { get; set; }

    /// <summary>提取时缓存里的在线状态（仅供导入时参考，界面状态一律现查云端）。</summary>
    [JsonPropertyName("is_online")]
    public bool? IsOnline { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Did : Name!;
}

/// <summary>devices.json 顶层文档。</summary>
public sealed class DeviceCatalogDocument
{
    [JsonPropertyName("_meta")]
    public DeviceCatalogMeta? Meta { get; set; }

    [JsonPropertyName("devices")]
    public List<DeviceRecord> Devices { get; set; } = new();
}

public sealed class DeviceCatalogMeta
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("app_version")]
    public string? AppVersion { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }
}
