using System.Diagnostics;
using QuickAnswer.Core.Api;
using QuickAnswer.Core.Match;
using QuickAnswer.Core.Models;
using QuickAnswer.Core.Storage;

namespace QuickAnswer.Core.Orchestration;

/// <summary>
/// 把 OCR 结果变成一条可展示的答案。
///
/// 顺序固定：题库 → 校验 → 未命中才问 AI → 拿到就上报。
/// 校验这一步不能省：题库和 AI 都可能给出「不在选项里的字母」，
/// 直接展示会让用户照着点错选项，这比不给答案更糟。
/// </summary>
public sealed class AnswerOrchestrator
{
    private readonly ApiClient _api;
    private readonly AppConfig _config;

    public AnswerOrchestrator(ApiClient api, AppConfig config)
    {
        _api = api;
        _config = config;
    }

    public Action<string>? StatusChanged { get; set; }
    public Action<string>? DeltaReceived { get; set; }
    public Action<AnswerResult>? AnswerReady { get; set; }

    /// <summary>返回 null 表示这一帧不值得处理（没识别到题目之类）。</summary>
    public async Task<AnswerResult?> ProcessAsync(ParsedQuestion q, CancellationToken ct)
    {
        if (q.Options.Count == 0 && string.IsNullOrWhiteSpace(q.Stem))
            return null;

        if (q.Stem.Length < 6 && q.Options.Count == 0)
            return null;

        var sw = Stopwatch.StartNew();

        // ---------- 1. 题库 ----------
        if (!string.IsNullOrEmpty(_api.BaseUrl))
        {
            StatusChanged?.Invoke("查题库…");
            try
            {
                var sr = await _api.SearchAsync(q, ct).ConfigureAwait(false);
                if (sr is { Hit: true } && sr.Score >= _config.HitThreshold
                    && !string.IsNullOrWhiteSpace(sr.AnswerLetter))
                {
                    var keys = OptionMapper.SplitKeys(sr.AnswerLetter);
                    bool valid = q.Options.Count == 0
                                 || keys.All(k => q.Options.Any(o => o.Key.Equals(k, StringComparison.OrdinalIgnoreCase)));

                    if (valid)
                    {
                        var hit = BuildFromDb(q, sr, sw);
                        _config.TodayCount++;
                        _config.TodayHit++;
                        AnswerReady?.Invoke(hit);
                        return hit;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 题库挂了不该挡住搜题，继续走 AI
            }
        }

        // ---------- 2. AI ----------
        StatusChanged?.Invoke("问 AI…");

        SolveResponse? solve;
        try
        {
            solve = await _api.SolveAsync(q, _config.ModelId,
                t => DeltaReceived?.Invoke(t), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fail = AnswerResult.Fail(ex.Message);
            AnswerReady?.Invoke(fail);
            return fail;
        }

        if (solve?.Result is null)
        {
            var fail = AnswerResult.Fail("AI 没有返回内容");
            AnswerReady?.Invoke(fail);
            return fail;
        }

        var dto = solve.Result;
        var letter = (dto.AnswerLetter ?? "").Trim().ToUpperInvariant();

        // 「A,C」以外，AI 还可能给「AC」；选项多于 2 个时才敢拆
        if (letter.Length > 1 && letter.All(char.IsLetter) && q.Options.Count > 2)
            letter = string.Join(",", letter.ToCharArray());

        if (q.Type == QuestionType.Judge && (letter is "T" or "F"))
            letter = ResolveJudgeLetter(q, letter);

        // ---------- 3. 校验 ----------
        if (q.Options.Count > 0)
        {
            var keys = OptionMapper.SplitKeys(letter);
            if (keys.Count == 0 || keys.Any(k => q.Options.All(o => !o.Key.Equals(k, StringComparison.OrdinalIgnoreCase))))
            {
                var fail = AnswerResult.Fail("AI 返回的选项不在当前题目里");
                AnswerReady?.Invoke(fail);
                return fail;
            }
        }

        var result = BuildFromAi(q, dto, letter, solve, sw);
        _config.TodayCount++;
        AnswerReady?.Invoke(result);

        // ---------- 4. 立即上报（不等任何后续操作）----------
        _ = ContributeAsync(q, letter, result.Text, solve.Model, dto.Confidence);

        return result;
    }

    private static AnswerResult BuildFromDb(ParsedQuestion q, SearchResponse sr, Stopwatch sw)
    {
        var letter = sr.AnswerLetter ?? "";
        var keys = OptionMapper.SplitKeys(letter);

        var result = new AnswerResult
        {
            Type = q.Type,
            Letter = letter,
            Text = sr.AnswerText ?? "",
            Confidence = sr.Score,
            Source = "题库",
            Detail = $"相似度 {sr.Score:P0} · {sw.ElapsedMilliseconds}ms",
            ElapsedMs = sw.ElapsedMilliseconds,
            LowConfidence = !string.Equals(sr.Confidence, "high", StringComparison.OrdinalIgnoreCase),
            TargetOptions = q.Options
                .Where(o => keys.Any(k => k.Equals(o.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
        };

        if (result.Text.Length == 0)
            result.Text = string.Join("、", result.TargetOptions.Select(o => o.Text));

        return result;
    }

    private static AnswerResult BuildFromAi(
        ParsedQuestion q, ApiAnswerDto dto, string letter, SolveResponse solve, Stopwatch sw)
    {
        var keys = OptionMapper.SplitKeys(letter);

        var result = new AnswerResult
        {
            Type = q.Type,
            Letter = letter,
            Text = dto.AnswerText ?? "",
            Confidence = dto.Confidence,
            Source = "AI",
            Detail = $"{solve.Model ?? "AI"} · {(solve.ElapsedMs > 0 ? solve.ElapsedMs : sw.ElapsedMilliseconds)}ms",
            ElapsedMs = sw.ElapsedMilliseconds,
            LowConfidence = dto.Confidence < 0.6,
            TargetOptions = q.Options
                .Where(o => keys.Any(k => k.Equals(o.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
        };

        if (result.Text.Length == 0 && result.TargetOptions.Count > 0)
            result.Text = string.Join("、", result.TargetOptions.Select(o => o.Text));

        return result;
    }

    private static string ResolveJudgeLetter(ParsedQuestion q, string tf)
    {
        bool wantTrue = tf == "T";

        foreach (var o in q.Options)
        {
            var t = o.Text.Trim();
            bool isTrue = t.Contains('对') || t.Contains("正确") || t.Contains('√')
                          || t.Equals("T", StringComparison.OrdinalIgnoreCase)
                          || t.Equals("True", StringComparison.OrdinalIgnoreCase);

            bool isFalse = t.Contains('错') || t.Contains("错误") || t.Contains('×')
                           || t.Equals("F", StringComparison.OrdinalIgnoreCase)
                           || t.Equals("False", StringComparison.OrdinalIgnoreCase);

            if (wantTrue && isTrue) return o.Key;
            if (!wantTrue && isFalse) return o.Key;
        }

        return q.Options.Count > 0 ? q.Options[0].Key : "";
    }

    private async Task ContributeAsync(
        ParsedQuestion q, string letter, string text, string? model, double confidence)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _api.ContributeAsync(q, letter, text, model, confidence, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 上报失败不影响用户，静默
        }
    }
}
