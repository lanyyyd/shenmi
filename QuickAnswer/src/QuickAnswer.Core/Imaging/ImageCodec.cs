using QuickAnswer.Core.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace QuickAnswer.Core.Imaging;

/// <summary>
/// 把 BGRA 位图编成 PNG。两个地方要用：
///   · 诊断留样（要能直接发给别人看）；
///   · 视觉模式（截图要 base64 塞进多模态模型的 image_url）。
/// 走 WinRT 的 BitmapEncoder，不用引 System.Drawing（那个在高分屏上还有自己的坑）。
/// </summary>
public static class ImageCodec
{
    public static async Task<byte[]?> EncodePngAsync(CapturedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4)
            return null;

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

    /// <summary>data URL。OpenAI 兼容接口的 image_url.url 既接受公网 URL，也接受这种内联形式。</summary>
    public static string ToDataUrl(byte[] png) =>
        "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>
    /// 按最长边等比缩小（只缩不放）。
    /// 视觉模型吃原图很贵，大部分模型自己也会把图缩到 1024 左右，
    /// 客户端先缩一遍能省掉一半以上的 token 和上传时间。
    /// 保留彩色：题目里用颜色标记的选项、彩色表格线，灰度掉之后就没了。
    /// </summary>
    public static CapturedImage Fit(CapturedImage src, int maxSide)
    {
        if (maxSide <= 0 || (src.Width <= maxSide && src.Height <= maxSide))
            return src;

        double scale = maxSide / (double)Math.Max(src.Width, src.Height);
        int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(src.Height * scale));

        return new CapturedImage
        {
            Width = dw,
            Height = dh,
            Bgra = Downscale(src.Bgra, src.Width, src.Height, dw, dh),
            SourceRect = src.SourceRect,
            Scale = src.Scale * scale,
        };
    }

    /// <summary>盒式均值缩小。缩小时每个目标像素覆盖一片源像素，取平均不会有摩尔纹。</summary>
    private static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];

        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh;
            int y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);

            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw;
                int x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);

                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * sw;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int p = (row + xx) * 4;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                        n++;
                    }
                }

                int q = (y * dw + x) * 4;
                dst[q] = (byte)(b / n);
                dst[q + 1] = (byte)(g / n);
                dst[q + 2] = (byte)(r / n);
                dst[q + 3] = 255;
            }
        }

        return dst;
    }
}
