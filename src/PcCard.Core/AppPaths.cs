using System.Reflection;

namespace PcCard.Core;

/// <summary>
/// 全应用统一的路径与身份。
///
/// ★ 数据放 <c>%ProgramData%\PcCard</c>（机器级）而不是 %LOCALAPPDATA%：
///   定时任务以 SYSTEM 在 Session 0 运行，登录用户的 GUI 也要读同一份
///   凭据 / 任务库 / 日志 —— 机器级目录是两者唯一的公共落点。
/// </summary>
public static class AppPaths
{
    public const string ProductName = "PcCard";

    /// <summary>GUI 单实例互斥体（全局命名空间，跨会话可见）。</summary>
    public const string GuiMutexName = @"Global\PcCard.Gui";

    /// <summary>第二实例把激活参数转发到首实例的管道名。</summary>
    public const string GuiPipeName = "PcCard.Gui";

    /// <summary>schtasks 里的任务文件夹（卸载时只删这一棵子树）。</summary>
    public const string TaskFolder = "PcCard";

    /// <summary>托盘自启任务名（onlogon + /rl HIGHEST）。</summary>
    public const string TrayTaskName = @"PcCard\Tray";

    /// <summary><c>%ProgramData%\PcCard</c>。首次访问自动建目录。</summary>
    public static string DataRoot
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ProductName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>DPAPI(LocalMachine) 加密的云 API 凭据。</summary>
    public static string CredentialsPath => Path.Combine(DataRoot, "credentials.bin");

    /// <summary>设备清单（提取脚本产出的 devices.json，导入时拷到这里）。</summary>
    public static string DevicesPath => Path.Combine(DataRoot, "devices.json");

    /// <summary>定时任务定义（与 schtasks 里实际注册的任务做一致性校验）。</summary>
    public static string TasksPath => Path.Combine(DataRoot, "tasks.json");

    /// <summary>已注册的系统任务名索引（每行一个，卸载器读取用）。</summary>
    public static string TasksIndexPath => Path.Combine(DataRoot, "tasks-index.txt");

    /// <summary>运行状态（当前选中设备等）。</summary>
    public static string StatePath => Path.Combine(DataRoot, "state.json");

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(DataRoot, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>操作日志（operations-YYYYMMDD.jsonl，供 GUI 列表展示）。</summary>
    public static string OperationsDirectory
    {
        get
        {
            var dir = Path.Combine(DataRoot, "operations");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>本 exe 所在目录（安装后 = {app}）。</summary>
    public static string InstallDirectory { get; } = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>GUI 实体。★ 刻意不叫 PcCard.exe —— 避免与 pccard.exe 命令冲突。</summary>
    public static string GuiExe => Path.Combine(InstallDirectory, "PcCardApp.exe");

    /// <summary>无 UI 任务执行器 / CLI。</summary>
    public static string CliExe => Path.Combine(InstallDirectory, "pccard.exe");

    /// <summary>产品版本（取入口程序集的版本号）。</summary>
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? typeof(AppPaths).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
