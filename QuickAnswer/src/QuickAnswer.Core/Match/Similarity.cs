namespace QuickAnswer.Core.Match;

/// <summary>
/// 字符串相似度。核心用字符 3-gram 的 Dice 系数：
///   Dice(A,B) = 2 * |A ∩ B| / (|A| + |B|)
/// 选它而不是编辑距离，是因为 OCR 错字往往是「错一个笔画」而非「漏一个字」，
/// 3-gram 对局部替换更宽容（一个错字只破坏 3 个 gram，而不是让整段错位）。
/// </summary>
public static class Similarity
{
    private const int N = 3;

    public static double Dice(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;
        if (a == b)
            return 1.0;

        // 短文本（如选项只有 2 个字）退化为 2-gram，否则 3-gram 取不到任何特征
        int n = Math.Min(a.Length, b.Length) >= N ? N : 2;
        if (a.Length < 2 || b.Length < 2)
            return a == b ? 1.0 : 0.0;

        var ha = Grams(a, n);
        var hb = Grams(b, n);
        if (ha.Count == 0 || hb.Count == 0)
            return 0;

        int inter = 0;
        foreach (var (g, cnt) in ha)
        {
            if (hb.TryGetValue(g, out var other))
                inter += Math.Min(cnt, other);
        }

        int total = 0;
        foreach (var c in ha.Values) total += c;
        foreach (var c in hb.Values) total += c;

        return total == 0 ? 0 : 2.0 * inter / total;
    }

    private static Dictionary<string, int> Grams(string s, int n)
    {
        var d = new Dictionary<string, int>(s.Length);
        for (int i = 0; i + n <= s.Length; i++)
        {
            var g = s.Substring(i, n);
            d[g] = d.TryGetValue(g, out var c) ? c + 1 : 1;
        }
        return d;
    }
}
