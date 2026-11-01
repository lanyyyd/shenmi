using System.Diagnostics;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using QuickAnswer.Core.Parse;

namespace QuickAnswer.Core.Ocr;

/// <summary>
/// 截图 → 预处理 → OCR → 打分 → 不满意就换参数再来一遍。
///
/// 设计原则：默认只跑一次（约 120ms），够好就返回；分数不达标才升级。
/// 之所以允许重试，是因为"哪套预处理更好"没法事先知道 ——
/// 浅色网页要灰度拉伸，深色 App 要反相，细笔画小字要锐化，
/// 与其猜，不如让引擎自己用分数投票。
/// </summary>
public sealed class OcrPipeline
{
    /// <summary>OCR 引擎对超长边不友好（解码内存、耗时都会跳）。超过就降倍数。</summary>
    private const int MaxSide = 7000;

    private readonly IOcrEngine _engine;

    public OcrPipeline(IOcrEngine engine) => _engine = engine;

    public string EngineName => _engine.Name;
    public bool IsAvailable => _engine.IsAvailable;

    public sealed class Outcome
    {
        public List<OcrTextLine> Lines { get; init; } = new();
        public OcrQuality Quality { get; init; }
        public double Scale { get; init; }
        public string Variant { get; init; } = "";
        public int Attempts { get; init; }

        /// <summary>整套流程（含所有重试）的耗时，最后统一回填。</summary>
        public long ElapsedMs { get; set; }

        /// <summary>真正送给引擎的那张图。诊断留样用它，复现问题时不会失真。</summary>
        public CapturedImage? Image { get; init; }
    }

    public async Task<Outcome?> RunAsync(ScreenRect region, double baseScale, CancellationToken ct)
    {
        if (!_engine.IsAvailable || region.IsEmpty)
            return null;

        var raw = ScreenCapturer.Capture(region);
        if (raw is null)
            return null;

        var sw = Stopwatch.StartNew();
        double scale = ClampScale(region, Math.Clamp(baseScale, 1.0, 4.0));

        var best = await PassAsync(raw, scale, ImageVariant.Sharp, 1, ct).ConfigureAwait(false);
        if (best is null)
            return null;

        if (!ct.IsCancellationRequested && best.Quality.Score < OcrQuality.Good)
        {
            // 换一条通路：锐化对细笔画有效，但遇到浅底浅字、或者本身带噪点的截图反而更差
            var alt = await PassAsync(raw, scale, ImageVariant.Soft, 2, ct).ConfigureAwait(false);
            if (alt is not null && alt.Quality.Score > best.Quality.Score + 0.02)
                best = alt;
        }

        if (!ct.IsCancellationRequested && best.Quality.Score < OcrQuality.Good)
        {
            // 还是不行，多半是字太小。按实测行高把倍数提上去再试一次。
            double need = ClampScale(region, OcrQuality.SuggestScale(best.Quality.MedianLineHeight, scale));
            if (need > scale * 1.25)
            {
                var up = await PassAsync(raw, need, ImageVariant.Sharp, 3, ct).ConfigureAwait(false);
                if (up is not null && up.Quality.Score > best.Quality.Score + 0.02)
                    best = up;
            }
        }

        best.ElapsedMs = sw.ElapsedMilliseconds;
        return best;
    }

    private async Task<Outcome?> PassAsync(
        CapturedImage raw, double scale, ImageVariant variant, int attempt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        var img = ImagePreprocessor.Prepare(raw, scale, variant);
        if (img is null)
            return null;

        var lines = await _engine.RecognizeAsync(img).ConfigureAwait(false);

        return new Outcome
        {
            Lines = lines,
            Quality = OcrQuality.Evaluate(lines),
            Scale = scale,
            Variant = variant.ToString(),
            Attempts = attempt,
            Image = img,
        };
    }

    private static double ClampScale(ScreenRect region, double scale)
    {
        double maxW = MaxSide / (double)Math.Max(1, region.Width);
        double maxH = MaxSide / (double)Math.Max(1, region.Height);
        return Math.Clamp(Math.Min(scale, Math.Min(maxW, maxH)), 1.0, 4.0);
    }
}
