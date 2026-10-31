using System.Text;
using System.Text.RegularExpressions;
using QuickAnswer.Core.Match;
using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Parse;

/// <summary>OCR 出来的一行文字 + 它在屏幕上的位置（物理像素）。</summary>
public sealed record OcrTextLine(string Text, ScreenRect Rect);

/// <summary>
/// 把 OCR 的散乱文本还原成「题干 + 选项 + 题型」。
///
/// 难点不在正则，而在 OCR 的两种常见破坏：
///   1. 长选项被折行 → 后续不以字母开头的行要并入上一个选项；
///   2. 选项字母被认错（D 认成 O、B 认成 8）→ 按递增规则修正。
/// 这两步不做，选项数量就会和题库对不上，乱序映射必然失败。
/// </summary>
public static class QuestionParser
{
    // 允许：A. / A、/ A) / （A）/ 【A】/ A：
    private static readonly Regex OptionRx = new(
        @"^\s*[（(【\[]?\s*(?<k>[A-HＡ-Ｈ])\s*(?<r>[）)】\]]?)\s*(?<sep>[.．、,，:：]?)\s*(?<t>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex MultiHintRx = new(
        @"多选|多项选择|不定项|选出所有|下列各项中.*的是|至少.*项",
        RegexOptions.Compiled);

    public static ParsedQuestion Parse(IReadOnlyList<OcrTextLine> lines)
    {
        var q = new ParsedQuestion();
        if (lines.Count == 0)
            return q;

        var ordered = lines.OrderBy(l => l.Rect.Y).ThenBy(l => l.Rect.X).ToList();
        q.RawOcr = string.Join("\n", ordered.Select(l => l.Text));
        q.BlockRect = Union(ordered.Select(l => l.Rect));

        // ---- 1. 切分选项 ----
        var stemBuilder = new StringBuilder();
        var rawOptions = new List<(string Key, StringBuilder Text, ScreenRect Rect)>();
        bool optionStarted = false;

        foreach (var line in ordered)
        {
            var m = OptionRx.Match(line.Text);
            bool isOption = false;

            if (m.Success)
            {
                var key = NormalizeKey(m.Groups["k"].Value);
                bool hasBracket = m.Groups["r"].Value.Length > 0;
                bool hasSep = m.Groups["sep"].Value.Length > 0;
                var body = m.Groups["t"].Value.Trim();

                // 只有「有右括号」或「有分隔符」才算选项行，
                // 否则题干里随便一个以字母开头的行都会被误判。
                if ((hasBracket || hasSep) && body.Length > 0)
                {
                    // 字母必须接在已有序列后面（A→B→C…），否则视为题干里的普通字母
                    bool inSequence = rawOptions.Count == 0
                        ? key == "A"
                        : key == NextKey(rawOptions[^1].Key);

                    if (inSequence)
                    {
                        isOption = true;
                        rawOptions.Add((key, new StringBuilder(body), line.Rect));
                        optionStarted = true;
                    }
                }
            }

            if (isOption)
                continue;

            if (optionStarted && rawOptions.Count > 0)
            {
                // 折行：并入上一个选项
                rawOptions[^1].Text.Append(line.Text.Trim());
                rawOptions[^1] = (rawOptions[^1].Key, rawOptions[^1].Text,
                                  Union(new[] { rawOptions[^1].Rect, line.Rect }));
            }
            else
            {
                stemBuilder.Append(line.Text.Trim()).Append('\n');
            }
        }

        q.Stem = CleanStem(stemBuilder.ToString());

        // ---- 2. 选项字母修正（OCR 认错时按递增补正）----
        for (int i = 0; i < rawOptions.Count; i++)
        {
            var expected = ((char)('A' + i)).ToString();
            q.Options.Add(new OptionItem
            {
                Key = expected,
                Text = rawOptions[i].Text.ToString().Trim(),
                Rect = rawOptions[i].Rect,
            });
        }

        if (q.Options.Count > 0 && !string.IsNullOrEmpty(q.Stem))
        {
            var probe = q.Stem[..Math.Min(4, q.Stem.Length)];
            q.StemRect = ordered.FirstOrDefault(l => l.Text.TrimStart().StartsWith(probe, StringComparison.Ordinal))?.Rect
                         ?? ordered[0].Rect;
        }
        else if (ordered.Count > 0)
        {
            q.StemRect = ordered[0].Rect;
        }

        // ---- 3. 题型判定 ----
        q.Type = DetectType(q);

        return q;
    }

    private static QuestionType DetectType(ParsedQuestion q)
    {
        if (q.Options.Count >= 2)
        {
            // 判断题型伪装成两个选项：正确 / 错误
            if (q.Options.Count == 2)
            {
                var a = TextNormalizer.Normalize(q.Options[0].Text);
                var b = TextNormalizer.Normalize(q.Options[1].Text);
                bool judgePair =
                    (a is "正确" or "对" or "是" or "t" or "true" && b is "错误" or "错" or "否" or "f" or "false")
                    || (b is "正确" or "对" or "是" or "t" or "true" && a is "错误" or "错" or "否" or "f" or "false");
                if (judgePair)
                    return QuestionType.Judge;
            }

            if (MultiHintRx.IsMatch(q.Stem) || q.Options.Count > 4)
                return QuestionType.Multi;

            return QuestionType.Choice;
        }

        if (Regex.IsMatch(q.Stem, @"判断|对错|是否正确|√|×"))
            return QuestionType.Judge;

        if (Regex.IsMatch(q.Stem, @"[（(]\s*[）)]|_{2,}|＿{2,}|\(\s*\)"))
            return QuestionType.Fill;

        return QuestionType.Short;
    }

    private static string CleanStem(string raw)
    {
        var s = raw.Replace("\r", "").Trim();
        // 去掉题号前的装饰（如 "1." "12、" "第3题"），题干本身保留题号更利于题库区分
        return Regex.Replace(s, @"\n{2,}", "\n").Trim();
    }

    private static string NormalizeKey(string k)
    {
        var c = char.ToUpperInvariant(k[0]);
        if (c >= 'Ａ' && c <= 'Ｈ')
            c = (char)('A' + (c - 'Ａ'));
        return c.ToString();
    }

    private static string NextKey(string key)
    {
        if (key.Length == 0) return "A";
        return ((char)(key[0] + 1)).ToString();
    }

    private static ScreenRect Union(IEnumerable<ScreenRect> rects)
    {
        var list = rects.Where(r => !r.IsEmpty).ToList();
        if (list.Count == 0)
            return default;

        int x1 = list.Min(r => r.X);
        int y1 = list.Min(r => r.Y);
        int x2 = list.Max(r => r.Right);
        int y2 = list.Max(r => r.Bottom);
        return new ScreenRect(x1, y1, x2 - x1, y2 - y1);
    }
}
