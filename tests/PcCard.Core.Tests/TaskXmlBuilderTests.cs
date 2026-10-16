using PcCard.Core.Models;
using PcCard.Core.Scheduling;
using Xunit;

namespace PcCard.Core.Tests;

public class TaskXmlBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 30, 0); // 周日

    private static ScheduleTask Daily(string time) => new()
    {
        Id = "abc123",
        Name = "早上开机",
        DeviceDid = "1234567890",
        Action = ActionKind.PowerOn,
        Trigger = TriggerKind.Daily,
        Time = time,
    };

    [Fact]
    public void Daily_FutureTime_TodayBoundary()
    {
        var b = TaskXmlBuilder.NextBoundary(Daily("12:00"), Now);
        Assert.Equal(new DateTime(2026, 10, 11, 12, 0, 0), b);
    }

    [Fact]
    public void Daily_PastTime_TomorrowBoundary()
    {
        var b = TaskXmlBuilder.NextBoundary(Daily("08:00"), Now);
        Assert.Equal(new DateTime(2026, 10, 12, 8, 0, 0), b);
    }

    [Fact]
    public void Once_PastTime_ShiftsToFuture()
    {
        var t = new ScheduleTask
        {
            Trigger = TriggerKind.Once,
            OnceAt = "2026-10-01T10:00:00",
        };
        var b = TaskXmlBuilder.NextBoundary(t, Now);
        Assert.True(b > Now, "Once 的过去时刻必须顺延，绝不能留下'创建即补跑'的坑");
    }

    [Fact]
    public void Weekly_PicksNextMatchingWeekday()
    {
        // 2026-10-11 是周日；只选周五 → 下一个周五 10-16
        var t = new ScheduleTask
        {
            Trigger = TriggerKind.Weekly,
            Time = "20:00",
            DaysOfWeek = new List<string> { "Friday" },
        };
        var b = TaskXmlBuilder.NextBoundary(t, Now);
        Assert.Equal(new DateTime(2026, 10, 16, 20, 0, 0), b);
    }

    [Fact]
    public void Xml_ContainsSystemPrincipalAndArgs()
    {
        var xml = TaskXmlBuilder.BuildXml(Daily("12:00"), Now, @"C:\Program Files\PcCard\pccard.exe");
        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains("<StartBoundary>2026-10-11T12:00:00</StartBoundary>", xml);
        Assert.Contains("task run abc123", xml);
        Assert.Contains(@"C:\Program Files\PcCard\pccard.exe", xml);
        // 中文名不能破坏 XML
        Assert.Contains("早上开机", xml);
    }

    [Fact]
    public void Xml_EscapesSpecialChars()
    {
        var t = Daily("12:00");
        t.Name = "a & b <c>";
        var xml = TaskXmlBuilder.BuildXml(t, Now, @"C:\x\pccard.exe");
        Assert.Contains("a &amp; b &lt;c&gt;", xml);
    }

    [Fact]
    public void Xml_Repetition_WhenConfigured()
    {
        var t = Daily("12:00");
        t.RepeatEveryMinutes = 30;
        t.RepeatDurationHours = 2;
        var xml = TaskXmlBuilder.BuildXml(t, Now, @"C:\x\pccard.exe");
        Assert.Contains("<Interval>PT30M</Interval>", xml);
        Assert.Contains("<Duration>PT2H</Duration>", xml);
    }
}
