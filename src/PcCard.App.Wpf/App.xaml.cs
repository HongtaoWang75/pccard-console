using System.IO;
using System.Windows;
using PcCard.AppWpf.Activation;
using PcCard.Core.Logging;

namespace PcCard.AppWpf;

/// <summary>
/// WPF 应用对象：单实例转发 → 建主窗、装托盘、接激活管道、跑 --selfcheck。
/// （入口由 WPF 自动生成的 Main 提供；启动参数走 Environment.GetCommandLineArgs。）
/// </summary>
public partial class App : Application
{
    private static readonly FileLog Log = new("gui");

    internal static StartupArgs Args = new();
    internal static GuiInstance? Instance;
    internal static MainWindow? MainWin { get; private set; }
    internal static TrayIcon? Tray { get; private set; }
    private static bool _forwarded;

    public App()
    {
        InitializeComponent();
        InstallCrashHandlers();

        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        Args = StartupArgs.Parse(cmdArgs);
        Log.Info($"--- PcCard WPF starting: {string.Join(' ', cmdArgs)} ---");

        Instance = new GuiInstance(Log);

        // --selfcheck 跳过单实例检查：独立短命实例，不打扰托盘常驻实例
        if (!Args.SelfCheck && !Instance.Acquire())
        {
            var delivered = GuiInstance.Forward(Args.ToActivationMessage(), TimeSpan.FromSeconds(3));
            Log.Info(delivered ? "激活参数已转发给现有实例，本进程退出" : "转发失败（旧实例可能僵死），改为自行开窗");
            _forwarded = delivered;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (_forwarded)
        {
            Shutdown(0); // OnExit 会写退出日志并释放管道/互斥体
            return;
        }

        try
        {
            MainWin = new MainWindow();
            if (!Args.StartHidden)
                MainWin.ShowFromTray();
            else
                MainWin.Hide(); // --tray：只在托盘里有图标

            Tray = new TrayIcon(
                show: () => Dispatcher.Invoke(() => MainWin?.ShowFromTray()),
                refresh: () => Dispatcher.Invoke(() => MainWin?.TriggerRefresh()),
                exit: () => Dispatcher.Invoke(ExitApp));
            Tray.Install();

            Instance?.StartListening(raw => Dispatcher.Invoke(() => MainWin?.HandleActivation(raw)));

            if (Args.SelfCheck)
                _ = RunSelfCheckAsync();
        }
        catch (Exception ex)
        {
            Log.Error("OnStartup failed", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Tray?.Uninstall();
        }
        catch
        {
            // 忽略
        }
        Instance?.Dispose();
        Log.Info("--- PcCard WPF exited ---");
        Log.Dispose();
        base.OnExit(e);
    }

    /// <summary>全局兜底：WPF 的 Dispatcher 异常是可捕获的（WinUI 的 XAML fail-fast 在这里不存在）。</summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("DispatcherUnhandledException", e.Exception);
            e.Handled = true; // 尽力续命
        };
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

    private async Task RunSelfCheckAsync()
    {
        string text;
        try
        {
            text = await MainWin!.RunSelfCheckAsync(Args.PngPath, Args.TimeoutSec);
            Log.Info("selfcheck ok");
        }
        catch (Exception ex)
        {
            Log.Error("selfcheck failed", ex);
            text = "SELFCHECK-FAIL: " + ex;
        }
        WriteSidecar(Args.PngPath, text);
        ExitApp();
    }

    internal static void WriteSidecar(string? pngPath, string text)
    {
        try
        {
            if (!string.IsNullOrEmpty(pngPath))
                File.WriteAllText(pngPath + ".txt", text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 落盘失败也已尽力
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
        MainWin?.ForceClose();
        Current.Shutdown();
    }
}
