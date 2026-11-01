using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.System;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Views;

/// <summary>
/// 全屏框选器。整块半透明遮罩上拖一个矩形，返回它的**绝对屏幕坐标**（物理像素）。
/// 返回值直接进 AppConfig.Region，后续截图、OCR 坐标换算都以它为基准。
/// </summary>
public sealed partial class RegionSelectorWindow : Window
{
    private readonly TaskCompletionSource<ScreenRect?> _tcs = new();
    private readonly ScreenRect _virtualScreen;

    private Point _start;
    private bool _dragging;
    private bool _done;

    /// <summary>DIP → 物理像素的比值。按下时量一次，整次框选用同一个值。</summary>
    private double _scale = 1.0;

    public RegionSelectorWindow()
    {
        InitializeComponent();

        Title = "框选题目区域";
        _virtualScreen = ScreenCapturer.VirtualScreen();

        WindowHelper.MakeOverlay(this);
        WindowHelper.HideFromSwitcher(this);
        WindowHelper.MoveToPhysical(this, _virtualScreen.X, _virtualScreen.Y,
            _virtualScreen.Width, _virtualScreen.Height);

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.KeyDown += OnKeyDown;

        Activated += (_, _) => Root.Focus(FocusState.Programmatic);
        Closed += (_, _) => _tcs.TrySetResult(null);

        // 先把截图贴上去再 Activate，避免用户看到一个纯黑的窗口闪一下。
        _ = ShowWithBackdropAsync();
    }

    /// <summary>
    /// 截一张全屏图铺在窗口里，用户看到的是「冻结的屏幕」，可以慢慢框。
    /// 这也顺手解决了「窗口不透明，框选时看不到屏幕内容」的问题 ——
    /// WinUI 3 的窗口没法简单做到全透明，那就干脆不透明，用截图假装。
    /// </summary>
    private async Task ShowWithBackdropAsync()
    {
        try
        {
            var shot = ScreenCapturer.Capture(_virtualScreen);
            if (shot is not null)
            {
                var buffer = CryptographicBuffer.CreateFromByteArray(shot.Bgra);
                var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    buffer, BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Ignore);

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                Backdrop.Source = source;
            }
        }
        catch
        {
            // 截图失败就留着黑底，仍然能框选，只是看不见内容
        }
        finally
        {
            Activate();
        }
    }

    /// <summary>等待用户框选。返回 null 表示取消。</summary>
    public Task<ScreenRect?> PickAsync() => _tcs.Task;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = e.GetCurrentPoint(Root).Position;
        _scale = DipToPixel();
        _dragging = true;

        Root.CapturePointer(e.Pointer);

        SelRect.Visibility = Visibility.Visible;
        SizeHint.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelRect, _start.X);
        Canvas.SetTop(SelRect, _start.Y);
        SelRect.Width = 0;
        SelRect.Height = 0;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        var p = e.GetCurrentPoint(Root).Position;
        double x = Math.Min(_start.X, p.X);
        double y = Math.Min(_start.Y, p.Y);
        double w = Math.Abs(p.X - _start.X);
        double h = Math.Abs(p.Y - _start.Y);

        Canvas.SetLeft(SelRect, x);
        Canvas.SetTop(SelRect, y);
        SelRect.Width = w;
        SelRect.Height = h;

        SizeText.Text = $"{(int)Math.Round(w * _scale)} × {(int)Math.Round(h * _scale)}";

        // 提示框放在选框上方，靠近顶部时改放下方
        double hintTop = y - 30;
        if (hintTop < 4)
            hintTop = y + h + 6;

        SizeHint.Margin = new Thickness(Math.Max(4, x), hintTop, 0, 0);
        SizeHint.HorizontalAlignment = HorizontalAlignment.Left;
        SizeHint.VerticalAlignment = VerticalAlignment.Top;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        var p = e.GetCurrentPoint(Root).Position;

        // 鼠标位置是 DIP，屏幕坐标是物理像素 —— 这里必须换算。
        // 150%/200% 缩放下直接拿 DIP 当物理用，框出来的矩形会缩到左上角一半大小，
        // 用户框的是这道题，截出来的是隔壁的"温馨提示"，看起来就像彻底错位。
        double k = _scale > 0 ? _scale : DipToPixel();

        int x = (int)Math.Round(Math.Min(_start.X, p.X) * k) + _virtualScreen.X;
        int y = (int)Math.Round(Math.Min(_start.Y, p.Y) * k) + _virtualScreen.Y;
        int w = (int)Math.Round(Math.Abs(p.X - _start.X) * k);
        int h = (int)Math.Round(Math.Abs(p.Y - _start.Y) * k);

        // 太小说明是误点，不当成框选
        if (w < 40 || h < 24)
        {
            SelRect.Visibility = Visibility.Collapsed;
            SizeHint.Visibility = Visibility.Collapsed;
            return;
        }

        Finish(new ScreenRect(x, y, w, h));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
            Finish(null);
    }

    /// <summary>
    /// DIP → 物理像素的比值。
    ///
    /// 优先量「窗口客户端物理宽度 / XAML 根元素 DIP 宽度」：这个比值就是内容实际被
    /// 拉伸的倍数，多屏不同缩放时也比 GetDpiForWindow 可靠（WinUI 3 的窗口横跨多屏
    /// 时只按它所在那块屏的 DPI 缩放全部内容）。
    /// 量不到再退回 GetDpiForWindow/96，最后兜底 1.0。
    /// </summary>
    private double DipToPixel()
    {
        try
        {
            double dipW = Root.ActualWidth;
            double physW = 0;

            try
            {
                physW = WindowHelper.AppWindowOf(this).ClientSize.Width;
            }
            catch
            {
                physW = _virtualScreen.Width;
            }

            if (physW <= 1)
                physW = _virtualScreen.Width;

            if (dipW > 1 && physW > 1)
            {
                double ratio = physW / dipW;
                if (ratio is > 0.5 and < 4)
                    return ratio;
            }
        }
        catch
        {
        }

        try
        {
            var hwnd = WindowHelper.Hwnd(this);
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 48)
                    return dpi / 96.0;
            }
        }
        catch
        {
        }

        return 1.0;
    }

    private void Finish(ScreenRect? rect)
    {
        if (_done)
            return;
        _done = true;

        _tcs.TrySetResult(rect);
        Close();
    }
}
