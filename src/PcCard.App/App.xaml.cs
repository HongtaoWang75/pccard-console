using Microsoft.UI.Xaml;
using PcCard.App.Activation;
using PcCard.Core.Logging;

namespace PcCard.App;

/// <summary>
/// 应用对象：建主窗、装托盘、接激活管道、跑 --selfcheck 自检。
/// </summary>
public partial class App : Application
{
    private static readonly FileLog Log = new("gui");

    internal static StartupArgs Startup = new();
    internal static GuiInstance? Instance;
    internal static MainWindow? MainWindow { get; private set; }
    internal static ShellTrayIcon? Tray { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InstallCrashHandlers();

        MainWindow = new MainWindow();
        if (!Startup.StartHidden)
            MainWindow.Activate();
        // StartHidden（--tray）：保持隐藏，只在托盘里有图标

        Tray = new ShellTrayIcon(
            show: () => MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.ShowFromTray()),
            refresh: () => MainWindow.DispatcherQueue.TryEnqueue(() => _ = MainWindow.RefreshAsync(manual: true)),
            exit: () => MainWindow.DispatcherQueue.TryEnqueue(ExitApp));
        Tray.Install();

        Instance?.StartListening(raw =>
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.HandleActivation(raw)));

        if (Startup.SelfCheck)
            _ = RunSelfCheckAsync();
        else if (Startup.ReproDialog)
            _ = RunReproAsync();
    }

    /// <summary>
    /// 全局异常兜底：任何漏网异常（async void / XAML 事件 / 未观察任务）先落日志、尽力续命。
    /// 注：XAML 的 stowed fail-fast（0xc000027b）是 RO 层 fast-fail、托管层拦不住 ——
    /// 终局要靠不触发它；这里保证「托管异常」一律有据可查。
    /// </summary>
    private void InstallCrashHandlers()
    {
        UnhandledException += (_, e) =>
        {
            Log.Error("Application.UnhandledException", e.Exception);
            e.Handled = true; // 尽力续命
        };

        // 注：WinUI3 的 DispatcherQueue 没有 UnhandledException 事件（那是 UWP CoreDispatcher 的 API），
        // 所以 async void 的兜底由 MainWindow.SafeRun 统一包装承担。

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.Error("AppDomain.UnhandledException", ex);
            else
                Log.Error($"AppDomain.UnhandledException: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>--repro-dialog：复现「对话框收尾期连弹」的崩溃机制（raw）或验证串行器（host）。</summary>
    private async Task RunReproAsync()
    {
        string text;
        try
        {
            text = await MainWindow!.RunReproDialogAsync(Startup.ReproRaw);
        }
        catch (Exception ex)
        {
            Log.Error("repro failed", ex);
            text = "REPRO-FAIL: " + ex;
        }
        Console.WriteLine(text);
        WriteSidecar(Startup.PngPath, text);
        ExitApp();
    }

    /// <summary>--selfcheck：自渲染截图 + 诊断落盘/落 stdout，默认跑完即退。</summary>
    private async Task RunSelfCheckAsync()
    {
        string text;
        try
        {
            text = await MainWindow!.RunSelfCheckAsync(Startup.PngPath, Startup.TimeoutSec);
            Log.Info("selfcheck ok");
        }
        catch (Exception ex)
        {
            Log.Error("selfcheck failed", ex);
            text = "SELFCHECK-FAIL: " + ex;
        }
        Console.WriteLine(text);
        WriteSidecar(Startup.PngPath, text);
        if (!Startup.Stay)
            ExitApp();
    }

    private static void WriteSidecar(string? pngPath, string text)
    {
        try
        {
            if (!string.IsNullOrEmpty(pngPath))
                File.WriteAllText(pngPath + ".txt", text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 落盘失败也已尽力（stdout 有同样的内容）
        }
    }

    internal static void ExitApp()
    {
        try
        {
            Tray?.Uninstall();
        }
        catch
        {
            // 忽略
        }
        MainWindow?.ForceClose();
        Current.Exit();
    }
}
