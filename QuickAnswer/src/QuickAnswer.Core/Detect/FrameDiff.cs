namespace QuickAnswer.Core.Detect;

/// <summary>
/// 画面变化检测：把框选区域缩到 64×64 灰度，逐格比较亮度。
///
/// 为什么换掉原来的 9×8 dHash —— 这是「点了下一题却不出答案」的根因：
/// dHash 一共只有 64 bit，题目区域里换掉一整行字，指纹常常只差 2~4 位，
/// 落在「画面没变」的阈值里，于是循环直接 return，连 OCR 都不跑。
/// 用户只能手动点「立即识别」—— 因为手动强制识别会跳过这道闸门。
///
/// 换成一格一格比：格子够多（4096），小面积改动（一行字、一个对勾）
/// 也能触发；同时因为判定的是「有多少格变了」而不是平均值，
/// 整屏亮度轻微漂移不会误报。
/// </summary>
public static class FrameDiff
{
    public const int W = 64;
    public const int H = 64;

    /// <summary>单格灰度差 ≥ 此值算「这格变了」。取 22 是为了压掉屏幕缩放的抗锯齿噪声。</summary>
    private const int CellDelta = 22;

    /// <summary>至少这么多格变了才算整帧变了。4096 格的 0.4% ≈ 一行小字。</summary>
    private const int MinCells = 16;

    /// <summary>
    /// 判定两帧是否有变化。<paramref name="minCells"/> 可以调大用来区分
    /// 「小改动」（光标闪、时钟跳秒）和「整页在动」（翻页动画、视频）。
    /// </summary>
    public static bool HasChanged(byte[]? previous, byte[] current, int minCells = MinCells)
    {
        if (previous is null || previous.Length != current.Length)
            return true;

        int cells = 0;
        for (int i = 0; i < current.Length; i++)
        {
            int d = current[i] - previous[i];
            if (d >= CellDelta || d <= -CellDelta)
            {
                cells++;
                if (cells >= minCells)
                    return true;
            }
        }

        return false;
    }

    /// <summary>整页都在动的阈值：3% 的格子变了，说明不是"一点小动静"。</summary>
    public const int BusyCells = 120;
}

/// <summary>
/// 换题判定的阈值。分成三档而不是一刀切，是因为三种情况的「该做什么」完全不同：
///   - 没变（光标闪、时钟跳秒）→ 先按兵不动，但允许重试两次（OCR 偶尔读空）；
///   - 微变（弹窗、加载动画）→ 需要二次采样确认，不能立刻 OCR；
///   - 剧变（翻页）→ 直接进 OCR，省掉等待。
/// </summary>
public static class ChangeThresholds
{
    /// <summary>确认换题后，静默多久再接受下一次触发（防止翻页动画连续触发）。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>疑似变化后，等待多久重新采样以确认画面已稳定。</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>同一帧最多再重试几次 OCR。给「这次没读出字」留机会，又不至于空转。</summary>
    public const int MaxSameFrameRetries = 2;
}
