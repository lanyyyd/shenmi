using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Models;

namespace QuickAnswer.App.Views;

/// <summary>
/// 隐蔽模式的标注层。
///
/// 三条设计都是被坑出来的：
///   1. **只覆盖题目区域，不铺满全屏。** 全屏窗口一旦透明失败就是整块白屏，
///      又因为置顶 + 不在 Alt+Tab 里，用户会被锁死到只能重启电脑。
///      只盖几百像素的话，最坏情况是一小块白，看得见也关得掉。
///   2. **窗口真透明**靠 DwmExtendFrameIntoClientArea + 拦 WM_ERASEBKGND。
///      WinUI 3 原生没有 TransparentTintBackdrop（那是 WinUIEx 的），
///      不设 SystemBackdrop 时窗口会画主题纯色 —— 这就是白屏的根因。
///   3. **不加 WS_EX_TOOLWINDOW。** 宁可牺牲一点隐蔽性，也要让用户能 Alt+Tab 切走。
/// </summary>
public sealed partial class StealthWindow : Window
{
    private static readonly Windows.UI.Color TriColor = Windows.UI.Color.FromArgb(235, 239, 68, 68);
    private static readonly Windows.UI.Color AnswerColor = Windows.UI.Color.FromArgb(240, 16, 185, 129);
    private static readonly Windows.UI.Color JudgeColor = Windows.UI.Color.FromArgb(240, 15, 118, 110);

    private readonly record struct Mark(
        string Text, double X, double Y, double Size, double MaxWidth,
        Windows.UI.Color Color, bool Bold);

    private bool _ready;

    public StealthWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer 标注";

        WindowHelper.MakeOverlay(this);
        WindowHelper.EnableTransparency(this);
        WindowHelper.SuppressEraseBackground(this);

        // 恒定穿透：这一层永远不响应鼠标
        WindowHelper.EnableClickThrough(this, () => true);

        // 故意不调 HideFromSwitcher —— 出问题时用户至少能从 Alt+Tab 找到它
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

    public void Render(ParsedQuestion question, AnswerResult result)
    {
        Layer.Children.Clear();

        var marks = BuildMarks(question, result);
        if (marks.Count == 0)
        {
            AppWindow.Hide();
            return;
        }

        // 标注包围盒（屏幕物理像素）
        var bounds = ComputeBounds(marks);

        // 窗口只盖住这块，不铺满屏幕
        WindowHelper.MoveToPhysical(this, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        // Canvas 用 DIP，标注是物理像素，换算一下
        double scale = WindowHelper.ScaleFactor(this);
        if (scale <= 0)
            scale = 1.0;

        foreach (var m in marks)
        {
            AddText(m.Text,
                (m.X - bounds.X) / scale,
                (m.Y - bounds.Y) / scale,
                m.Size,
                m.MaxWidth / scale,
                m.Color,
                m.Bold);
        }

        // 尺寸变了要重新声明一次玻璃区，否则新增的那块可能是白的
        WindowHelper.EnableTransparency(this);
        WindowHelper.ShowNoActivate(this);

        if (App.Config.ProtectFromCapture)
            WindowHelper.ProtectFromCapture(this);
    }

    public void Clear()
    {
        Layer.Children.Clear();
        AppWindow.Hide();
    }

    // ---------------------------------------------------------------- 标注计算

    private List<Mark> BuildMarks(ParsedQuestion question, AnswerResult result)
    {
        var marks = new List<Mark>();

        if (result.Letter.Length == 0 && string.IsNullOrWhiteSpace(result.Text))
            return marks;

        switch (question.Type)
        {
            case QuestionType.Choice:
            case QuestionType.Multi:
                BuildChoiceMarks(question, result, marks);
                break;

            case QuestionType.Judge:
                BuildJudgeMarks(question, result, marks);
                break;

            default:
                BuildAnswerTextMarks(question, result, marks);
                break;
        }

        return marks;
    }

    private static void BuildChoiceMarks(ParsedQuestion question, AnswerResult result, List<Mark> marks)
    {
        var targets = result.TargetOptions
            .Where(o => o.Rect is { } r && !r.IsEmpty)
            .ToList();

        if (targets.Count == 0)
        {
            BuildAnswerTextMarks(question, result, marks);
            return;
        }

        foreach (var opt in targets)
        {
            var rect = opt.Rect!.Value;

            // 默认在选项左侧行外；贴左边缘时改到右侧，避免被裁掉
            double x = rect.X - 20;
            if (x < 4)
                x = rect.Right + 6;

            marks.Add(new Mark("\u25C0", x, rect.CenterY - 10, 13, 20, TriColor, true));
        }

        if (question.Type == QuestionType.Multi)
        {
            var first = targets.Min(o => o.Rect!.Value.Y);
            var left = targets.Min(o => o.Rect!.Value.X);
            marks.Add(new Mark($"多选 · {targets.Count} 项", Math.Max(4, left - 20), first - 26,
                11, 200, TriColor, true));
        }
    }

    private static void BuildJudgeMarks(ParsedQuestion question, AnswerResult result, List<Mark> marks)
    {
        // 有 A/B 选项的判断题：直接标在正确选项旁
        if (question.Options.Count >= 2 && question.Options.Any(o => o.Rect is { IsEmpty: false }))
        {
            BuildChoiceMarks(question, result, marks);
            return;
        }

        bool isTrue = result.TargetOptions.Count > 0
            ? IsTrueText(result.TargetOptions[0].Text)
            : IsTrueText(result.Text);

        var glyph = isTrue ? "√" : "×";

        if (question.StemRect is { } stem)
        {
            marks.Add(new Mark(glyph, stem.Right + 6, stem.Y - 2, 16, 40, JudgeColor, true));
            return;
        }

        marks.Add(new Mark(glyph, 40, 40, 18, 40, JudgeColor, true));
    }

    private static void BuildAnswerTextMarks(ParsedQuestion question, AnswerResult result, List<Mark> marks)
    {
        var text = string.IsNullOrWhiteSpace(result.Text) ? result.Letter : result.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        double x, y;
        if (question.StemRect is { } stem)
        {
            x = stem.X;
            y = stem.Bottom + 4;
        }
        else if (question.BlockRect is { } block)
        {
            x = block.X;
            y = block.Bottom + 4;
        }
        else
        {
            x = 60;
            y = 60;
        }

        // 简答可能上百字，限宽，避免拉出一个超宽窗口
        marks.Add(new Mark(text, x, y, 14.5, 560, AnswerColor, true));
    }

    private static bool IsTrueText(string? t)
    {
        if (string.IsNullOrWhiteSpace(t))
            return false;

        return t.Contains('对') || t.Contains("正确") || t.Contains('√')
               || t.Equals("T", StringComparison.OrdinalIgnoreCase)
               || t.Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 几何

    private static ScreenRect ComputeBounds(List<Mark> marks)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue;
        double x2 = double.MinValue, y2 = double.MinValue;

        foreach (var m in marks)
        {
            // 粗估尺寸即可：宁可窗口略大，也不要裁掉字
            double maxW = Math.Max(m.MaxWidth, 1);
            double approxW = Math.Min(maxW, Math.Max(m.Text.Length, 1) * m.Size * 0.62);
            int lines = Math.Max(1, (int)Math.Ceiling(m.Text.Length * m.Size * 0.62 / maxW));
            double approxH = m.Size * 1.6 * lines;

            x1 = Math.Min(x1, m.X);
            y1 = Math.Min(y1, m.Y);
            x2 = Math.Max(x2, m.X + approxW);
            y2 = Math.Max(y2, m.Y + approxH);
        }

        const int pad = 14;

        int left = (int)Math.Floor(x1) - pad;
        int top = (int)Math.Floor(y1) - pad;
        int width = (int)Math.Ceiling(x2 - x1) + pad * 2;
        int height = (int)Math.Ceiling(y2 - y1) + pad * 2;

        // 太小的话 DWM 玻璃可能不生效，给个下限
        width = Math.Max(width, 120);
        height = Math.Max(height, 60);

        return new ScreenRect(left, top, width, height);
    }

    // ---------------------------------------------------------------- 绘制

    private void AddText(string text, double x, double y, double fontSize, double maxWidth,
                         Windows.UI.Color color, bool bold)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(40, maxWidth),
        };

        Canvas.SetLeft(tb, Math.Max(0, x));
        Canvas.SetTop(tb, Math.Max(0, y));
        Layer.Children.Add(tb);
    }
}
