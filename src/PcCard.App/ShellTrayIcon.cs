using System.Runtime.InteropServices;
using PcCard.Core.Logging;

namespace PcCard.App;

/// <summary>
/// 纯 P/Invoke 托盘图标（Shell_NotifyIcon）。
///
/// ★ 刻意不引 H.NotifyIcon.WinUI：多一个 NuGet 依赖与版本窗口（本机缓存里没有，
///   还要对齐 WASDK 2.5 的模块化包），而我们要的只是「图标 + 右键菜单 + 气泡」。
///   自带 message-only 窗口的实现约 150 行，还更省内存（目标机 WARP/小内存）。
///
/// 在独立 STA 后台线程上跑消息循环；所有动作经委托 marshal 回 UI 线程。
/// </summary>
public sealed class ShellTrayIcon
{
    private readonly Action _onShow;
    private readonly Action _onRefresh;
    private readonly Action _onExit;
    private readonly FileLog _log = new("tray");

    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProcDelegate; // 必须保引用，否则 GC 掉回调就崩
    private uint _wmTaskbarCreated;
    private bool _uninstalled;

    public ShellTrayIcon(Action show, Action refresh, Action exit)
    {
        _onShow = show;
        _onRefresh = refresh;
        _onExit = exit;
    }

    public void Install()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "PcCard.Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Uninstall()
    {
        _uninstalled = true;
        if (_hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_APP_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadMain()
    {
        try
        {
            var hInstance = GetModuleHandleW(null);
            _wndProcDelegate = WndProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = "PcCardTrayWnd",
            };
            RegisterClassExW(ref wc);

            _hwnd = CreateWindowExW(0, "PcCardTrayWnd", "", 0, 0, 0, 0, 0,
                new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _log.Error("托盘窗口创建失败");
                return;
            }

            _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            AddIcon();

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("托盘线程异常", ex);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
        {
            AddIcon(); // 资源管理器重启后重挂图标
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_TRAY:
            {
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                switch (mouse)
                {
                    case WM_LBUTTONUP:
                        _onShow();
                        break;
                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        break;
                }
                break;
            }

            case WM_COMMAND:
            {
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case MenuShow:
                        _onShow();
                        break;
                    case MenuRefresh:
                        _onRefresh();
                        break;
                    case MenuExit:
                        _onExit();
                        break;
                }
                break;
            }

            case WM_APP_CLOSE:
                RemoveIcon();
                DestroyWindowW(hWnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, MenuShow, "显示主界面");
        AppendMenuW(menu, MF_STRING, MenuRefresh, "刷新状态");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, MenuExit, "退出");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // 不这么做右键菜单点外面不消失
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); // 经典修复：菜单关闭后的空消息
        DestroyMenu(menu);

        switch (cmd)
        {
            case MenuShow:
                _onShow();
                break;
            case MenuRefresh:
                _onRefresh();
                break;
            case MenuExit:
                _onExit();
                break;
        }
    }

    private void AddIcon()
    {
        var data = NewNotifyData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = LoadTrayIcon();
        data.szTip = "开机卡控制台";
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
            _log.Warn("Shell_NotifyIcon(NIM_ADD) 失败");

        // Win10/11 用版本 4（不写也行，但 v4 的悬浮/定位行为更规范）
        var ver = NewNotifyData();
        ver.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref ver);
    }

    private void RemoveIcon()
    {
        if (_uninstalled || _hwnd == IntPtr.Zero) { /* 仍尝试 */ }
        var data = NewNotifyData();
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    private NOTIFYICONDATAW NewNotifyData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var n = ExtractIconExW(exe, 0, out var large, out _, 1);
                if (n > 0 && large != IntPtr.Zero) return large;
            }
        }
        catch
        {
            // 落到系统默认图标
        }
        return LoadIconW(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    // ═══════════════════════ P/Invoke ═══════════════════════

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_APP_CLOSE = WM_APP + 2;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_NULL = 0;
    private const int HWND_MESSAGE = -3;

    private const int MenuShow = 1;
    private const int MenuRefresh = 2;
    private const int MenuExit = 3;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int IDI_APPLICATION = 32512;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindowW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
