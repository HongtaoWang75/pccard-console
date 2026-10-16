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
