namespace PcCard.Core.Models;

/// <summary>
/// 一条操作日志（operations-YYYYMMDD.jsonl 里的一行）。
/// </summary>
public sealed class OperationEntry
{
    /// <summary>本地时间 ISO。</summary>
    public string Time { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>gui / cli / task:&lt;名称&gt;。</summary>
    public string Source { get; set; } = "";

    /// <summary>设备显示名（2026-10-20 起记录；更早的日志为空，读取时按 Did 回填）。</summary>
    public string? DeviceName { get; set; }

    public string Action { get; set; } = "";
    public string Did { get; set; } = "";
    public bool Ok { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public long DurationMs { get; set; }
}
