using System.Diagnostics;
using System.Text;
using PcCard.Core.Models;

namespace PcCard.Core.Scheduling;

/// <summary>
/// schtasks.exe 的薄封装。创建时必须：
///   · XML 落盘为 UTF-16LE + BOM（schtasks 对编码敏感，错编码会报
///     "The task XML contains a value which is incorrectly formatted or out of range"）
///   · 用 /F 覆盖同名任务（幂等 upsert）
///
/// 所有方法返回 (ok, message)，stderr 原文回传给调用方 —— 调试期原文比任何
/// "友好错误"都有用。
/// </summary>
public static class WindowsTaskScheduler
{
    public static (bool Ok, string Message) CreateOrUpdate(ScheduleTask task, string exePath, DateTime now)
    {
        var xml = TaskXmlBuilder.BuildXml(task, now, exePath);
        var spool = Path.Combine(AppPaths.DataRoot, "spool");
        Directory.CreateDirectory(spool);
        var xmlPath = Path.Combine(spool, $"task_{task.Id}.xml");
        File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            return RunSchTasks($"/Create /TN \"{task.SystemTaskName}\" /XML \"{xmlPath}\" /F");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* 删不掉无所谓 */ }
        }
    }

    public static (bool Ok, string Message) Delete(ScheduleTask task)
        => RunSchTasks($"/Delete /TN \"{task.SystemTaskName}\" /F");

    public static (bool Ok, string Message) DeleteByName(string fullTaskName)
        => RunSchTasks($"/Delete /TN \"{fullTaskName}\" /F");

    /// <summary>查询任务 XML（不存在返回 null）。</summary>
    public static string? QueryXml(string fullTaskName)
    {
        var (ok, output) = RunSchTasks($"/Query /TN \"{fullTaskName}\" /XML");
        return ok ? output : null;
    }

    public static bool Exists(string fullTaskName) => QueryXml(fullTaskName) is not null;

    public static (bool Ok, string Message) Run(string fullTaskName)
        => RunSchTasks($"/Run /TN \"{fullTaskName}\"");

    /// <summary>即时执行一条任务（GUI「立即运行」按钮 / 安装后拉起托盘）。</summary>
    public static (bool Ok, string Message) RunTrayTask() => Run(AppPaths.TrayTaskName);

    private static (bool Ok, string Message) RunSchTasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            // ★ 并行读两个流再等待退出：旧写法「先 ReadToEnd(stdout) 再 WaitForExit」在
            //   stderr 写满管道缓冲时会死锁（GUI 上表现为整个窗口「未响应」）。
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
                return (false, "schtasks 超时（10s）");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return (p.ExitCode == 0, message);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
