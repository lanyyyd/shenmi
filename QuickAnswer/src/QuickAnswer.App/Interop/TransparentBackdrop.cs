using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuickAnswer.App.Interop;

/// <summary>
/// 完全透明的系统背景（SystemBackdrop）。
///
/// WinUI 3 的窗口在没有 SystemBackdrop 时会自己按主题画一层纯色 ——
/// 浅色主题下就是那块"透明度调到 0 却是白底"的白。想让它真透，
/// 必须挂一个背景，而这个背景得是一支**全透明的合成笔刷**。
///
/// 为什么不用 SDK 自带的：MicaBackdrop / DesktopAcrylicBackdrop 都是糊的，
/// 没有"透明"档。自己实现 SystemBackdrop，在目标连上来的时候把
/// ICompositionSupportsSystemBackdrop.SystemBackdrop 换成 alpha=0 的
/// CompositionColorBrush，就不会再画任何底色。
/// </summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    private Compositor? _compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        try
        {
            _compositor ??= new Compositor();
            connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        }
        catch
        {
            // 拿不到 Compositor 就保持原样：最坏情况是回到「有底色的不透明窗口」
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        try
        {
            disconnectedTarget.SystemBackdrop = null;
        }
        catch
        {
        }
    }
}
