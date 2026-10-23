using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SteamEyaWinUI.Services;

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

        // AUMID 必须在创建任何窗口前确定：系统通知的设置项与通知中心条目都按它归档，
        // 晚于窗口设置会让首条通知落到按 exe 路径生成的默认 AUMID 上。
        NotificationService.SetProcessAppUserModelId();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
