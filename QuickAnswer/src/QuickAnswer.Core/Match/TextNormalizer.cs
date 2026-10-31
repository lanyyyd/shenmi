using System.Text;

namespace QuickAnswer.Core.Match;

/// <summary>
/// 文本归一化。目标是让「OCR 错字」和「原始题面」尽量落到同一个形态，
/// 供相似度比较使用。注意：归一化结果只用于比对，不用于展示。
/// </summary>
public static class TextNormalizer
{
    /// <summary>归一化：NFKC → 全角转半角 → 小写 → 去掉空白与标点。</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        // NFKC 会把全角字母/数字/标点统一成半角，是这一步的主要工作
        var s = input.Normalize(NormalizationForm.FormKC);

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
                continue;

            // 零宽字符、BOM 等不可见字符
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
                continue;

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>取归一化后文本的前 N 个字符，作为检索粗筛的哈希键。</summary>
    public static string Head(string normalized, int n)
    {
        if (string.IsNullOrEmpty(normalized))
            return "";
        return normalized.Length <= n ? normalized : normalized[..n];
    }

    /// <summary>中文占比（用于判断这一帧到底是不是中文题目）。</summary>
    public static double ChineseRatio(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int total = 0, cn = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            total++;
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                cn++;
        }
        return total == 0 ? 0 : (double)cn / total;
    }
}
