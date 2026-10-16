using System.Text;

namespace PcCard.Core.Logging;

/// <summary>
/// 极简文件日志（复用 FlowGet 的设计）：按天落盘 + 最多保留 14 天。
///
/// ★ 日志正文一律英文：便于 grep、便于跨语言检索。
///   面向用户的文案才需要双语，日志不需要。
/// </summary>
public sealed class FileLog : IDisposable
{
    private const int RetainDays = 14;

    private readonly object _gate = new();
    private readonly string _channel;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public FileLog(string channel)
    {
        _channel = channel;
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>同时写日志与 stderr —— CLI 场景下让用户直接看到。</summary>
    public void ErrorWithConsole(string message, Exception? ex = null)
    {
        Error(message, ex);
        Console.Error.WriteLine(ex is null ? message : $"{message} ({ex.Message})");
    }

    private void Write(string level, string message)
    {
        if (_disposed) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{_channel}] {message}";

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch
            {
                // 日志写不进去绝不能反过来搞崩业务
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today) return;

        _writer?.Dispose();
        _writerDate = today;

        var path = Path.Combine(AppPaths.LogDirectory, $"{_channel}-{today:yyyyMMdd}.log");
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };

        PruneOldLogs();
    }

    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch
        {
            // 清理失败无所谓，下次再说
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
