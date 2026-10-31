using System.Runtime.InteropServices;

namespace QuickAnswer.App.Interop;

internal static class NativeMethods
{
    // ---- SetWindowDisplayAffinity ----
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;   // Windows 10 2004+

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // ---- 扩展窗口样式 ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- 窗口子类化（拦 WM_NCHITTEST，实现点击穿透 / 拖动）----
    public delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_DPICHANGED = 0x02E0;
    public const int HTTRANSPARENT = -1;
    public const int HTCAPTION = 2;
    public const int DWLP_MSGRESULT = 0;

    // ---- 全局热键 ----
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 显示但不抢焦点 ----
    public const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ---- 让 WinUI 3 窗口真正透明 ----
    // 原生 WinUI 3 没有 TransparentTintBackdrop（那是 WinUIEx 的东西）。
    // 不设 backdrop 时窗口会画一层主题纯色，浅色主题下就是整块白 —— 这正是
    // 「开隐蔽模式直接白屏」的根因。做法与 WinUIEx 一致：
    //   1) DwmExtendFrameIntoClientArea 把整个客户区标记成 DWM 玻璃
    //   2) 去掉 WS_CAPTION / WS_THICKFRAME / WS_BORDER
    //   3) 拦 WM_ERASEBKGND 返回 1，禁止窗口擦背景
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_BORDER = 0x00800000;
    public const uint WM_ERASEBKGND = 0x0014;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    // ---- DPI ----
    // 必须用这个：AppWindow.Resize 吃的是物理像素，高分屏上直接把逻辑尺寸传进去，
    // 150% 缩放下窗口只有应有大小的 2/3，内容会整体溢出（用户看到的就是"窗口很小、显示不全"）。
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---- DWM 圆角（Win11）。Win10 上调用失败，忽略即可 ----
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
