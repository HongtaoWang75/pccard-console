using PcCard.Core.Models;

namespace PcCard.Core.Storage;

/// <summary>
/// 定时任务定义（tasks.json）。系统侧的实际注册由 Scheduling.WindowsTaskScheduler 负责，
/// 两边通过 Task.Id ↔ PcCard\Task_&lt;id&gt; 对应。
/// </summary>
public static class TaskStore
{
    private static readonly object Gate = new();

    public static List<ScheduleTask> Load()
    {
        lock (Gate)
        {
            return JsonFile.Read<ScheduleTaskDocument>(AppPaths.TasksPath)?.Tasks ?? new List<ScheduleTask>();
        }
    }

    public static void Save(List<ScheduleTask> tasks)
    {
        lock (Gate)
        {
            JsonFile.Write(AppPaths.TasksPath, new ScheduleTaskDocument { Tasks = tasks });
            // 纯 ASCII 的任务名索引：卸载器读它逐个 /Delete。
            // （刻意不用 schtasks /Query 输出解析 —— 任务名可含中文，Inno 的
            //   Pos/Copy 是字节偏移，会在中文上翻车。）
            try
            {
                File.WriteAllLines(
                    AppPaths.TasksIndexPath,
                    tasks.Select(t => t.SystemTaskName),
                    new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 索引写不进去不影响主功能（只影响卸载时的任务清理）
            }
        }
    }

    public static ScheduleTask? Find(string id) => Load().FirstOrDefault(t => t.Id == id);

    public static void Upsert(ScheduleTask task)
    {
        var tasks = Load();
        var idx = tasks.FindIndex(t => t.Id == task.Id);
        if (idx >= 0) tasks[idx] = task;
        else tasks.Add(task);
        Save(tasks);
    }

    public static bool Remove(string id)
    {
        var tasks = Load();
        var removed = tasks.RemoveAll(t => t.Id == id) > 0;
        if (removed) Save(tasks);
        return removed;
    }

    /// <summary>回填一次运行结果（由 pccard task run 调用）。</summary>
    public static void UpdateLastRun(string id, bool ok, string message)
    {
        var tasks = Load();
        var t = tasks.FirstOrDefault(x => x.Id == id);
        if (t is null) return;
        t.LastRunUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        t.LastResult = ok ? "ok" : "fail";
        t.LastMessage = message.Length > 200 ? message[..200] : message;
        Save(tasks);
    }
}
