using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 离线/局域网模式：拉起本机统一网关（复用同一套后端代码），数据落在非 C 盘目录。
/// 打包时把网关放在 &lt;AppDir&gt;\gateway\ 下（steameya-gateway.exe 或 run_local.py）。
/// </summary>
internal static class GatewayLauncher
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static Process? _process;

    public static bool IsRunning => _process is { HasExited: false };

    /// <summary>离线数据目录：优先软件所在盘；若在 C 盘则改用其他可用盘。</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var root = Path.GetPathRoot(baseDir) ?? "C:\\";
        if (!root.StartsWith("C", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(baseDir, "data");
        }
        // 软件装在 C 盘：改用其它可用盘符，避免离线聊天数据落在 C 盘
        for (var c = 'D'; c <= 'Z'; c++)
        {
            var candidate = $"{c}:\\";
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "SteamEYA", "data");
            }
        }
        return Path.Combine(baseDir, "data");
    }

    /// <summary>启动本地网关并等待就绪；返回是否启动成功。</summary>
    public static async Task<bool> EnsureStartedAsync(string deviceName, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            return true;
        }

        Directory.CreateDirectory(DataDirectory);
        var gatewayDir = Path.Combine(AppContext.BaseDirectory, "gateway");
        var exe = Path.Combine(gatewayDir, "steameya-gateway.exe");
        var script = Path.Combine(gatewayDir, "run_local.py");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (File.Exists(exe))
        {
            psi.FileName = exe;
            psi.Arguments = "--local --port 8898";
        }
        else if (File.Exists(script))
        {
            psi.FileName = "python";
            psi.Arguments = $"\"{script}\" --port 8898";
        }
        else
        {
            // 开发期：直接用仓库里的 server/gateway_main.py
            var devScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "server", "gateway_main.py"));
            if (!File.Exists(devScript))
            {
                return false;
            }
            psi.FileName = "python";
            psi.Arguments = $"\"{devScript}\" --local --port 8898";
        }

        psi.Environment["EYA_DATA_DIR"] = DataDirectory;
        psi.Environment["EYA_INSTALL_ID"] = SessionState.InstallId;
        psi.Environment["EYA_DEVICE_NAME"] = deviceName;
        psi.Environment["EYA_LAN_PORT"] = "8898";
        // 显式开启局域网发现：离线模式本就默认开启；在线模式下由聊天室的“局域网互传”按需拉起本机网关，
        // 让“在线/离线都能局域网互传”在客户端侧也成立。
        psi.Environment["EYA_LAN_DISCOVERY"] = "1";
        // 聊天前端目录：优先 <AppDir>\web，其次 <AppDir>\gateway\web
        var webDir = Path.Combine(AppContext.BaseDirectory, "web");
        if (!Directory.Exists(webDir))
        {
            webDir = Path.Combine(gatewayDir, "web");
        }
        if (Directory.Exists(webDir))
        {
            psi.Environment["EYA_WEB_DIR"] = webDir;
        }

        try
        {
            _process = Process.Start(psi);
        }
        catch
        {
            return false;
        }
        if (_process is null)
        {
            return false;
        }

        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            if (await HealthAsync(ct))
            {
                return true;
            }
            if (_process.HasExited)
            {
                return false;
            }
        }
        return false;
    }

    public static void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _process = null;
        }
    }

    private static async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await HttpClient.GetAsync(SessionState.LocalGatewayUrl + "/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}