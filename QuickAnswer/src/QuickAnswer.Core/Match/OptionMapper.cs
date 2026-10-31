using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Match;

public sealed class OptionMapResult
{
    public bool Success { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>映射后的本地答案字母（多选用逗号分隔，如 "A,C"）。</summary>
    public string LocalAnswerKey { get; init; } = "";

    /// <summary>题库选项字母 → 本地选项字母。</summary>
    public Dictionary<string, string> Map { get; init; } = new();

    /// <summary>选项里含「以上都对/都不对」这类无区分度短语，建议无论命中与否都问一次 AI 复核。</summary>
    public bool Fragile { get; init; }

    public static OptionMapResult Fail(string reason) => new() { Success = false, Reason = reason };
}

/// <summary>
/// 选项乱序映射 —— 整个项目里最不能出错的一段。
///
/// 题库里存的是「答案文本」，字母只是那一次的排序结果。用户屏幕上选项被打乱了，
/// 直接照搬字母会给出错误答案，比不给答案更糟。所以：
///   1. 用选项文本做匹配，把题库的每个选项对到本地的一个选项上；
///   2. 再把题库的答案字母按这个映射翻译成本地字母；
///   3. 任何一环不确信（数量不符 / 有选项配不上 / 答案配不上）→ 整体放弃，交给 AI。
/// </summary>
public static class OptionMapper
{
    // 阈值必须配合下面的 OptionSimilarity 一起看：
    // 短选项（≤5 字）用编辑距离，"打即机" vs "打印机" 得 0.667，
    // 所以基线阈值不能设成 0.80，否则最常见的「错一个字」会被判失败。
    private const double BaseThreshold = 0.65;
    private const double StrictThreshold = 0.92;

    private static readonly string[] FragilePhrases =
    {
        "以上都", "都不对", "以上均", "全部正确", "全部错误", "以上答案", "全都对", "都不正确",
    };

    public static OptionMapResult Map(
        IReadOnlyList<OptionItem> local,
        IReadOnlyList<OptionItem> db,
        string dbAnswerKey)
    {
        if (local.Count == 0 || db.Count == 0)
            return OptionMapResult.Fail("选项为空");

        if (local.Count != db.Count)
            return OptionMapResult.Fail($"选项数量不一致（本地 {local.Count} / 题库 {db.Count}）");

        var ln = new string[local.Count];
        var dn = new string[db.Count];
        for (int i = 0; i < local.Count; i++) ln[i] = NormalizeOption(local[i].Text, local[i].Key);
        for (int i = 0; i < db.Count; i++) dn[i] = NormalizeOption(db[i].Text, db[i].Key);

        bool fragile = db.Any(o => LooksFragile(o.Text)) || local.Any(o => LooksFragile(o.Text));

        // 选项之间存在高度相似（如「增大 / 增加 / 减少 / 减小」）时，放宽容错会导致串位，
        // 此时把阈值提到 0.95，宁可判失败也不给错答案。
        double threshold = HasConfusableOptions(dn) ? StrictThreshold : BaseThreshold;

        var sim = new double[db.Count, local.Count];
        for (int i = 0; i < db.Count; i++)
            for (int j = 0; j < local.Count; j++)
                sim[i, j] = OptionSimilarity(dn[i], ln[j]);

        // 全局按相似度降序贪心，避免「先到先得」造成顺序依赖
        var pairs = new List<(int D, int L, double S)>(db.Count * local.Count);
        for (int i = 0; i < db.Count; i++)
            for (int j = 0; j < local.Count; j++)
                pairs.Add((i, j, sim[i, j]));
        pairs.Sort((a, b) => b.S.CompareTo(a.S));

        var map = new Dictionary<string, string>();
        var usedLocal = new bool[local.Count];

        foreach (var (d, l, s) in pairs)
        {
            if (s < threshold)
                break;
            if (map.ContainsKey(db[d].Key) || usedLocal[l])
                continue;

            map[db[d].Key] = local[l].Key;
            usedLocal[l] = true;
        }

        if (map.Count != db.Count)
        {
            var missing = string.Join("/", db.Where(o => !map.ContainsKey(o.Key)).Select(o => o.Text));
            return OptionMapResult.Fail($"仅匹配 {map.Count}/{db.Count} 个选项（缺：{missing}）");
        }

        var localKeys = new List<string>();
        foreach (var k in SplitKeys(dbAnswerKey))
        {
            if (!map.TryGetValue(k, out var lk))
                return OptionMapResult.Fail($"题库答案 {k} 不在选项内");

            if (k.Equals(lk, StringComparison.OrdinalIgnoreCase))
                continue;

            localKeys.Add(lk);
        }

        // 答案键与选项完全一致时也不能漏，上面的 continue 让 localKeys 可能为空
        if (localKeys.Count == 0)
            localKeys.AddRange(SplitKeys(dbAnswerKey).Select(k => map[k]));

        return new OptionMapResult
        {
            Success = true,
            LocalAnswerKey = string.Join(",", localKeys.Distinct()),
            Map = map,
            Fragile = fragile,
        };
    }

    /// <summary>判断题跨表述归一：「正确/是/T」都视作「对」。</summary>
    private static string NormalizeOption(string text, string key)
    {
        var n = TextNormalizer.Normalize(text);
        if (n.Length == 0 && key.Length > 0)
            n = TextNormalizer.Normalize(key);

        return n switch
        {
            "正确" or "对" or "是" or "t" or "true" or "√" or "yes" => "对",
            "错误" or "错" or "否" or "f" or "false" or "×" or "x" or "no" => "错",
            _ => n,
        };
    }

    public static string[] SplitKeys(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Array.Empty<string>();

        return key
            .Split(new[] { ',', '，', ';', '；', '/', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => k.Length > 0)
            .ToArray();
    }

    private static bool LooksFragile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return FragilePhrases.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>任意两个选项之间是否高度相似（会把贪心匹配搞串）。</summary>
    private static bool HasConfusableOptions(string[] normalized)
    {
        for (int i = 0; i < normalized.Length; i++)
        {
            for (int j = i + 1; j < normalized.Length; j++)
            {
                if (OptionSimilarity(normalized[i], normalized[j]) >= 0.85)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 选项相似度。按长度分流：
    /// 短选项（≤5 字）用编辑距离 —— 3 个字错 1 个字时，3-gram 会零重合，
    /// n-gram 相似度直接归零，但人看只是「错了一个字」。
    /// 长选项仍用 n-gram，对整段错位更鲁棒。
    /// </summary>
    private static double OptionSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1.0;

        if (Math.Min(a.Length, b.Length) <= 5)
            return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);

        return Similarity.Dice(a, b);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
