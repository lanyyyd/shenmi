using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using Windows.Foundation;
using Windows.System;

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
    }

    /// <summary>等待用户框选。返回 null 表示取消。</summary>
    public Task<ScreenRect?> PickAsync() => _tcs.Task;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = e.GetCurrentPoint(Root).Position;
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

        SizeText.Text = $"{(int)w} × {(int)h}";

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

        int x = (int)Math.Round(Math.Min(_start.X, p.X)) + _virtualScreen.X;
        int y = (int)Math.Round(Math.Min(_start.Y, p.Y)) + _virtualScreen.Y;
        int w = (int)Math.Round(Math.Abs(p.X - _start.X));
        int h = (int)Math.Round(Math.Abs(p.Y - _start.Y));

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

    private void Finish(ScreenRect? rect)
    {
        if (_done)
            return;
        _done = true;

        _tcs.TrySetResult(rect);
        Close();
    }
}
