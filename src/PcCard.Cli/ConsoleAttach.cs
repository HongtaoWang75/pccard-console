using System.Runtime.InteropServices;

namespace PcCard.Cli;

/// <summary>
/// WinExe 进程默认没有控制台；<c>--console</c> 时把自己挂到父进程的控制台，
/// 方便开发期直接在终端看输出。（复用 FlowGet 的同名做法）
/// </summary>
internal static partial class ConsoleAttach
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    public static bool TryAttach()
    {
        if (GetConsoleWindow() != IntPtr.Zero) return true;
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 拿不到就安静地用文件日志
        }
        return true;
    }
}
