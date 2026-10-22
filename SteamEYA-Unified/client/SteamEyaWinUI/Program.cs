using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SteamEyaWinUI;

/// <summary>
/// 自定义入口（csproj 已定义 DISABLE_XAML_GENERATED_MAIN）：
/// 按 WinUI 生成入口的标准流程初始化 COM 包装器与同步上下文后启动 App。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
