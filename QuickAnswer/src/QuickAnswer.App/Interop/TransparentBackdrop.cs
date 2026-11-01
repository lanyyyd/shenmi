using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace QuickAnswer.App.Interop;

/// <summary>
/// 完全透明的系统背景（SystemBackdrop）。
///
/// WinUI 3 的窗口在没有 SystemBackdrop 时会自己按主题画一层纯色 ——
/// 浅色主题下就是那块"透明度调到 0 却是白底"的白。想让它真透，
/// 必须挂一个背景，而这个背景得是一支 **全透明的合成笔刷**：
/// Mica / Acrylic 都是糊的，SDK 里没有"透明"这一档，只能自己写。
///
/// 类型上有个坑：WinAppSDK 2.x 的合成层跟 Windows.UI.Composition 是统一的一套，
/// ICompositionSupportsSystemBackdrop.SystemBackdrop 在元数据里是
/// Windows.UI.Composition.CompositionBrush，而 Compositor.CreateColorBrush
/// 返回的是 Microsoft.UI.Composition 的那一个。所以这里先按统一路径拿再转换，
/// 转换失败就退回直接用 Windows.UI.Composition 建一支。
/// </summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        try
        {
            var brush = CreateTransparentBrush();
            if (brush is not null)
                connectedTarget.SystemBackdrop = brush;
        }
        catch
        {
            // 拿不到就维持原样：最坏情况是回到「有底色的不透明窗口」，不影响功能
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

    private static Windows.UI.Composition.CompositionBrush? CreateTransparentBrush()
    {
        try
        {
            var compositor = new Microsoft.UI.Composition.Compositor();
            var brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
            return brush.As<Windows.UI.Composition.CompositionBrush>();
        }
        catch
        {
        }

        try
        {
            var compositor = new Windows.UI.Composition.Compositor();
            return compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255));
        }
        catch
        {
        }

        return null;
    }
}
