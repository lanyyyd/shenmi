using System.Text;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using QuickAnswer.Core.Ocr;
using QuickAnswer.Core.Parse;
using QuickAnswer.Core.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace QuickAnswer.Core.Diagnostics;

/// <summary>
/// 识别留样：把「真正送进 OCR 的那张图」和识别结果一起落盘。
///
/// 为什么非做不可：OCR 不准这件事，在用户那边只能描述成"感觉不准"。
/// 到底是被识别成了错字、还是框选框歪了、还是深色主题没反相、还是试卷本身是低清扫描件，
/// 看一张图就能定，靠猜要来回好几轮。
/// 只保留最近 <see cref="Keep"/> 次，自动覆盖，不会越积越多。
/// </summary>
public static class OcrDiagnostics
{
    /// <summary>保留最近多少次（一张 PNG + 一个 TXT 算一次）。</summary>
    public const int Keep = 20;

    public static string Dir => Path.Combine(AppConfig.Dir, "diag");

    /// <summary>用户可以关掉。关掉后既不写盘也不留内存。</summary>
    public static bool Enabled { get; set; } = true;

    public static async Task SaveAsync(
        OcrPipeline.Outcome outcome,
        ScreenRect region,
        ParsedQuestion? question,
        string note)
    {
        if (!Enabled)
            return;

        try
        {
            Directory.CreateDirectory(Dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var name = $"{stamp}_{outcome.Variant}_q{outcome.Quality.Score:0.00}";

            if (outcome.Image is { } img)
            {
                var png = await EncodePngAsync(img).ConfigureAwait(false);
                if (png is not null)
                    await File.WriteAllBytesAsync(Path.Combine(Dir, name + ".png"), png).ConfigureAwait(false);
            }

            var report = BuildReport(outcome, region, question, note);
            await File.WriteAllTextAsync(Path.Combine(Dir, name + ".txt"), report).ConfigureAwait(false);

            Prune();
        }
        catch
        {
            // 留样本失败绝不能影响出答案
        }
    }

    private static string BuildReport(
        OcrPipeline.Outcome outcome, ScreenRect region, ParsedQuestion? q, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"时间      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"说明      {note}");
        sb.AppendLine($"区域      {region.X},{region.Y} {region.Width}×{region.Height}");
        sb.AppendLine($"引擎      {outcome.Variant} · {outcome.Scale:0.0}x · 第 {outcome.Attempts} 次 · {outcome.ElapsedMs}ms");
        sb.AppendLine($"质量分    {outcome.Quality.Score:0.00}  字数 {outcome.Quality.Chars} · 行 {outcome.Quality.Lines} · 中文 {outcome.Quality.ChineseRatio:P0} · 行高 {outcome.Quality.MedianLineHeight:0}px");
        sb.AppendLine();

        if (q is not null)
        {
            sb.AppendLine($"题型      {q.Type}");
            sb.AppendLine($"题干      {q.Stem.Replace("\n", " / ")}");
            for (int i = 0; i < q.Options.Count; i++)
                sb.AppendLine($"  选项{i}   {q.Options[i].Key}. {q.Options[i].Text}");
            sb.AppendLine();
        }

        sb.AppendLine("---- OCR 原始行 ----");
        foreach (var line in outcome.Lines)
            sb.AppendLine($"[{line.Rect.X},{line.Rect.Y} {line.Rect.Width}×{line.Rect.Height}] {line.Text}");

        return sb.ToString();
    }

    private static async Task<byte[]?> EncodePngAsync(CapturedImage image)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)image.Width,
                (uint)image.Height,
                96, 96,
                image.Bgra);

            await encoder.FlushAsync();

            var size = (int)stream.Size;
            if (size <= 0)
                return null;

            var bytes = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var f in files.Skip(Keep * 2))
                f.Delete();
        }
        catch
        {
        }
    }
}
