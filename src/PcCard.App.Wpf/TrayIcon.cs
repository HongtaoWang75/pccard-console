using Forms = System.Windows.Forms;

namespace PcCard.AppWpf;

/// <summary>托盘图标（WinForms NotifyIcon，RDP/Session 2 下稳定）。</summary>
public sealed class TrayIcon
{
    private readonly Action _show;
    private readonly Action _refresh;
    private readonly Action _exit;
    private Forms.NotifyIcon? _icon;

    public TrayIcon(Action show, Action refresh, Action exit)
    {
        _show = show;
        _refresh = refresh;
        _exit = exit;
    }

    public void Install()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => _show());
        menu.Items.Add("立即刷新", null, (_, _) => _refresh());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());

        _icon = new Forms.NotifyIcon
        {
            Text = "开机卡控制台",
            Visible = true,
            ContextMenuStrip = menu,
        };

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            // 取不到 exe 图标就不显示（托盘仍可用）
        }

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _show();
        };
        _icon.DoubleClick += (_, _) => _show();
    }

    public void Uninstall()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
