using System.Numerics;

namespace QuickAnswer.Core.Detect;

/// <summary>
/// 差值哈希（dHash）。把区域缩成 9×8 灰度图，比较每行相邻像素的亮度，
/// 得到 64 bit 指纹。用途只有一个：**在 OCR 之前廉价地判断画面有没有变**。
///
/// 为什么不用更精确的感知哈希：这里要的不是「像不像」，而是「变没变」，
/// dHash 对亮度整体漂移（如屏幕自动调光）不敏感，对文字刷新极敏感，正好合适。
/// </summary>
public static class DHash
{
    public const int W = 9;
    public const int H = 8;

    /// <summary>输入必须是 W×H 的灰度数组（72 字节）。</summary>
    public static ulong Compute(ReadOnlySpan<byte> gray)
    {
        if (gray.Length < W * H)
            return 0;

        ulong hash = 0;
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W - 1; x++)
            {
                hash <<= 1;
                if (gray[row + x] < gray[row + x + 1])
                    hash |= 1UL;
            }
        }
        return hash;
    }

    /// <summary>汉明距离：0 = 完全一样，64 = 完全不同。</summary>
    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>
/// 换题判定的阈值。分成三档而不是一刀切，是因为三种情况的「该做什么」完全不同：
///   - 没变（光标闪、时钟跳秒）→ 什么都不做，这是绝大多数 tick 的结果；
///   - 微变（弹窗、加载动画）→ 需要二次采样确认，不能立刻 OCR；
///   - 剧变（翻页）→ 直接进 OCR，省掉 180ms 等待。
/// </summary>
public static class ChangeThresholds
{
    /// <summary>汉明距离 ≤ 此值视为画面未变。</summary>
    public const int Unchanged = 6;

    /// <summary>汉明距离 &gt; 此值视为整页切换，跳过二次确认。</summary>
    public const int PageSwitch = 18;

    /// <summary>确认换题后，静默多久再接受下一次触发（防止翻页动画连续触发）。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>疑似变化后，等待多久重新采样以确认画面已稳定。</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(180);
}
