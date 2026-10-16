namespace PcCard.Core.Models;

public enum TriggerKind
{
    /// <summary>每天 HH:mm。</summary>
    Daily,

    /// <summary>每周指定几天的 HH:mm。</summary>
    Weekly,

    /// <summary>单次，指定具体时间（OnceAt）。</summary>
    Once,
}

/// <summary>
/// 一条定时任务。持久化在 tasks.json，同时镜像到 schtasks（PcCard\Task_&lt;id&gt;）。
/// </summary>
public sealed class ScheduleTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>目标设备 did（字符串形式）。</summary>
    public string DeviceDid { get; set; } = "";

    public ActionKind Action { get; set; } = ActionKind.PowerOn;

    public TriggerKind Trigger { get; set; } = TriggerKind.Daily;

    /// <summary>Daily/Weekly 的触发时刻，"HH:mm"（本地时间）。</summary>
    public string Time { get; set; } = "08:00";

    /// <summary>Weekly 用：Monday…Sunday（英文枚举名，写进任务 XML 的 DaysOfWeek）。</summary>
    public List<string> DaysOfWeek { get; set; } = new();

    /// <summary>Once 用：本地时间 ISO（yyyy-MM-ddTHH:mm:ss）。</summary>
    public string? OnceAt { get; set; }

    /// <summary>可选：在该触发器上叠加重复间隔（分钟）。</summary>
    public int? RepeatEveryMinutes { get; set; }

    /// <summary>可选：重复持续时间（小时），默认 24。</summary>
    public int? RepeatDurationHours { get; set; }

    // ── 运行结果回填（由 pccard task run 写入）──
    public string? LastRunUtc { get; set; }
    public string? LastResult { get; set; }
    public string? LastMessage { get; set; }

    /// <summary>schtasks 里的完整任务名。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SystemTaskName => $@"{AppPaths.TaskFolder}\Task_{Id}";
}

public sealed class ScheduleTaskDocument
{
    public List<ScheduleTask> Tasks { get; set; } = new();
}
