using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Models;
using Windows.Graphics;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Views;

/// <summary>
/// 隐蔽模式的答案浮层。
///
/// 和上一版的根本差别在「窗口位置由谁决定」：
///
///   旧版把标注画在题干/选项旁边，每帧都按 OCR 出来的包围盒 MoveAndResize 一次。
///   后果就是用户看到的「乱跑」—— 换题、页面滚动、某个选项被识别成两行，
///   包围盒都会变，窗口于是跟着跳；再叠加一层 DIP/物理像素换算，
///   高缩放下还会偏出去一截。
///
///   新版是一张固定尺寸的卡片：**默认停在屏幕中央**，位置只由用户拖动决定，
///   并记进配置（StealthLeft / StealthTop），下次开还是那儿。
///   代价是不能再指着某个选项画三角，但卡片里本来就写着答案字母，信息没丢。
///
///   另外两个约束：
///   · 点击不激活（WM_MOUSEACTIVATE → MA_NOACTIVATE），拖动时不抢焦点，
///     用户在题目软件里打字不会断；
///   · 背景不透明度来自设置项，0 = 完全不画背景色（只剩文字）。
/// </summary>
public sealed partial class StealthWindow : Window
{
    private const int MinWidth = 232;
    private const int TextCardWidth = 300;
    private const int LetterCardWidth = 148;
    private const int MinHeight = 76;
    private const int MaxHeight = 220;

    /// <summary>卡片内边距（左右各 16）+ 抬头行 16 + 行距 7，见 XAML。</summary>
    private const int ChromeHeight = 22 + 16 + 7;
    private const int SidePadding = 32;

    private static readonly Windows.UI.Color BgColor = Windows.UI.Color.FromArgb(255, 15, 23, 42);
    private static readonly Windows.UI.Color LetterColor = Windows.UI.Color.FromArgb(255, 74, 222, 128);
    private static readonly Windows.UI.Color BodyColor = Windows.UI.Color.FromArgb(255, 241, 245, 249);

    // 没有背景色时只能靠文字自己立住：换成深一点的绿，压在白底试卷上也看得清
    private static readonly Windows.UI.Color LetterColorNoBg = Windows.UI.Color.FromArgb(255, 5, 150, 105);
    private static readonly Windows.UI.Color BodyColorNoBg = Windows.UI.Color.FromArgb(255, 6, 78, 59);

    private bool _ready;
    private bool _placed;

    private bool _dragging;
    private int _dragCursorX, _dragCursorY;
    private int _dragOriginX, _dragOriginY;

    private int _logicalW = MinWidth;
    private int _logicalH = MinHeight;
    private int _left;
    private int _top;

    private double _opacity = 0.30;

    public StealthWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer 答案浮层";

        _opacity = Math.Clamp(App.Config.OverlayOpacity, 0, 1);

        WindowHelper.MakeOverlay(this);
        WindowHelper.EnableTransparency(this);
        WindowHelper.SuppressEraseBackground(this);

        // 不抢焦点：拖动结束（甚至拖动过程中）焦点仍在题目软件上
        WindowHelper.NoActivate(this);

        // 默认可以拖；设置里开了「点击穿透」就整层交还给下层窗口
        WindowHelper.EnableClickThrough(this, () => App.Config.StealthClickThrough);

        // 事件挂在最外层 Grid 上：透明度 0 时卡片自己 Background 是 null，
        // 那一层不参与命中测试，挂在它上面就拖不动了
        Hit.PointerPressed += OnPointerPressed;
        Hit.PointerMoved += OnPointerMoved;
        Hit.PointerReleased += OnPointerReleased;
        Hit.PointerCaptureLost += (_, _) => _dragging = false;
        Activated += OnFirstActivated;
        AppWindow.Hide();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_ready)
            return;
        _ready = true;

        if (App.Config.ProtectFromCapture)
            WindowHelper.ProtectFromCapture(this);
    }

    // ---------------------------------------------------------------- 呈现

    public void Render(AnswerResult result)
    {
        if (!ApplyContent(result))
        {
            Clear();
            return;
        }

        ApplyOpacity();
        PlaceWindow();

        WindowHelper.ShowNoActivate(this);

        if (App.Config.ProtectFromCapture)
            WindowHelper.ProtectFromCapture(this);
    }

    public void Clear()
    {
        _dragging = false;
        AppWindow.Hide();
    }

    /// <summary>设置页「复位」用：丢掉记忆位置，回到屏幕中央。</summary>
    public void ResetPosition()
    {
        App.Config.StealthLeft = -1;
        App.Config.StealthTop = -1;
        _placed = false;

        if (_logicalW > 0 && AppWindow.Size.Width > 0)
        {
            PlaceWindow();
            if (AppWindow.IsVisible)
                WindowHelper.ShowNoActivate(this);
        }
    }

    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0, 1);
        ApplyOpacity();
    }

    private bool ApplyContent(AnswerResult result)
    {
        var letter = (result.Letter ?? "").Trim();
        var text = (result.Text ?? "").Trim();

        if (letter.Length == 0 && text.Length == 0 && string.IsNullOrWhiteSpace(result.Detail))
            return false;

        bool hasLetter = letter.Length > 0;

        LetterText.Visibility = hasLetter ? Visibility.Visible : Visibility.Collapsed;
        LetterText.Text = letter;
        LetterText.FontSize = letter.Length > 1 ? 26 : 40;

        BodyText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyText.Text = text;

        SourceLabel.Text = string.IsNullOrWhiteSpace(result.Detail)
            ? (result.Source.Length > 0 ? result.Source : "QuickAnswer")
            : (result.Source.Length > 0 ? $"{result.Source} · {result.Detail}" : result.Detail);

        Dot.Fill = new SolidColorBrush(result.Source == "题库"
            ? Windows.UI.Color.FromArgb(255, 74, 222, 128)
            : Windows.UI.Color.FromArgb(255, 96, 165, 250));

        MeasureCard(hasLetter, letter, text);
        return true;
    }

    /// <summary>
    /// 按内容估算卡片尺寸。窗口大小必须自己算：WinUI 3 的 Window 没有 SizeToContent，
    /// 而窗口比内容大一圈的话，透明区域虽然看不见，却照样吃鼠标点击。
    /// </summary>
    private void MeasureCard(bool hasLetter, string letter, string text)
    {
        int letterWidth = hasLetter ? (letter.Length > 1 ? 66 : 40) : 0;

        // 只有字母 → 小卡片；有正文 → 固定 300 宽，太长就换行 + 省略号，
        // 不让窗口横着铺满屏幕
        int width = text.Length == 0
            ? (hasLetter ? LetterCardWidth : MinWidth)
            : TextCardWidth;

        int textWidth = Math.Max(60, width - SidePadding - (hasLetter ? letterWidth + 12 : 0));
        int perLine = Math.Max(4, textWidth / 15);
        int lines = text.Length == 0
            ? 0
            : Math.Clamp((text.Length + perLine - 1) / perLine, 1, 6);

        int bodyHeight = text.Length == 0
            ? (hasLetter ? 46 : 18)
            : Math.Max(lines * 21, hasLetter ? 46 : 21);

        _logicalW = width;
        _logicalH = Math.Clamp(ChromeHeight + bodyHeight, MinHeight, MaxHeight);
    }

    private void ApplyOpacity()
    {
        // 0.95 封顶：全不透明既不好看，也没人需要
        byte alpha = (byte)Math.Round(255 * Math.Clamp(_opacity, 0, 0.95));

        if (alpha == 0)
        {
            // 透明度拉到 0 = 没有背景色，只剩文字
            Card.Background = null;
            Card.BorderBrush = null;
        }
        else
        {
            Card.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(alpha, BgColor.R, BgColor.G, BgColor.B));
            Card.BorderBrush = new SolidColorBrush(
                Windows.UI.Color.FromArgb((byte)Math.Min(255, alpha + 50), 255, 255, 255));
        }

        bool noBg = alpha == 0;

        LetterText.Foreground = new SolidColorBrush(noBg ? LetterColorNoBg : LetterColor);
        BodyText.Foreground = new SolidColorBrush(noBg ? BodyColorNoBg : BodyColor);

        var minor = new SolidColorBrush(noBg
            ? Windows.UI.Color.FromArgb(255, 71, 85, 105)
            : Windows.UI.Color.FromArgb(255, 148, 163, 184));
        SourceLabel.Foreground = minor;
        HintLabel.Foreground = minor;
    }

    // ---------------------------------------------------------------- 定位

    private void PlaceWindow()
    {
        int physW = WindowHelper.ToPhysical(this, _logicalW);
        int physH = WindowHelper.ToPhysical(this, _logicalH);

        if (!_placed)
        {
            var area = TargetWorkArea();

            if (App.Config.StealthLeft >= 0 && App.Config.StealthTop >= 0)
            {
                // 上次拖到的位置
                _left = App.Config.StealthLeft;
                _top = App.Config.StealthTop;
            }
            else
            {
                // 默认：屏幕正中间
                _left = area.X + (area.Width - physW) / 2;
                _top = area.Y + (area.Height - physH) / 2;
            }

            _left = ClampX(area, _left, physW);
            _top = ClampY(area, _top, physH);
            _placed = true;
        }

        // 后续换题只改内容，不再动位置 —— 这就是「不乱跑」的关键
        WindowHelper.MoveToPhysical(this, _left, _top, physW, physH);
    }

    /// <summary>浮层该落在哪块显示器：框选区域所在那块，没框选就用主屏。</summary>
    private RectInt32 TargetWorkArea()
    {
        if (App.Config.Region is { } r)
            return WindowHelper.WorkAreaAt(this, r.X + r.Width / 2, r.Y + r.Height / 2);

        var work = WindowHelper.WorkArea(this);
        return WindowHelper.WorkAreaAt(this, work.X + work.Width / 2, work.Y + work.Height / 2);
    }

    private static int ClampX(RectInt32 area, int left, int width)
    {
        // 至少留 72px 在屏幕里，不然拖出去就找不回来了
        int min = area.X - width + 72;
        int max = area.X + area.Width - 72;
        return min > max ? area.X : Math.Clamp(left, min, max);
    }

    private static int ClampY(RectInt32 area, int top, int height)
    {
        int min = area.Y;
        int max = area.Y + area.Height - 40;
        return min > max ? area.Y : Math.Clamp(top, min, max);
    }

    // ---------------------------------------------------------------- 拖动

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (App.Config.StealthClickThrough)
            return;

        if (!GetCursorPos(out var cursor))
            return;

        // 用光标绝对坐标算位移：窗口自己在动，用相对坐标会越拖越飘
        _dragging = true;
        _dragCursorX = cursor.X;
        _dragCursorY = cursor.Y;
        _dragOriginX = AppWindow.Position.X;
        _dragOriginY = AppWindow.Position.Y;

        Hit.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        if (!GetCursorPos(out var cursor))
            return;

        int left = _dragOriginX + (cursor.X - _dragCursorX);
        int top = _dragOriginY + (cursor.Y - _dragCursorY);

        var area = WindowHelper.WorkAreaAt(this, cursor.X, cursor.Y);
        var size = AppWindow.Size;

        _left = ClampX(area, left, size.Width);
        _top = ClampY(area, top, size.Height);

        WindowHelper.MoveToPhysical(this, _left, _top, size.Width, size.Height);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Hit.ReleasePointerCapture(e.Pointer);

        App.Config.StealthLeft = _left;
        App.Config.StealthTop = _top;
        App.Config.Save();

        e.Handled = true;
    }
}
