using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Capture;

/// <summary>预处理的两条通路，区别只在锐化强度。</summary>
public enum ImageVariant
{
    /// <summary>灰度 + 对比度拉伸 + Lanczos 重采样。默认通路，笔画边缘最接近原始。</summary>
    Soft,

    /// <summary>在 Soft 基础上多一级反锐化掩模。小字、细笔画、低质量截图更吃这一路。</summary>
    Sharp,
}

/// <summary>
/// 送进 OCR 之前的那道工序 —— 纯托管像素运算，不依赖 GDI / System.Drawing。
///
/// 为什么必须做：Windows OCR 本身不差，差的是喂给它的图。
///   1. 屏幕像素是彩色的。彩色底 + 浅色字直接送进去，转灰度后对比度不足，整行读不出来；
///   2. GDI BitBlt 出来的 alpha 通道是 0，而 bitmap 按 Premultiplied 解释时这是非法像素
///      （RGB 大于 A），不同版本处理方式不一致，等于喂给引擎一堆坏数据；
///   3. 原来用 StretchBlt + HALFTONE 放大 —— HALFTONE 是为照片设计的抖动算法，
///      放大文字时细笔画会糊成一团，"日/曰"、"己/已"就是这么错的。
///
/// 所以这里的顺序是：灰度 → 反相（深色底）→ 对比度拉伸 → Lanczos3 放大 → 可选锐化 → BGRA。
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>Lanczos 的支撑半径。3 是图像缩放里公认的精度/开销平衡点。</summary>
    private const double Support = 3.0;

    /// <summary>暗像素占比超过这个值就认定是深色主题，先反相再识别。</summary>
    private const double DarkRatio = 0.55;

    private const byte DarkLevel = 96;

    /// <summary>把原图处理成可以送 OCR 的位图。<paramref name="scale"/> 是放大倍数。</summary>
    public static CapturedImage? Prepare(CapturedImage raw, double scale, ImageVariant variant)
    {
        if (raw.Width <= 0 || raw.Height <= 0)
            return null;

        int count = raw.Width * raw.Height;
        if (raw.Bgra.Length < count * 4)
            return null;

        var gray = ToGray(raw.Bgra, count);

        // 深色底（白字黑底）先反相。Windows OCR 对反相文字的识别率明显更低，
        // 而夜间模式的题库 App 全是这样。
        if (IsDark(gray))
            Invert(gray);

        Stretch(gray, variant == ImageVariant.Sharp ? 0.02 : 0.01);

        int dw = Math.Max(1, (int)Math.Round(raw.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(raw.Height * scale));

        var scaled = Resize(gray, raw.Width, raw.Height, dw, dh);

        if (variant == ImageVariant.Sharp && dw > 32 && dh > 32)
            Sharpen(scaled, dw, dh, 0.7);

        return new CapturedImage
        {
            Width = dw,
            Height = dh,
            Bgra = ToBgra(scaled),
            SourceRect = raw.SourceRect,
            Scale = scale,
        };
    }

    // ---------------------------------------------------------------- 基础变换

    /// <summary>BT.601 亮度。整数近似比浮点快一倍，误差对 OCR 没有意义。</summary>
    public static byte[] ToGray(byte[] bgra, int count)
    {
        var gray = new byte[count];
        for (int i = 0, p = 0; i < count; i++, p += 4)
            gray[i] = (byte)((bgra[p + 2] * 77 + bgra[p + 1] * 150 + bgra[p] * 29) >> 8);
        return gray;
    }

    /// <summary>BGRA，alpha 一律写 255 —— 不能让引擎再碰上 alpha=0 的非法像素。</summary>
    public static byte[] ToBgra(byte[] gray)
    {
        var bgra = new byte[gray.Length * 4];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            byte v = gray[i];
            bgra[p] = v;
            bgra[p + 1] = v;
            bgra[p + 2] = v;
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    public static bool IsDark(byte[] gray)
    {
        if (gray.Length == 0)
            return false;

        int dark = 0;
        // 每 4 个像素采一个，几百万像素也不到 1ms
        int step = gray.Length > 400_000 ? 4 : 1;
        int total = 0;
        for (int i = 0; i < gray.Length; i += step)
        {
            total++;
            if (gray[i] < DarkLevel)
                dark++;
        }
        return total > 0 && dark / (double)total > DarkRatio;
    }

    public static void Invert(byte[] gray)
    {
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (byte)(255 - gray[i]);
    }

    /// <summary>
    /// 按分位数拉伸对比度：把 clip 之外的像素压到 0/255，中间线性展开。
    /// 不做这一步，浅灰字压在浅色底上就只有 20 级灰度差，转灰度后基本是同一片白。
    /// </summary>
    public static void Stretch(byte[] gray, double clip)
    {
        if (gray.Length == 0)
            return;

        Span<int> hist = stackalloc int[256];
        foreach (var v in gray)
            hist[v]++;

        int lowCount = (int)(gray.Length * clip);
        int highCount = (int)(gray.Length * clip);

        int lo = 0, acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc > lowCount)
            {
                lo = i;
                break;
            }
        }

        int hi = 255;
        acc = 0;
        for (int i = 255; i >= 0; i--)
        {
            acc += hist[i];
            if (acc > highCount)
            {
                hi = i;
                break;
            }
        }

        // 对比度已经够、或者整图基本是单色（放大噪声没有意义）就别动
        if (hi - lo < 24)
            return;

        var lut = new byte[256];
        double k = 255.0 / (hi - lo);
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp((i - lo) * k, 0, 255);

        for (int i = 0; i < gray.Length; i++)
            gray[i] = lut[gray[i]];
    }

    // ---------------------------------------------------------------- 重采样

    /// <summary>Lanczos3 可分离重采样。上采样时保留边缘，不会像 HALFTONE 那样把笔画抹开。</summary>
    public static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (dw == sw && dh == sh)
            return (byte[])src.Clone();

        var wx = BuildWeights(sw, dw);
        var wy = BuildWeights(sh, dh);

        var tmp = new byte[dw * sh];
        for (int y = 0; y < sh; y++)
        {
            int srcRow = y * sw;
            int dstRow = y * dw;
            for (int x = 0; x < dw; x++)
            {
                // BuildWeights 已经把所有抽头夹在 [0, srcLen) 内，循环里不必再判边界
                var (start, weights) = wx[x];
                int acc = 0;
                for (int k = 0; k < weights.Length; k++)
                    acc += src[srcRow + start + k] * weights[k];

                tmp[dstRow + x] = ToByte(acc);
            }
        }

        // 纵向按「一条抽头扫一行累加」组织，不按输出像素逐个 gather ——
        // 后者每个像素要跳 6 行取数，8 百万像素时全耗在缓存未命中上。
        var dst = new byte[dw * dh];
        var accRow = new int[dw];
        for (int y = 0; y < dh; y++)
        {
            Array.Clear(accRow);

            var (start, weights) = wy[y];
            for (int k = 0; k < weights.Length; k++)
            {
                int weight = weights[k];
                if (weight == 0)
                    continue;

                int srcRow = (start + k) * dw;
                for (int x = 0; x < dw; x++)
                    accRow[x] += tmp[srcRow + x] * weight;
            }

            int dstRow = y * dw;
            for (int x = 0; x < dw; x++)
                dst[dstRow + x] = ToByte(accRow[x]);
        }

        return dst;
    }

    /// <summary>每个目标像素对应的源区间和定点权重（Q8）。先算好，避免逐像素三角函数。</summary>
    private static (int Start, int[] Weights)[] BuildWeights(int srcLen, int dstLen)
    {
        var result = new (int, int[])[dstLen];
        double scale = (double)srcLen / dstLen;
        double support = Support * Math.Max(1.0, scale);
        double norm = Math.Max(1.0, scale);

        for (int i = 0; i < dstLen; i++)
        {
            double center = (i + 0.5) * scale - 0.5;
            int start = (int)Math.Floor(center - support);
            int end = (int)Math.Ceiling(center + support);

            // 只保留落在图内的抽头，区间外的一律裁掉，比后面逐像素夹紧要快
            if (start < 0) start = 0;
            if (end > srcLen - 1) end = srcLen - 1;
            if (end < start) end = start;

            int n = end - start + 1;
            var w = new int[n];
            int sum = 0;
            for (int k = 0; k < n; k++)
            {
                double x = (start + k - center) / norm;
                w[k] = (int)Math.Round(Lanczos(x) * 256);
                sum += w[k];
            }

            if (sum == 0)
            {
                w[n / 2] = 256;
                sum = 256;
            }

            // 归一化，避免整体偏亮或偏暗
            if (sum != 256)
            {
                for (int k = 0; k < n; k++)
                    w[k] = (int)Math.Round(w[k] * 256.0 / sum);
            }

            result[i] = (start, w);
        }

        return result;
    }

    /// <summary>
    /// 定点累加结果转字节。Lanczos 的负瓣会让累加值变成负数，
    /// 直接 (byte)((acc+128)>>8) 会回绕成 253 这种亮像素 —— 边缘上就是一层"鬼影"。
    /// </summary>
    private static byte ToByte(int acc)
    {
        int v = (acc + 128) >> 8;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-6) return 1.0;
        if (x >= Support) return 0.0;

        double px = Math.PI * x;
        return Support * Math.Sin(px) * Math.Sin(px / Support) / (px * px);
    }

    /// <summary>反锐化掩模：out = v + amount * (v - box3(v))。放大后的软边靠它收回来。</summary>
    public static void Sharpen(byte[] gray, int w, int h, double amount)
    {
        if (w < 3 || h < 3)
            return;

        var box = new byte[gray.Length];

        // 横向 1/3 均值
        var row = new byte[w];
        for (int y = 0; y < h; y++)
        {
            int off = y * w;
            for (int x = 0; x < w; x++)
            {
                int l = x > 0 ? gray[off + x - 1] : gray[off + x];
                int c = gray[off + x];
                int r = x < w - 1 ? gray[off + x + 1] : gray[off + x];
                row[x] = (byte)((l + c + r + 1) / 3);
            }
            Buffer.BlockCopy(row, 0, box, off, w);
        }

        // 纵向 1/3 均值。必须写进另一块内存：就地写会把上一行已经模糊过的结果
        // 再模糊一次，逐行级联，结果是整幅图往下拖出一条长长的竖影。
        var blur = new byte[gray.Length];
        for (int y = 0; y < h; y++)
        {
            int off = y * w;
            int up = (y > 0 ? y - 1 : y) * w;
            int dn = (y < h - 1 ? y + 1 : y) * w;
            for (int x = 0; x < w; x++)
                blur[off + x] = (byte)((box[up + x] + box[off + x] + box[dn + x] + 1) / 3);
        }

        for (int i = 0; i < gray.Length; i++)
        {
            int v = gray[i];
            int d = v - blur[i];
            gray[i] = (byte)Math.Clamp(v + (int)Math.Round(d * amount), 0, 255);
        }
    }
}
