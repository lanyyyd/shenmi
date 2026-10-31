using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Models;
using QuickAnswer.Core.Parse;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace QuickAnswer.Core.Ocr;

public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<List<OcrTextLine>> RecognizeAsync(CapturedImage image, double scale);
}

/// <summary>
/// Windows 内置 OCR（Windows.Media.Ocr）。
///
/// 选它而不是 PaddleOCR 的理由：零依赖、不用下模型、单帧 60–120ms，
/// 对「一秒钟要看一次屏幕」这个场景，速度比多 3% 的准确率重要得多。
/// 代价是对小字和复杂排版不友好 —— 所以调用方必须先把图放大 2 倍（见 ScreenCapturer.CaptureScaled）。
/// </summary>
public sealed class WinRtOcrEngine : IOcrEngine
{
    private static readonly string[] PreferredLanguages =
    {
        "zh-Hans-CN", "zh-Hans", "zh-CN", "zh-SG",
    };

    private readonly OcrEngine? _engine;

    public string Name => "Windows OCR";
    public bool IsAvailable => _engine is not null;
    public string? UnavailableReason { get; }

    private WinRtOcrEngine(OcrEngine? engine, string? reason)
    {
        _engine = engine;
        UnavailableReason = reason;
    }

    public static WinRtOcrEngine Create()
    {
        foreach (var tag in PreferredLanguages)
        {
            try
            {
                var lang = new Language(tag);
                if (!OcrEngine.IsLanguageSupported(lang))
                    continue;

                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null)
                    return new WinRtOcrEngine(engine, null);
            }
            catch
            {
                // 语言标签不被识别时继续试下一个
            }
        }

        try
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback is not null)
                return new WinRtOcrEngine(fallback, null);
        }
        catch
        {
        }

        return new WinRtOcrEngine(null,
            "系统缺少中文 OCR 语言包。请到「设置 → 时间和语言 → 语言和区域 → 中文 → 语言选项」安装「光学字符识别」。");
    }

    public async Task<List<OcrTextLine>> RecognizeAsync(CapturedImage image, double scale)
    {
        var result = new List<OcrTextLine>();
        if (_engine is null || image.Width <= 0 || image.Height <= 0)
            return result;

        if (scale <= 0)
            scale = 1;

        SoftwareBitmap? bitmap = null;
        try
        {
            // CryptographicBuffer 是拿 IBuffer 最稳的途径：
            // AsBuffer() 那套扩展在 .NET Core 之后已经不可靠了。
            var buffer = CryptographicBuffer.CreateFromByteArray(image.Bgra);
            bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                buffer, BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);

            var ocr = await _engine.RecognizeAsync(bitmap);
            if (ocr is null)
                return result;

            var origin = image.SourceRect;

            foreach (var line in ocr.Lines)
            {
                if (line.Words.Count == 0)
                    continue;

                var sb = new System.Text.StringBuilder();
                string? prev = null;

                foreach (var word in line.Words)
                {
                    var t = word.Text ?? "";
                    if (t.Length == 0)
                        continue;

                    // 中英文混排：两个 ASCII 词之间补空格，中文之间不补（否则「打 印 机」）
                    if (prev is not null && NeedSpace(prev, t))
                        sb.Append(' ');

                    sb.Append(t);
                    prev = t;
                }

                var text = sb.ToString();
                if (text.Length == 0)
                    continue;

                // BoundingRect 是相对「这张图」的坐标，图被放大过，要除回去再加屏幕偏移
                var r = line.Words[0].BoundingRect;
                double left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;

                foreach (var w in line.Words)
                {
                    var wr = w.BoundingRect;
                    left = Math.Min(left, wr.X);
                    top = Math.Min(top, wr.Y);
                    right = Math.Max(right, wr.X + wr.Width);
                    bottom = Math.Max(bottom, wr.Y + wr.Height);
                }

                var rect = new ScreenRect(
                    origin.X + (int)Math.Round(left / scale),
                    origin.Y + (int)Math.Round(top / scale),
                    Math.Max(1, (int)Math.Round((right - left) / scale)),
                    Math.Max(1, (int)Math.Round((bottom - top) / scale)));

                result.Add(new OcrTextLine(text, rect));
            }
        }
        catch
        {
            // OCR 失败不该让整个循环崩掉，返回已拿到的部分
        }
        finally
        {
            bitmap?.Dispose();
        }

        return result;
    }

    private static bool NeedSpace(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;

        char la = a[^1], fb = b[0];
        bool aAscii = la < 128, bAscii = fb < 128;
        if (!aAscii || !bAscii)
            return false;

        // 英文单词之间需要空格；标点后面不需要
        if (char.IsPunctuation(la) || char.IsPunctuation(fb))
            return false;

        return true;
    }
}
