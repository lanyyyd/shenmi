using System.Diagnostics;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Steam「在玩」心跳：每 60 秒检查一次 steam.exe，运行中续期、退出后立刻上报。
/// 作为服务端 Steam Web API 的兜底：没有配置 api_key 或账号映射时也能显示「在玩 Steam」。
/// </summary>
internal static class PresenceService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static CancellationTokenSource? _cts;
    private static bool _lastRunning;

    public static void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var cloud = new CloudGameService();

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!SessionState.Offline && !string.IsNullOrWhiteSpace(SessionState.Token))
                    {
                        var running = Process.GetProcessesByName("steam").Length > 0;
                        // 运行中每轮都发（心跳）；退出时只补发一次 false
                        if (running || running != _lastRunning)
                        {
                            await cloud.ReportSteamAsync(running, ct);
                            _lastRunning = running;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 单次失败不影响下一轮
                }

                try
                {
                    await Task.Delay(Interval, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }
}
