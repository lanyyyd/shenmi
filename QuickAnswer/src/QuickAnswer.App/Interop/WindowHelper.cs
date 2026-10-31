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

    public static void Resize(Window window, int width, int height)
    {
        try
        {
            AppWindowOf(window).Resize(new SizeInt32(width, height));
        }
        catch
        {
        }
    }

    public static void CenterOnScreen(Window window, int width, int height)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            int x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
            int y = area.WorkArea.Y + (area.WorkArea.Height - height) / 2;
            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
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
                p.IsBorderless = true;
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

    public static void MoveTo(Window window, int left, int top, int width, int height)
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

    private static readonly List<SubclassProc> _subclassProcs = new();
}
