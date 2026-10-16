using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PcCard.App.Activation;
using PcCard.Core.Logging;

namespace PcCard.App;

/// <summary>
/// 自定义入口（csproj 里定义了 DISABLE_XAML_GENERATED_MAIN）。
/// 在建任何窗口之前完成：单实例抢锁 → 已有实例则转发激活参数 → 解析 --tray/--selfcheck。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SetupOutput();
        var log = new FileLog("gui");
        var startup = StartupArgs.Parse(args);
        log.Info($"--- PcCard GUI starting: {string.Join(' ', args)} ---");

        var instance = new GuiInstance(log);
        try
        {
            // ★ --selfcheck 刻意跳过单实例检查：以独立的短命实例运行（自己开窗、
            //   自己截图、自己退出），完全不打扰可能正在托盘常驻的正式实例。
            if (!startup.SelfCheck && !instance.Acquire())
            {
                var delivered = GuiInstance.Forward(startup.ToActivationMessage(), TimeSpan.FromSeconds(3));
                log.Info(delivered ? "激活参数已转发给现有实例，本进程退出" : "转发失败（旧实例可能僵死），改为自行开窗");
                if (delivered) return 0;
            }

            App.Startup = startup;
            App.Instance = instance;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            // ★ 参数不能叫 `_` —— 那样 `_ = new App();` 会变成对回调参数的赋值（编译不过）
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("GUI 启动失败", ex);
            Console.Error.WriteLine($"GUI 启动失败：{ex.Message}");
            return 1;
        }
        finally
        {
            instance.Dispose();
            log.Info("--- PcCard GUI exited ---");
            log.Dispose();
        }
    }

    /// <summary>显式接管 stdout 为 UTF-8（--selfcheck 的输出会被 SSH 管道捕获，不能乱码）。</summary>
    private static void SetupOutput()
    {
        try
        {
            var enc = new System.Text.UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), enc) { AutoFlush = true });
        }
        catch
        {
            // 无标准流时忽略
        }
    }
}
