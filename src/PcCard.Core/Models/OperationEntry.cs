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

    public string Action { get; set; } = "";
    public string Did { get; set; } = "";
    public bool Ok { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public long DurationMs { get; set; }
}
