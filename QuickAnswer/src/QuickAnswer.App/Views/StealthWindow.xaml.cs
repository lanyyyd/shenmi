using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using Windows.Foundation;

namespace QuickAnswer.App.Views;

/// <summary>
/// 隐蔽模式标注层：一层覆盖整个虚拟桌面的透明窗口，答案直接画在题目上。
///
/// 相比悬浮窗，它「不像一个软件」——没有窗口、没有面板，只有题目旁边多了一个三角。
/// 代价是完全依赖 OCR 的坐标精度，所以三角定位做了左右两侧的兜底。
/// </summary>
public sealed partial class StealthWindow : Window
{
    private static readonly Windows.UI.Color TriColor = Windows.UI.Color.FromArgb(235, 239, 68, 68);
    private static readonly Windows.UI.Color AnswerColor = Windows.UI.Color.FromArgb(240, 16, 185, 129);
    private static readonly Windows.UI.Color JudgeColor = Windows.UI.Color.FromArgb(240, 15, 118, 110);

    private bool _ready;

    public StealthWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer";

        WindowHelper.MakeOverlay(this);
        WindowHelper.HideFromSwitcher(this);

        var vs = ScreenCapturer.VirtualScreen();
        WindowHelper.MoveTo(this, vs.X, vs.Y, vs.Width, vs.Height);

        // 恒定穿透：这一层永远不会响应鼠标，用户点下去是点到下面的题目软件
        WindowHelper.EnableClickThrough(this, () => true);

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

    public void Render(ParsedQuestion question, AnswerResult result)
    {
        Layer.Children.Clear();

        if (result.Letter.Length == 0 && string.IsNullOrWhiteSpace(result.Text))
            return;

        var vs = ScreenCapturer.VirtualScreen();

        switch (question.Type)
        {
            case QuestionType.Choice:
            case QuestionType.Multi:
                DrawChoiceMarks(question, result, vs);
                break;

            case QuestionType.Judge:
                DrawJudgeMark(question, result, vs);
                break;

            case QuestionType.Fill:
                DrawAnswerText(question, result, vs, offsetY: question.StemRect?.Height + 4 ?? 24);
                break;

            default:
                DrawAnswerText(question, result, vs, offsetY: question.StemRect?.Height + 4 ?? 24);
                break;
        }
    }

    // ---------------------------------------------------------------- 选择题

    private void DrawChoiceMarks(ParsedQuestion question, AnswerResult result, ScreenRect vs)
    {
        var targets = result.TargetOptions;
        if (targets.Count == 0)
        {
            // 没拿到选项矩形（题库命中但 OCR 没框出选项）→ 退化为在题干下方写答案
            DrawAnswerText(question, result, vs, offsetY: 0);
            return;
        }

        foreach (var opt in targets)
        {
            if (opt.Rect is not { } rect || rect.IsEmpty)
                continue;

            double screenLeft = rect.X - vs.X;
            double screenTop = rect.Y - vs.Y;
            double screenRight = rect.Right - vs.X;

            const double size = 11;
            string glyph = question.Type == QuestionType.Multi ? "\u25C0" : "\u25C0";

            // 默认画在选项左侧行外 18px；贴左边缘时改到右侧，避免被裁掉
            double x = screenLeft - 18;
            if (x < 4)
                x = screenRight + 6;

            AddGlyph(glyph, x, screenTop + rect.Height / 2.0 - size * 0.62, size, TriColor);
        }

        if (question.Type == QuestionType.Multi)
        {
            double top = targets.Min(o => o.Rect?.Y ?? 0) - vs.Y - 22;
            double left = targets.Min(o => o.Rect?.X ?? 0) - vs.X;
            AddText($"多选 · {targets.Count} 项", Math.Max(4, left - 18), Math.Max(4, top), 10,
                TriColor, bold: true);
        }
    }

    // ---------------------------------------------------------------- 判断题

    private void DrawJudgeMark(ParsedQuestion question, AnswerResult result, ScreenRect vs)
    {
        bool isTrue;
        if (result.TargetOptions.Count > 0)
        {
            var t = result.TargetOptions[0].Text;
            isTrue = t.Contains('对') || t.Contains("正确") || t.Contains('√')
                     || t.Equals("T", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var t = result.Text;
            isTrue = t.Contains('对') || t.Contains("正确") || t.Contains('√')
                     || t.Equals("T", StringComparison.OrdinalIgnoreCase);
        }

        var glyph = isTrue ? "√" : "×";

        if (question.Options.Count >= 2 && question.Options[0].Rect is { } r0)
        {
            // 有 A/B 选项的判断题：标在正确选项左侧
            DrawChoiceMarks(question, result, vs);
            return;
        }

        // 括号式判断题：写在题干行尾右侧
        if (question.StemRect is { } stem)
        {
            double x = stem.Right - vs.X + 8;
            double y = stem.Y - vs.Y + 2;
            AddText(glyph, x, y, 17, JudgeColor, bold: true);
            return;
        }

        AddText(glyph, 40, 40, 18, JudgeColor, bold: true);
    }

    // ---------------------------------------------------------------- 填空 / 简答

    private void DrawAnswerText(ParsedQuestion question, AnswerResult result, ScreenRect vs, double offsetY)
    {
        var text = string.IsNullOrWhiteSpace(result.Text) ? result.Letter : result.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        // 优先挂在题干下方；没有题干矩形就退到选项上方 / 屏幕固定位置
        double x, y;
        if (question.StemRect is { } stem)
        {
            x = stem.X - vs.X;
            y = stem.Bottom - vs.Y + offsetY;
        }
        else if (question.BlockRect is { } block)
        {
            x = block.X - vs.X;
            y = block.Bottom - vs.Y + 4;
        }
        else
        {
            x = 40;
            y = 40;
        }

        // 绿色竖线 + 文字：竖线让答案在密集文字里一眼能找到
        var bar = new Rectangle
        {
            Width = 2.5,
            Height = 18,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Fill = new SolidColorBrush(AnswerColor),
        };
        Canvas.SetLeft(bar, Math.Max(2, x - 8));
        Canvas.SetTop(bar, y + 1);
        Layer.Children.Add(bar);

        AddText(text, Math.Max(2, x), y, 14.5, AnswerColor, bold: true);
    }

    // ---------------------------------------------------------------- 绘制原语

    private void AddGlyph(string glyph, double x, double y, double size, Windows.UI.Color color)
    {
        AddText(glyph, x, y, size, color, bold: true);
    }

    private void AddText(string text, double x, double y, double fontSize, Windows.UI.Color color, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
        };

        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        Layer.Children.Add(tb);
    }
}
