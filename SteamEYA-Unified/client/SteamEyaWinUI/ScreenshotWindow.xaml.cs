using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using System.Runtime.InteropServices.WindowsRuntime;

namespace SteamEyaWinUI;

/// <summary>
/// 全屏截图覆盖层：拖拽框选 → Enter/对勾复制，或另存为；Esc/叉号取消。
/// 交互对齐微信截图：开始框选后选区外压暗、选区内保持原图，工具条贴着选区右下角。
/// </summary>
public sealed partial class ScreenshotWindow : Window
{
    private readonly ScreenshotFrame _frame;

    private Point _startPoint;
    private Rect _selection;
    private bool _selecting;
    private bool _hasSelection;
    private bool _busy;
    private bool _chromeRemoved;

    internal ScreenshotWindow(ScreenshotFrame frame)
    {
        _frame = frame;
        InitializeComponent();

        Title = "SteamEYA 截图";

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // 仅靠这个还不够：WPS/播放器等会把自己的置顶窗口再压上来，BringToFront 里还会补一次 Win32 置顶。
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.MoveAndResize(new RectInt32(0, 0, frame.Width, frame.Height));

        // WinUI 3 的窗口即使 SetBorderAndTitleBar(false,false) 仍会带 1px 边框和 Win11 圆角，
        // 截图时会在屏幕边缘露出一条线，这里去掉 DWM 边框/圆角并改成 WS_POPUP 无边框窗口。
        RemoveWindowChrome();

        CopyLabel.Text = Loc.T("Screenshot_Copy");
        SaveLabel.Text = Loc.T("Screenshot_Save");
        HintText.Text = Loc.T("Screenshot_Hint");

        // 在窗口 Activate 之前就把画面填好，避免出现「先黑屏半秒再显示截图」。
        ShowFrame();
    }

    /// <summary>
    /// 强制把自己拉到最前：OverlappedPresenter.IsAlwaysOnTop 只设置窗口风格，
    /// 遇到 WPS 这类自带置顶窗口的前台程序仍会被盖住，这里用 Win32 再压一次并抢焦点。
    /// </summary>
    internal void BringToFront()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == 0)
            {
                return;
            }

            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);

            // 前台锁：SetForegroundWindow 对非前台进程可能被系统忽略，
            // 先 BringWindowToTop 再 SetForegroundWindow，两个都失败时至少置顶生效。
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
            Root.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"截图窗口置顶失败：{ex.Message}");
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Focus(FocusState.Programmatic);

        // 构造时窗口句柄偶尔还没就绪，Loaded 时补一次去边框。
        if (!_chromeRemoved)
        {
            RemoveWindowChrome();
        }

        // 二次置顶：窗口刚显示时前台应用可能还在抢焦点，延迟一帧再压一次。
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, BringToFront);
    }

    /// <summary>
    /// 直接把抓到的 BGRA 像素写进 WriteableBitmap，不再走「PNG 编码 → 解码」。
    /// 原先那条链路要几百毫秒，窗口已经 Activate，用户会先看到黑屏再看到图。
    /// </summary>
    private void ShowFrame()
    {
        var pixels = _frame.Pixels;
        // 屏幕 DIB 的 alpha 通常为 0，WriteableBitmap 按预乘透明度渲染会整张透明，必须置为不透明。
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var bitmap = new WriteableBitmap(_frame.Width, _frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        bitmap.Invalidate();
        ShotImage.Source = bitmap;
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasSelection)
        {
            UpdateSelection(_selection);
            PlaceToolbar(_selection);
        }
    }

    // ---------- 框选 ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _startPoint = point.Position;
        _selecting = true;
        _hasSelection = false;
        Toolbar.Visibility = Visibility.Collapsed;
        MaskFull.Visibility = Visibility.Collapsed;

        UpdateSelection(new Rect(_startPoint, new Size(0, 0)));
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        var current = e.GetCurrentPoint(Root).Position;
        var rect = new Rect(
            Math.Min(_startPoint.X, current.X),
            Math.Min(_startPoint.Y, current.Y),
            Math.Abs(current.X - _startPoint.X),
            Math.Abs(current.Y - _startPoint.Y));

        UpdateSelection(rect);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        Root.ReleasePointerCapture(e.Pointer);

        _hasSelection = _selection.Width >= 4 && _selection.Height >= 4;
        if (_hasSelection)
        {
            PlaceToolbar(_selection);
            Toolbar.Visibility = Visibility.Visible;
        }
        else
        {
            // 只是点了一下没拖动：回到"未选择"状态，等待重新框选。
            ResetSelection();
        }

        e.Handled = true;
    }

    private void UpdateSelection(Rect rect)
    {
        _selection = rect;

        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        rect = new Rect(
            Math.Clamp(rect.X, 0, width),
            Math.Clamp(rect.Y, 0, height),
            Math.Clamp(rect.Width, 0, width - Math.Clamp(rect.X, 0, width)),
            Math.Clamp(rect.Height, 0, height - Math.Clamp(rect.Y, 0, height)));

        // 上、下、左、右四块压暗，中间保持原亮度（等价于在遮罩上"挖洞"）。
        MaskTop.Width = width;
        MaskTop.Height = rect.Y;
        Canvas.SetLeft(MaskTop, 0);
        Canvas.SetTop(MaskTop, 0);

        MaskBottom.Width = width;
        MaskBottom.Height = Math.Max(0, height - rect.Y - rect.Height);
        Canvas.SetLeft(MaskBottom, 0);
        Canvas.SetTop(MaskBottom, rect.Y + rect.Height);

        MaskLeft.Width = rect.X;
        MaskLeft.Height = rect.Height;
        Canvas.SetLeft(MaskLeft, 0);
        Canvas.SetTop(MaskLeft, rect.Y);

        MaskRight.Width = Math.Max(0, width - rect.X - rect.Width);
        MaskRight.Height = rect.Height;
        Canvas.SetLeft(MaskRight, rect.X + rect.Width);
        Canvas.SetTop(MaskRight, rect.Y);

        foreach (var mask in new[] { MaskTop, MaskBottom, MaskLeft, MaskRight })
        {
            mask.Visibility = Visibility.Visible;
        }

        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        Toolbar.Visibility = Visibility.Collapsed;
        MaskFull.Visibility = Visibility.Visible;

        foreach (var mask in new[] { MaskTop, MaskBottom, MaskLeft, MaskRight })
        {
            mask.Visibility = Visibility.Collapsed;
        }

        SelectionBorder.Visibility = Visibility.Collapsed;
    }

    private void PlaceToolbar(Rect rect)
    {
        const double ToolbarWidth = 240;
        const double ToolbarHeight = 48;
        const double Gap = 8;

        var left = Math.Min(rect.X + rect.Width - ToolbarWidth, Root.ActualWidth - ToolbarWidth - Gap);
        left = Math.Max(Gap, left);

        var top = rect.Y + rect.Height + Gap;
        if (top + ToolbarHeight > Root.ActualHeight)
        {
            top = Math.Max(Gap, rect.Y - ToolbarHeight - Gap);
        }

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    // ---------- 键盘 ----------

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private async void OnEnterInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_hasSelection && !_busy)
        {
            args.Handled = true;
            await CopyAsync();
        }
    }

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter && _hasSelection && !_busy)
        {
            e.Handled = true;
            await CopyAsync();
        }
    }

    // ---------- 动作 ----------

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            await CopyAsync();
        }
    }

    private async Task CopyAsync()
    {
        var png = await BuildPngAsync();
        if (png is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var ok = await ScreenshotService.CopyToClipboardAsync(png);
            AppState.ShowStatus(
                Loc.T(ok ? "Screenshot_Copied" : "Screenshot_CopyFailed"),
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);

            if (ok)
            {
                Close();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_hasSelection)
        {
            return;
        }

        _busy = true;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"SteamEYA_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });

            // 无打包 WinUI：保存对话框必须显式绑定窗口句柄。
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var png = await BuildPngAsync();
            if (png is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(file.Path, png);
            AppState.ShowStatus(Loc.T("Screenshot_Saved"), InfoBarSeverity.Success);
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存截图失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("Screenshot_SaveFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 去掉窗口边框、圆角和任务栏入口。仅靠 OverlappedPresenter 的设置仍会留下
    /// 1px 系统边框和 Win11 圆角，全屏截图时会在屏幕四周露出缝隙。
    /// </summary>
    private void RemoveWindowChrome()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == 0)
            {
                return;
            }

            // Win11：禁止圆角、边框颜色设为“无”。
            var noRound = 1; // DWMWCP_DONOTROUND
            _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref noRound, sizeof(int));
            var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));

            // Win10 及部分显卡驱动下 DWM 边框仍会绘制，退回到纯 WS_POPUP 无边框窗口。
            var style = (long)GetWindowLongPtr(hwnd, GwlStyle);
            style &= ~(WsCaption | WsThickFrame | WsSystemMenu | WsMinimizeBox | WsMaximizeBox);
            style |= WsPopup;
            _ = SetWindowLongPtr(hwnd, GwlStyle, (nint)style);

            var exStyle = (long)GetWindowLongPtr(hwnd, GwlExStyle);
            exStyle |= WsExToolWindow;                                  // 不出现在任务栏 / Alt+Tab
            exStyle &= ~(long)WsExAppWindow;                            // 从任务栏移除
            exStyle &= ~((long)WsExClientEdge | (long)WsExWindowEdge);  // 去掉凹陷/凸起边
            _ = SetWindowLongPtr(hwnd, GwlExStyle, (nint)exStyle);

            _ = SetWindowPos(hwnd, 0, 0, 0, _frame.Width, _frame.Height,
                SwpNoZorder | SwpFrameChanged | SwpShowWindow);
            _chromeRemoved = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"去除截图窗口边框失败：{ex.Message}");
        }
    }

    private const int HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSystemMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExWindowEdge = 0x00000100L;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    /// <summary>按当前选区裁出像素并编码成 PNG；选区无效时返回 null。</summary>
    private async Task<byte[]?> BuildPngAsync()
    {
        if (!_hasSelection)
        {
            return null;
        }

        var scale = _frame.Scale <= 0 ? 1.0 : _frame.Scale;
        var x = (int)Math.Round(_selection.X * scale);
        var y = (int)Math.Round(_selection.Y * scale);
        var width = (int)Math.Round(_selection.Width * scale);
        var height = (int)Math.Round(_selection.Height * scale);

        if (width < 1 || height < 1)
        {
            return null;
        }

        try
        {
            var cropped = ScreenshotService.Crop(_frame, x, y, width, height);
            return await ScreenshotService.EncodePngAsync(cropped, width, height);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"生成截图失败：{ex.Message}");
            AppState.ShowStatus(Loc.T("Screenshot_CopyFailed"), InfoBarSeverity.Error);
            return null;
        }
    }
}
