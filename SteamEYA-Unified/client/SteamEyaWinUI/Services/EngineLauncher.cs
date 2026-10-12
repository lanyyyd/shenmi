using System.Diagnostics;
using System.Net.Http;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 云游戏引擎（Selenium 本地服务）启动器。
/// 打包后放在 &lt;AppDir&gt;\engine\steameya-engine.exe；数据目录尽量放在非 C 盘。
/// </summary>
internal static class EngineLauncher
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static Process? _process;

    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var root = Path.GetPathRoot(baseDir) ?? "C:\\";
        if (!root.StartsWith("C", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(baseDir, "engine", "data");
        }
        for (var c = 'D'; c <= 'Z'; c++)
        {
            var candidate = $"{c}:\\";
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "SteamEYA", "engine-data");
            }
        }
        return Path.Combine(baseDir, "engine", "data");
    }

    /// <summary>确保引擎服务在运行；已运行或启动成功返回 true。</summary>
    public static async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (await HealthAsync(ct))
        {
            return true;
        }

        var engineDir = Path.Combine(AppContext.BaseDirectory, "engine");
        var exe = Path.Combine(engineDir, "steameya-engine.exe");
        var script = Path.Combine(engineDir, "local_service.py");

        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (File.Exists(exe))
        {
            psi.FileName = exe;
            psi.Arguments = "--port 6971";
        }
        else if (File.Exists(script))
        {
            psi.FileName = "python";
            psi.Arguments = $"\"{script}\" --port 6971";
        }
        else
        {
            var devScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "engine", "local_service.py"));
            if (!File.Exists(devScript))
            {
                return false;
            }
            psi.FileName = "python";
            psi.Arguments = $"\"{devScript}\" --port 6971";
        }

        Directory.CreateDirectory(DataDirectory);
        psi.Environment["EYA_ENGINE_DATA"] = DataDirectory;

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

        for (var i = 0; i < 20; i++)
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

    private static async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await HttpClient.GetAsync(SessionState.EngineUrl + "/engine/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}