using System.Text;
using PcCard.Core.Models;
using PcCard.Core.Storage;

namespace PcCard.Core.Logging;

/// <summary>
/// 操作日志：按天落 <c>operations-YYYYMMDD.jsonl</c>，GUI 读最近 N 条展示。
/// 与 FileLog（诊断日志）分开 —— 这是给用户看的业务流水。
/// </summary>
public static class OperationLog
{
    private static readonly object Gate = new();

    public static void Append(OperationEntry entry)
    {
        try
        {
            var path = Path.Combine(AppPaths.OperationsDirectory, $"operations-{DateTime.Now:yyyyMMdd}.jsonl");
            var line = JsonFile.ToCompactJson(entry);
            lock (Gate)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var w = new StreamWriter(fs, new UTF8Encoding(false));
                w.WriteLine(line);
            }
        }
        catch
        {
            // 操作日志写不进去不能反向搞崩业务
        }
    }

    /// <summary>读最近 max 条（新的在前）。</summary>
    public static List<OperationEntry> ReadRecent(int max = 500)
    {
        var result = new List<OperationEntry>();
        try
        {
            var files = Directory.EnumerateFiles(AppPaths.OperationsDirectory, "operations-*.jsonl")
                .OrderByDescending(f => f)
                .ToList();
            foreach (var f in files)
            {
                var lines = File.ReadAllLines(f, Encoding.UTF8);
                for (var i = lines.Length - 1; i >= 0 && result.Count < max; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try
                    {
                        var e = System.Text.Json.JsonSerializer.Deserialize<OperationEntry>(lines[i], JsonFile.Options);
                        if (e is not null) result.Add(e);
                    }
                    catch
                    {
                        // 单行坏了不拖累整体
                    }
                }
                if (result.Count >= max) break;
            }
        }
        catch
        {
            // 目录不存在等
        }
        return result;
    }
}
