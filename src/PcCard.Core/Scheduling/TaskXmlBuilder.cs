using System.Globalization;
using System.Text;
using PcCard.Core.Models;

namespace PcCard.Core.Scheduling;

/// <summary>
/// 纯函数：ScheduleTask → 计划任务 XML（Task Scheduler 1.2 schema）。
///
/// ★ 为什么用 XML 而不是 schtasks /sc：XML 不受系统区域设置影响
///   （/ST、/SD、/D 的解析跟 locale 走，中文 Windows 上是经典翻车点），
///   且能表达 StartWhenAvailable、ExecutionTimeLimit 等 /sc 表达不了的设置。
///
/// ★ 注意 StartBoundary 取"下一个未来时刻"：若取过去时刻，
///   StartWhenAvailable=true 会把这条当成"错过的任务"立刻补跑 —— 那就等于
///   创建任务的瞬间就开关机一次。这是本文件里最要命的一个坑。
/// </summary>
public static class TaskXmlBuilder
{
    private static readonly string[] WeekDayNames =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public static string BuildXml(ScheduleTask task, DateTime now, string exePath)
    {
        var boundary = NextBoundary(task, now);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine("  <RegistrationInfo>");
        sb.AppendLine($"    <Description>PcCard scheduled action: {Esc(task.Action.ToWireName())} ({Esc(task.Name)})</Description>");
        sb.AppendLine($"    <URI>\\{AppPaths.TaskFolder}\\Task_{Esc(task.Id)}</URI>");
        sb.AppendLine("  </RegistrationInfo>");
        sb.AppendLine("  <Triggers>");
        AppendTrigger(sb, task, boundary);
        sb.AppendLine("  </Triggers>");
        sb.AppendLine("  <Principals>");
        sb.AppendLine("    <Principal id=\"Author\">");
        sb.AppendLine("      <UserId>S-1-5-18</UserId>"); // LocalSystem
        sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        sb.AppendLine("    </Principal>");
        sb.AppendLine("  </Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        sb.AppendLine("    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>");
        sb.AppendLine("    <IdleSettings>");
        sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        sb.AppendLine("    </IdleSettings>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        sb.AppendLine($"    <Enabled>{(task.Enabled ? "true" : "false")}</Enabled>");
        sb.AppendLine("    <Hidden>false</Hidden>");
        sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        sb.AppendLine("    <WakeToRun>false</WakeToRun>");
        sb.AppendLine("    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>");
        sb.AppendLine("    <Priority>7</Priority>");
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\">");
        sb.AppendLine("    <Exec>");
        sb.AppendLine($"      <Command>{Esc(exePath)}</Command>");
        sb.AppendLine($"      <Arguments>task run {Esc(task.Id)}</Arguments>");
        sb.AppendLine($"      <WorkingDirectory>{Esc(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>");
        sb.AppendLine("    </Exec>");
        sb.AppendLine("  </Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }

    private static void AppendTrigger(StringBuilder sb, ScheduleTask task, DateTime boundary)
    {
        var start = boundary.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        switch (task.Trigger)
        {
            case TriggerKind.Daily:
                sb.AppendLine("    <CalendarTrigger>");
                sb.AppendLine($"      <StartBoundary>{start}</StartBoundary>");
                sb.AppendLine("      <Enabled>true</Enabled>");
                sb.AppendLine("      <ScheduleByDay>");
                sb.AppendLine("        <DaysInterval>1</DaysInterval>");
                sb.AppendLine("      </ScheduleByDay>");
                AppendRepetition(sb, task);
                sb.AppendLine("    </CalendarTrigger>");
                break;

            case TriggerKind.Weekly:
                sb.AppendLine("    <CalendarTrigger>");
                sb.AppendLine($"      <StartBoundary>{start}</StartBoundary>");
                sb.AppendLine("      <Enabled>true</Enabled>");
                sb.AppendLine("      <ScheduleByWeek>");
                sb.AppendLine("        <DaysOfWeek>");
                foreach (var d in NormalizeDays(task.DaysOfWeek))
                    sb.AppendLine($"          <{d}/>");
                sb.AppendLine("        </DaysOfWeek>");
                sb.AppendLine("        <WeeksInterval>1</WeeksInterval>");
                sb.AppendLine("      </ScheduleByWeek>");
                AppendRepetition(sb, task);
                sb.AppendLine("    </CalendarTrigger>");
                break;

            case TriggerKind.Once:
            default:
                sb.AppendLine("    <TimeTrigger>");
                sb.AppendLine($"      <StartBoundary>{start}</StartBoundary>");
                sb.AppendLine("      <Enabled>true</Enabled>");
                AppendRepetition(sb, task);
                sb.AppendLine("    </TimeTrigger>");
                break;
        }
    }

    private static void AppendRepetition(StringBuilder sb, ScheduleTask task)
    {
        if (task.RepeatEveryMinutes is not { } mins || mins <= 0) return;
        var hours = task.RepeatDurationHours is { } h && h > 0 ? h : 24;
        sb.AppendLine("      <Repetition>");
        sb.AppendLine($"        <Interval>PT{mins}M</Interval>");
        sb.AppendLine($"        <Duration>PT{hours}H</Duration>");
        sb.AppendLine("        <StopAtDurationEnd>false</StopAtDurationEnd>");
        sb.AppendLine("      </Repetition>");
    }

    /// <summary>下一个触发时刻（严格晚于 now 的最近一个）。</summary>
    public static DateTime NextBoundary(ScheduleTask task, DateTime now)
    {
        switch (task.Trigger)
        {
            case TriggerKind.Once:
            {
                var t = DateTime.TryParse(task.OnceAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                    ? v
                    : now.AddMinutes(1);
                return t > now ? t : now.AddMinutes(1); // 过去的时刻就顺延一分钟，避免"创建即补跑"
            }

            case TriggerKind.Weekly:
            {
                var (hh, mm) = ParseTime(task.Time);
                var days = NormalizeDays(task.DaysOfWeek)
                    .Select(d => Array.IndexOf(WeekDayNames, d))
                    .Where(i => i >= 0)
                    .ToHashSet();
                if (days.Count == 0) days.Add((int)now.DayOfWeek);
                for (var offset = 0; offset < 8; offset++)
                {
                    var cand = now.Date.AddDays(offset).AddHours(hh).AddMinutes(mm);
                    if (days.Contains((int)cand.DayOfWeek) && cand > now) return cand;
                }
                return now.AddDays(1); // 理论到不了
            }

            case TriggerKind.Daily:
            default:
            {
                var (hh, mm) = ParseTime(task.Time);
                var today = now.Date.AddHours(hh).AddMinutes(mm);
                return today > now ? today : today.AddDays(1);
            }
        }
    }

    private static (int, int) ParseTime(string time)
    {
        var parts = (time ?? "").Split(':');
        var hh = parts.Length > 0 && int.TryParse(parts[0], out var h) ? Math.Clamp(h, 0, 23) : 8;
        var mm = parts.Length > 1 && int.TryParse(parts[1], out var m) ? Math.Clamp(m, 0, 59) : 0;
        return (hh, mm);
    }

    private static IEnumerable<string> NormalizeDays(IEnumerable<string> days)
    {
        var set = new List<string>();
        foreach (var d in days)
        {
            var norm = WeekDayNames.FirstOrDefault(w => w.Equals(d, StringComparison.OrdinalIgnoreCase));
            if (norm is not null && !set.Contains(norm)) set.Add(norm);
        }
        return set;
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
