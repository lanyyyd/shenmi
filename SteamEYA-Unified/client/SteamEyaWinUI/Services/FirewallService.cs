using System.ComponentModel;
using System.Diagnostics;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 局域网模式所需的 Windows 防火墙入站规则。
///
/// 离线/局域网模式下每台电脑都要接收对端转发来的消息与文件，因此每台机器都必须放行
/// TCP 8898（网关）与 UDP 8899（设备发现）。之前只能靠用户手动关防火墙，这里改成首次进入
/// 局域网时弹一次 UAC 自动添加，成功后记入设置，之后不再打扰。
/// </summary>
internal static class FirewallService
{
    private const string TcpRuleName = "SteamEYA LAN TCP 8898";
    private const string UdpRuleName = "SteamEYA LAN UDP 8899";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _attempted;

    /// <summary>规则是否已经成功添加过（持久化在设置里）。</summary>
    public static bool IsReady => AppState.SettingsService.Load().LanFirewallReady;

    /// <summary>
    /// 确保两条入站规则存在。已成功过则直接返回；<paramref name="force"/> 用于「重新放行」按钮
    /// （比如用户重装系统或第三方安全软件清掉规则后）。
    /// </summary>
    public static async Task<bool> EnsureRulesAsync(bool force = false)
    {
        if (!force)
        {
            if (_attempted)
            {
                return IsReady;
            }
            if (IsReady)
            {
                return true;
            }
        }

        await Gate.WaitAsync();
        try
        {
            if (!force)
            {
                if (_attempted)
                {
                    return IsReady;
                }
                if (IsReady)
                {
                    return true;
                }
            }

            _attempted = true;
            var ok = await Task.Run(ApplyRules);
            if (ok)
            {
                var settings = AppState.SettingsService.Load();
                settings.LanFirewallReady = true;
                AppState.SettingsService.Save(settings);
            }
            return ok;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>一次性执行删除 + 添加，保证重复执行不会堆出同名规则，而且只弹一次 UAC。</summary>
    private static bool ApplyRules()
    {
        var commands = new List<string>
        {
            // 先删（可能不存在，失败无所谓），再按固定端口重建。
            $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1",
            $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1",
            // profile=any：学校/公司网络经常被识别成「公用」，只放行专用网络会失效。
            $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow " +
            "protocol=TCP localport=8898 profile=any enable=yes >nul",
            $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow " +
            "protocol=UDP localport=8899 profile=any enable=yes >nul",
        };

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + string.Join(" & ", commands),
            UseShellExecute = true,
            Verb = "runas",   // 添加防火墙规则必须提权；用户点「是」后静默执行
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(20000))
            {
                AppLog.Warn("添加防火墙规则超时。");
                return false;
            }
            if (process.ExitCode == 0)
            {
                AppLog.Info("已添加局域网防火墙入站规则（TCP 8898 / UDP 8899）。");
                return true;
            }
            AppLog.Warn($"添加防火墙规则失败，netsh 退出码 {process.ExitCode}。");
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 里点了「否」：不改设置，下次启动会再问一次。
            AppLog.Warn("用户取消了防火墙放行。");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"添加防火墙规则失败：{ex.Message}");
            return false;
        }
    }
}
