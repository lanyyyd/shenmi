using QuickAnswer.Core.Parse;

namespace QuickAnswer.Core.Ocr;

/// <summary>
/// 一次 OCR 的"可读性"打分。Windows OCR 不返回任何置信度，只能从结果本身反推：
/// 认出来的字多不多、是不是中文、行数够不够、字够不够大。
///
/// 用途有两个：一是判断要不要换一套预处理再来一遍，二是拦住"读成半句也照样拿去问 AI"。
/// </summary>
public readonly record struct OcrQuality(
    double Score,
    int Chars,
    int Lines,
    double ChineseRatio,
    double MedianLineHeight)
{
    /// <summary>低于此值基本可以认定这一帧废了（图太糊、框歪了、根本不是题目）。</summary>
    public const double Poor = 0.45;

    /// <summary>高于此值就不必再试别的预处理了 —— 再试也长不出更多信息。</summary>
    public const double Good = 0.72;

    public bool IsPoor => Score < Poor;

    public static OcrQuality Evaluate(IReadOnlyList<OcrTextLine> lines)
    {
        if (lines.Count == 0)
            return new OcrQuality(0, 0, 0, 0, 0);

        int chars = 0, cn = 0;
        var heights = new List<int>(lines.Count);

        foreach (var line in lines)
        {
            var t = line.Text;
            chars += t.Length;

            foreach (var ch in t)
            {
                if (char.IsWhiteSpace(ch))
                    continue;
                if (ch >= 0x4E00 && ch <= 0x9FFF)
                    cn++;
            }

            if (line.Rect.Height > 0)
                heights.Add(line.Rect.Height);
        }

        if (chars == 0)
            return new OcrQuality(0, 0, lines.Count, 0, 0);

        double cnRatio = cn / (double)chars;
        double median = Median(heights);

        // 四项加权：字数够、是中文、行数像题、字不能太小。
        // 字高权重最高 —— 字太小意味着 OCR 是在猜，后面的相似度匹配全是建立在猜测上。
        double textScore = Math.Clamp(chars / 50.0, 0, 1);
        double cnScore = Math.Clamp(cnRatio / 0.50, 0, 1);
        double lineScore = Math.Clamp(lines.Count / 3.0, 0, 1);
        double sizeScore = Math.Clamp(median / 14.0, 0, 1);

        double score = 0.30 * textScore + 0.25 * cnScore + 0.15 * lineScore + 0.30 * sizeScore;

        return new OcrQuality(Math.Round(score, 3), chars, lines.Count, Math.Round(cnRatio, 3), median);
    }

    /// <summary>
    /// 按实测行高算下一轮该用多大倍数。目标是把行高做到 30px 上下 ——
    /// 这是 Windows OCR 中文模型最舒服的区间，再大只是变慢。
    /// </summary>
    public static double SuggestScale(double medianLineHeight, double currentScale)
    {
        if (medianLineHeight <= 0)
            return currentScale;

        double need = 30.0 / medianLineHeight;
        return Math.Clamp(need, 1.0, 4.0);
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0)
            return 0;

        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2.0;
    }
}
