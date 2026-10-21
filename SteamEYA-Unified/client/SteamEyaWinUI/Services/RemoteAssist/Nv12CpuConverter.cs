namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// CPU 版 BGRA → NV12（BT.709 全范围，与 GPU 路径的 VideoProcessor 参数保持一致）。
///
/// 只在 GDI 兜底采集时用到：1080p 一帧约 2M 像素，纯整数运算，实测 8~15ms，
/// 对应 10~15fps 的兜底帧率——比"没有画面"好得多。
/// </summary>
internal static class Nv12CpuConverter
{
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        var frameSize = width * height;
        var nv12 = new byte[frameSize + frameSize / 2];

        // Y 平面：BT.709 全范围，系数放大 256 倍取整。
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * width * 4;
            var dstRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = srcRow + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var luma = (54 * r + 183 * g + 19 * b + 128) >> 8;
                nv12[dstRow + x] = (byte)(luma > 255 ? 255 : luma);
            }
        }

        // UV 平面：2×2 取平均后按 BT.709 算 Cb/Cr，交错存放。
        var uvOffset = frameSize;
        for (var y = 0; y < height; y += 2)
        {
            var srcRow0 = y * width * 4;
            var srcRow1 = Math.Min(y + 1, height - 1) * width * 4;
            var dstRow = uvOffset + (y >> 1) * width;

            for (var x = 0; x < width; x += 2)
            {
                var x1 = Math.Min(x + 1, width - 1);
                var i0 = srcRow0 + x * 4;
                var i1 = srcRow0 + x1 * 4;
                var i2 = srcRow1 + x * 4;
                var i3 = srcRow1 + x1 * 4;

                var b = (bgra[i0] + bgra[i1] + bgra[i2] + bgra[i3]) >> 2;
                var g = (bgra[i0 + 1] + bgra[i1 + 1] + bgra[i2 + 1] + bgra[i3 + 1]) >> 2;
                var r = (bgra[i0 + 2] + bgra[i1 + 2] + bgra[i2 + 2] + bgra[i3 + 2]) >> 2;

                var luma = (54 * r + 183 * g + 19 * b + 128) >> 8;
                var u = (((b - luma) * 138) >> 8) + 128;
                var v = (((r - luma) * 179) >> 8) + 128;

                nv12[dstRow + x] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                nv12[dstRow + x + 1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }

        return nv12;
    }
}
