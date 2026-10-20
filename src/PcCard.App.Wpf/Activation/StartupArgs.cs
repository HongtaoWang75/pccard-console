namespace PcCard.AppWpf.Activation;

/// <summary>启动参数（与 WinUI 版保持一致）。</summary>
public sealed class StartupArgs
{
    /// <summary>--tray：启动即隐藏进托盘（安装器自启用）。</summary>
    public bool StartHidden { get; init; }

    /// <summary>--selfcheck：启动后自渲染截图 + 打印诊断，用于远程（RDP 断开态）验证。</summary>
    public bool SelfCheck { get; init; }

    /// <summary>--png &lt;path&gt;：自检截图落盘路径。</summary>
    public string? PngPath { get; init; }

    /// <summary>--stay：保留参数兼容（WPF 版自检完成即退）。</summary>
    public bool Stay { get; init; }

    /// <summary>--timeout &lt;sec&gt;：自检等待数据/布局的最长秒数。</summary>
    public int TimeoutSec { get; init; } = 20;

    public static StartupArgs Parse(string[] args)
    {
        return new StartupArgs
        {
            StartHidden = args.Contains("--tray"),
            SelfCheck = args.Contains("--selfcheck"),
            Stay = args.Contains("--stay"),
            PngPath = GetOpt(args, "--png"),
            TimeoutSec = GetOpt(args, "--timeout") is { } t && int.TryParse(t, out var v) && v > 0 ? v : 20,
        };
    }

    /// <summary>转发给首实例的单行消息。</summary>
    public string ToActivationMessage() => SelfCheck ? $"selfcheck|{PngPath}|{Stay}" : "show";

    /// <summary>解析转发来的单行消息。</summary>
    public static StartupArgs FromActivationMessage(string message)
    {
        var parts = message.Split('|');
        return parts[0] switch
        {
            "selfcheck" => new StartupArgs
            {
                SelfCheck = true,
                PngPath = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                Stay = parts.Length > 2 && parts[2].Equals("true", StringComparison.OrdinalIgnoreCase),
            },
            _ => new StartupArgs(),
        };
    }

    private static string? GetOpt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
