using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Models;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Views;

/// <summary>
/// 小型答案悬浮窗。默认停靠屏幕右下角，可拖动，双击收起成一条。
/// 尺寸刻意做小（232×136）：悬浮窗越大，挡住题目本身的概率越高。
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const int FullWidth = 232;
    private const int FullHeight = 136;
    private const int CompactHeight = 44;

    private bool _compact;
    private bool _ready;
    private double _opacity = 0.30;

    public OverlayWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);

        WindowHelper.MakeOverlay(this);
        WindowHelper.HideFromSwitcher(this);
        ApplyCorner();

        PlaceBottomRight();
        WindowHelper.EnableClickThrough(this, () => App.Config.ClickThrough);

        Root.DoubleTapped += OnDoubleTapped;
        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_ready)
            return;
        _ready = true;

        if (App.Config.ProtectFromCapture)
            WindowHelper.ProtectFromCapture(this);
    }

    private void ApplyCorner()
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(WindowHelper.Hwnd(this), DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
        }
    }

    private void PlaceBottomRight()
    {
        try
        {
            // WorkArea 是物理像素，FullWidth 是设计稿里的逻辑像素，两者不能直接相减，
            // 否则 150% 缩放下悬浮窗会跑到屏幕外面去。
            var work = WindowHelper.WorkArea(this);
            int w = WindowHelper.ToPhysical(this, FullWidth);
            int h = WindowHelper.ToPhysical(this, FullHeight);
            int margin = WindowHelper.ToPhysical(this, 16);

            int left = work.X + work.Width - w - margin;
            int top = work.Y + work.Height - h - margin;

            WindowHelper.MoveTo(this, left, top, FullWidth, FullHeight);
        }
        catch
        {
        }
    }

    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0.1, 1.0);
        ApplyBackground();
    }

    private void ApplyBackground()
    {
        // 只调背景 alpha，文字保持不透明 —— 否则题看不清，答案也看不清
        byte alpha = (byte)Math.Round(255 * Math.Clamp(_opacity, 0.10, 0.95));
        Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 30, 36, 48));
    }

    public void Render(AnswerResult result)
    {
        bool choiceLike = result.TargetOptions.Count > 0 || result.Letter.Length > 0;

        SourceLabel.Text = result.Source.Length > 0 ? result.Source : "—";
        Dot.Fill = new SolidColorBrush(result.Source == "题库"
            ? Windows.UI.Color.FromArgb(255, 74, 222, 128)
            : Windows.UI.Color.FromArgb(255, 96, 165, 250));

        if (choiceLike && result.Letter.Length > 0)
        {
            BigLetter.Visibility = Visibility.Visible;
            TextScroller.Visibility = Visibility.Collapsed;

            BigLetter.Text = result.Letter;
            // 多选答案（"A,C"）用略小字号，避免超出 232 的宽度
            BigLetter.FontSize = result.Letter.Length > 1 ? 34 : 54;
        }
        else
        {
            BigLetter.Visibility = Visibility.Collapsed;
            TextScroller.Visibility = Visibility.Visible;
            BodyText.Text = string.IsNullOrWhiteSpace(result.Text) ? result.Detail : result.Text;
        }

        DetailLabel.Text = result.LowConfidence
            ? result.Detail + " · 低置信"
            : result.Detail;

        SetOpacity(App.Config.OverlayOpacity);
        App.Config.OverlayLeft = (int)AppWindow.Position.X;
        App.Config.OverlayTop = (int)AppWindow.Position.Y;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _compact = !_compact;

        int h = _compact ? CompactHeight : FullHeight;
        WindowHelper.MoveTo(this, (int)AppWindow.Position.X, (int)AppWindow.Position.Y, FullWidth, h);

        if (_compact)
        {
            BodyText.Visibility = Visibility.Collapsed;
            DetailLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BodyText.Visibility = Visibility.Visible;
            DetailLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppWindow.Hide();
        }
        catch
        {
        }
    }
}
