using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Interop;

/// <summary>
/// 窗口层面的「隐身」与「不吃鼠标」。
///
/// 防截屏用 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)：让 DWM 在合成画面时
/// 直接跳过这个窗口。走 DWM 输出的捕获路径（桌面复制、BitBlt、系统截图、OBS 显示器采集）
/// 拿到的画面里这个窗口根本不存在 —— 不是黑块，是完全透明。
///
/// 与 JiYuTrainer 那套「注入 StudentMain.exe 去 hook 截图 API」不同：这里不碰任何
/// 其他进程，一行官方 API 搞定，也不会被杀软当成注入行为拦下来。
/// </summary>
public static class WindowHelper
{
    public static IntPtr Hwnd(Window window) => WindowNative.GetWindowHandle(window);

    public static AppWindow AppWindowOf(Window window)
    {
        var hwnd = Hwnd(window);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
    }

    /// <summary>从 Alt+Tab 与任务栏里藏起来。</summary>
    public static void HideFromSwitcher(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// 开启防截屏。必须在 HWND 有效之后调用。
    /// 每个顶层 Window 都要单独调用 —— WinUI 3 里主窗口、悬浮窗、标注层各自是独立 HWND，
    /// 漏掉任何一个都会在截图里露出来。
    /// </summary>
    public static bool ProtectFromCapture(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return false;

        if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            return true;

        // 低于 Win10 2004 时该标志无效，退化为「黑块」模式（仍然挡住内容，只是会露馅）
        return SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    public static void DisableProtection(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
    }

    /// <summary>
    /// 逻辑像素 → 物理像素。
    ///
    /// AppWindow.Resize / MoveAndResize 吃的全是物理像素。高分屏（125%/150%/200%）下
    /// 把逻辑值直接传进去，窗口就只有应有大小的 1/1.25 ~ 1/2，
    /// 表现就是「打开一个小窗，界面被裁掉，最大化才看得全」。
    /// </summary>
    public static int ToPhysical(Window window, int logical)
    {
        try
        {
            var hwnd = Hwnd(window);
            uint dpi = hwnd == IntPtr.Zero ? 96 : GetDpiForWindow(hwnd);
            if (dpi == 0)
                dpi = 96;
            return (int)Math.Round(logical * dpi / 96.0);
        }
        catch
        {
            return logical;
        }
    }

    /// <summary>当前窗口所在显示器的缩放倍率（1.0 = 100%，1.5 = 150%）。</summary>
    public static double ScaleFactor(Window window)
    {
        try
        {
            var hwnd = Hwnd(window);
            uint dpi = hwnd == IntPtr.Zero ? 96 : GetDpiForWindow(hwnd);
            if (dpi == 0)
                dpi = 96;
            return dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 让窗口真正透出下层内容（不许画主题背景色）。
    /// 只对「标注层」这类需要浮在别人窗口上的场景用；必须在 HWND 有效后、显示前调用。
    /// </summary>
    public static void EnableTransparency(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            // 整个客户区当成 DWM 玻璃
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 去掉标题栏/边框残留（否则四周会有一圈不透明边）
            var style = GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER);
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }
        catch
        {
        }
    }

    /// <summary>按逻辑像素设置窗口大小。</summary>
    public static void Resize(Window window, int logicalWidth, int logicalHeight)
    {
        try
        {
            AppWindowOf(window).Resize(new SizeInt32(
                ToPhysical(window, logicalWidth),
                ToPhysical(window, logicalHeight)));
        }
        catch
        {
        }
    }

    /// <summary>按逻辑像素设置窗口大小并居中。</summary>
    public static void CenterOnScreen(Window window, int logicalWidth, int logicalHeight)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);

            int w = ToPhysical(window, logicalWidth);
            int h = ToPhysical(window, logicalHeight);

            // 小屏幕（或高缩放）下别让窗口比工作区还高，否则底部按钮点不到
            if (h > area.WorkArea.Height - 24)
                h = area.WorkArea.Height - 24;
            if (w > area.WorkArea.Width - 24)
                w = area.WorkArea.Width - 24;

            int x = area.WorkArea.X + (area.WorkArea.Width - w) / 2;
            int y = area.WorkArea.Y + (area.WorkArea.Height - h) / 2;
            appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        }
        catch
        {
        }
    }

    /// <summary>无边框、不可缩放、置顶的悬浮窗样式。</summary>
    public static void MakeOverlay(Window window)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            if (appWindow.Presenter is OverlappedPresenter p)
            {
                // WinAppSDK 的 OverlappedPresenter 没有 IsBorderless，
                // 去边框和标题栏要走 SetBorderAndTitleBar。
                p.SetBorderAndTitleBar(false, false);
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.IsAlwaysOnTop = true;
            }
            appWindow.IsShownInSwitchers = false;
        }
        catch
        {
        }
    }

    /// <summary>按逻辑像素移动并设置大小（left/top 是物理屏幕坐标，width/height 是逻辑尺寸）。</summary>
    public static void MoveTo(Window window, int left, int top, int logicalWidth, int logicalHeight)
    {
        try
        {
            AppWindowOf(window).MoveAndResize(new RectInt32(
                left, top, ToPhysical(window, logicalWidth), ToPhysical(window, logicalHeight)));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 直接按物理像素摆放。全屏遮罩层专用 —— 它的尺寸来自虚拟桌面（已经是物理像素），
    /// 再按 DPI 转一次会放大成 1.5 倍，盖不住屏幕。
    /// </summary>
    public static void MoveToPhysical(Window window, int left, int top, int width, int height)
    {
        try
        {
            AppWindowOf(window).MoveAndResize(new RectInt32(left, top, width, height));
        }
        catch
        {
        }
    }

    public static RectInt32 WorkArea(Window window)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            return area.WorkArea;
        }
        catch
        {
            return new RectInt32(0, 0, 1920, 1080);
        }
    }

    /// <summary>
    /// 让窗口对鼠标完全透明 —— 鼠标点下去会落到下面的题目软件上。
    ///
    /// 不能用 WS_EX_TRANSPARENT：WinUI 3 的内容走 DirectComposition 合成，
    /// 加了这个样式经常表现为「穿透生效但界面也不刷新」或者干脆没反应。
    /// 可靠做法是子类化 WndProc，在 WM_NCHITTEST 里直接返回 HTTRANSPARENT。
    /// </summary>
    public static void EnableClickThrough(Window window, Func<bool> isEnabled)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        SubclassProc proc = (h, msg, w, l, id, data) =>
        {
            if (msg == WM_NCHITTEST && isEnabled())
                return new IntPtr(HTTRANSPARENT);

            return DefSubclassProc(h, msg, w, l);
        };

        // 委托必须被强引用，否则会被 GC 回收，回调变成野指针直接崩进程
        _subclassProcs.Add(proc);
        SetWindowSubclass(hwnd, proc, (UIntPtr)1, IntPtr.Zero);
    }

    /// <summary>
    /// 显示窗口但不抢焦点。标注层必须用这个 ——
    /// 用 Activate() 会把键盘焦点从题目软件抢走，用户打字直接断掉。
    /// </summary>
    public static void ShowNoActivate(Window window)
    {
        try
        {
            var hwnd = Hwnd(window);
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }
        catch
        {
        }
    }

    /// <summary>禁止窗口擦除背景，否则每帧都会先用主题色刷一遍，透明就失效了。</summary>
    public static void SuppressEraseBackground(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        SubclassProc proc = (h, msg, w, l, id, data) =>
        {
            if (msg == WM_ERASEBKGND)
                return new IntPtr(1);

            return DefSubclassProc(h, msg, w, l);
        };

        _subclassProcs.Add(proc);
        SetWindowSubclass(hwnd, proc, (UIntPtr)7, IntPtr.Zero);
    }

    private static readonly List<SubclassProc> _subclassProcs = new();
}
