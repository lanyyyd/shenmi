using System.Runtime.InteropServices;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 任务栏托盘图标（Shell_NotifyIcon）。
/// 无打包 WinUI 没有内置托盘 API，这里直接用 Win32：图标回调消息发到主窗口，
/// 左键恢复窗口、右键弹「显示主界面 / 退出」菜单。
/// </summary>
internal static class TrayIconService
{
    /// <summary>托盘图标回调（发到主窗口，lParam 低字是鼠标消息）。</summary>
    public const uint WmTrayIcon = 0x8000 + 3;

    /// <summary>右键菜单命令（wParam 是菜单项 id）。</summary>
    public const uint WmTrayCommand = 0x8000 + 4;

    public const int CommandShow = 1;
    public const int CommandExit = 2;

    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;

    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;

    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;

    private static nint _icon;
    private static bool _visible;

    public static void Show(nint hwnd, string tooltip)
    {
        if (hwnd == 0)
        {
            return;
        }

        var data = CreateData(hwnd, tooltip);
        if (_visible)
        {
            _icon = _icon == 0 ? LoadAppIcon() : _icon;
            data.hIcon = _icon;
            Shell_NotifyIconW(NimAdd, ref data);
            return;
        }

        _icon = LoadAppIcon();
        data.hIcon = _icon;
        _visible = Shell_NotifyIconW(NimAdd, ref data);
    }

    public static void Hide(nint hwnd)
    {
        if (!_visible || hwnd == 0)
        {
            return;
        }

        var data = CreateData(hwnd, string.Empty);
        Shell_NotifyIconW(NimDelete, ref data);
        _visible = false;
    }

    /// <summary>处理托盘图标回调；返回 true 表示消息已被消费。</summary>
    public static bool HandleCallback(nint hwnd, nint lParam, out bool restoreWindow)
    {
        restoreWindow = false;
        var evt = (uint)(lParam.ToInt64() & 0xFFFF);

        if (evt is WmLButtonUp or WmLButtonDblClk)
        {
            restoreWindow = true;
            return true;
        }

        if (evt == WmRButtonUp)
        {
            ShowContextMenu(hwnd);
            return true;
        }

        return false;
    }

    private static void ShowContextMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, CommandShow, Loc.T("Tray_Show"));
            AppendMenuW(menu, MfSeparator, 0, string.Empty);
            AppendMenuW(menu, MfString, CommandExit, Loc.T("Tray_Exit"));

            GetCursorPos(out var point);

            // 不先抢前台，菜单会在点击别处时不消失（Win32 的经典要求）。
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenu(
                menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, hwnd, 0);

            if (command != 0)
            {
                PostMessageW(hwnd, WmTrayCommand, (nint)command, 0);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static unsafe NotifyIconData CreateData(nint hwnd, string tooltip)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = _icon
        };

        var limit = Math.Min(tooltip.Length, 127);
        for (var i = 0; i < limit; i++)
        {
            data.szTip[i] = tooltip[i];
        }

        data.szTip[limit] = '\0';
        return data;
    }

    private static nint LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return 0;
            }

            // 从 exe 内嵌图标里取 16x16 那个，和任务栏/开始菜单一致。
            return ExtractIconW(GetModuleHandleW(null), exe, 0);
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint hInst, string exeFileName, int iconIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, uint flags, nint itemId, string item);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint hMenu, uint flags, int x, int y, int reserved, nint hWnd, nint rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
