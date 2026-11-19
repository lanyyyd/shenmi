using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

/// <summary>驱动准备进度（Percent 为 0~100 的“驱动阶段自身”进度，由调用方换算成总进度）。</summary>
internal sealed record DriverProgress(
    string Stage, double Percent, long Bytes = 0, long Total = 0, double Speed = 0, int Threads = 0);

/// <summary>
/// 浏览器驱动准备：先查本地缓存，缺失时从国内镜像（npmmirror）多线程下载。
///
/// 这样做的原因：Selenium Manager 会在上号时同步联网解析并下载 driver，弱网下能卡几分钟。
/// 这里把下载提前到本地完成，并复用百宝箱的 <see cref="DownloadEngine"/>（分段并发 + 断点续传）。
/// 任何一步失败都返回 null，让引擎退回 Selenium Manager 的原始行为，不阻断上号。
/// </summary>
internal static class BrowserDriverProvisioner
{
    private const int DownloadThreads = 8;

    private const string EdgeIndexUrl = "https://registry.npmmirror.com/-/binary/edgedriver/";

    private const string ChromeIndexUrl = "https://registry.npmmirror.com/-/binary/chrome-for-testing/";

    private const string ChromeLegacyIndexUrl = "https://registry.npmmirror.com/-/binary/chromedriver/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly Lazy<string> Root = new(ResolveRoot);

    /// <summary>驱动缓存根目录（惰性解析一次）：「能写优先放数据目录」，避免 C 盘还原后要重下。</summary>
    private static string CacheRoot => Root.Value;

    /// <summary>
    /// 驱动缓存目录：优先跟随 <see cref="EngineLauncher.DataDirectory"/>（装机在非 C 盘时就在安装盘，
    /// 否则用 D:\SteamEYA\engine-data），目录不可写时退回 %LOCALAPPDATA%。
    /// 首次解析时把旧位置已有的驱动搬过来，避免升级后白下一次。
    /// </summary>
    private static string ResolveRoot()
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeaTT", "drivers");

        try
        {
            var primary = Path.Combine(EngineLauncher.DataDirectory, "drivers");
            Directory.CreateDirectory(primary);
            if (IsWritable(primary))
            {
                MigrateLegacy(legacy, primary);
                return primary;
            }

            AppLog.Warn($"驱动缓存目录不可写，退回 {legacy}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解析驱动缓存目录失败，退回 {legacy}：{ex.Message}");
        }

        return legacy;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把旧缓存目录里的驱动搬到新目录；搬失败就留着，不影响功能。</summary>
    private static void MigrateLegacy(string from, string to)
    {
        try
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from))
            {
                return;
            }

            var moved = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                moved++;
            }

            if (moved > 0)
            {
                AppLog.Info($"已把 {moved} 个驱动文件迁移到 {to}");
            }

            try
            {
                Directory.Delete(from, recursive: true);
            }
            catch
            {
                // 旧目录删不掉（文件占用）就留着，下次启动再试。
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"迁移旧驱动缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 返回驱动 exe 的本地路径；返回 null 表示“这次不管”，交给 Selenium Manager 处理。
    /// </summary>
    public static async Task<string?> EnsureAsync(
        string browser, IProgress<DriverProgress>? progress, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new DriverProgress("正在检查浏览器版本", 2));
            var detected = DetectBrowser(browser);
            if (detected is null)
            {
                AppLog.Warn($"未识别到 {browser} 的安装信息，跳过驱动预下载");
                return null;
            }

            var (fullVersion, major, exeName) = detected.Value;
            var dir = Path.Combine(CacheRoot, browser, major);
            var cached = FindCachedDriver(dir, exeName);
            if (cached is not null)
            {
                progress?.Report(new DriverProgress($"已命中驱动缓存（{major}）", 100));
                return cached;
            }

            progress?.Report(new DriverProgress($"正在解析驱动版本（浏览器 {fullVersion}）", 6));
            var url = await ResolveDownloadUrlAsync(browser, major, ct);
            if (url is null)
            {
                AppLog.Warn($"国内镜像没有 {browser} {major} 版本的驱动，改用 Selenium Manager");
                return null;
            }

            Directory.CreateDirectory(dir);
            var zipPath = await DownloadAsync(url, dir, progress, ct);
            if (zipPath is null)
            {
                return null;
            }

            progress?.Report(new DriverProgress("正在解压驱动", 92));
            var exePath = ExtractDriver(zipPath, dir, exeName);
            TryDelete(zipPath);
            if (exePath is null)
            {
                AppLog.Warn("驱动压缩包里没有找到 exe，改用 Selenium Manager");
                return null;
            }

            progress?.Report(new DriverProgress("驱动准备完成", 100));
            return exePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"准备浏览器驱动失败，退回 Selenium Manager：{ex.Message}");
            return null;
        }
    }

    /// <summary>只查缓存，不联网：给「更新 Cookie」这类顺带打开浏览器的场景用。</summary>
    public static string? TryGetCachedPath(string browser)
    {
        try
        {
            var detected = DetectBrowser(browser);
            if (detected is null)
            {
                return null;
            }

            var (_, major, exeName) = detected.Value;
            return FindCachedDriver(Path.Combine(CacheRoot, browser, major), exeName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载 zip；用百宝箱的引擎按 250ms 采样上报速度。失败返回 null。</summary>
    private static async Task<string?> DownloadAsync(
        string url, string dir, IProgress<DriverProgress>? progress, CancellationToken ct)
    {
        var engine = new DownloadEngine(url, dir, DownloadThreads);
        var task = engine.RunAsync();
        var previousBytes = 0L;
        var stopwatch = Stopwatch.StartNew();
        var reported = false;

        while (!task.IsCompleted)
        {
            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                engine.Cancel();
                throw;
            }

            engine.SampleSpeed(stopwatch.Elapsed, previousBytes);
            previousBytes = engine.ReceivedBytes;
            stopwatch.Restart();

            var total = engine.TotalBytes;
            reported = total > 0;
            // 驱动阶段内部进度：探测/建连占 8%，下载占 8%~90%。
            var percent = total > 0 ? 8 + (82.0 * previousBytes / total) : 8;
            progress?.Report(new DriverProgress(
                reported ? "正在从国内镜像下载浏览器驱动" : "正在连接镜像",
                Math.Clamp(percent, 8, 90),
                previousBytes,
                total,
                engine.Speed,
                engine.Connections));
        }

        await task;
        if (engine.Status != Models.DownloadStatus.Completed || string.IsNullOrWhiteSpace(engine.TargetPath))
        {
            AppLog.Warn($"驱动下载未完成（{engine.Status}）：{url}");
            return null;
        }

        return engine.TargetPath;
    }

    /// <summary>解压出驱动 exe 并放到缓存目录根部；已存在则不覆盖（可能正被占用）。</summary>
    private static string? ExtractDriver(string zipPath, string dir, string exeName)
    {
        var target = Path.Combine(dir, exeName);
        if (File.Exists(target))
        {
            return target;
        }

        var tempDir = Path.Combine(dir, "_extract");
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            var exe = Directory
                .EnumerateFiles(tempDir, exeName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (exe is null)
            {
                return null;
            }

            File.Copy(exe, target, overwrite: true);
            return target;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响驱动可用性。
            }
        }
    }

    private static string? FindCachedDriver(string dir, string exeName)
    {
        try
        {
            var path = Path.Combine(dir, exeName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按主版本号在镜像索引里挑驱动，并逐个探测是否真有 win64 包。
    /// 镜像存在「版本目录同步不完整，只有 linux 包」的情况，所以不能只取版本号最大的那个。
    /// </summary>
    private static async Task<string?> ResolveDownloadUrlAsync(string browser, string major, CancellationToken ct)
    {
        // Chrome 从 115 起改用 chrome-for-testing 的目录结构，更早的版本只有老的 chromedriver 目录。
        var legacyChrome = browser != "edge" && int.TryParse(major, out var m) && m < 115;
        var indexUrl = browser == "edge"
            ? EdgeIndexUrl
            : legacyChrome ? ChromeLegacyIndexUrl : ChromeIndexUrl;
        var json = await Http.GetStringAsync(indexUrl, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<(int[] Parts, string Version)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameNode))
            {
                continue;
            }

            var name = (nameNode.GetString() ?? string.Empty).Trim('/');
            var parts = ParseVersion(name);
            if (parts.Length < 4 || parts[0].ToString() != major)
            {
                continue;
            }

            candidates.Add((parts, name));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // 从新到旧最多探 8 个，避免同步不全的版本把整个流程拖长。
        candidates.Sort((a, b) => CompareVersion(b.Parts, a.Parts));
        foreach (var (_, version) in candidates.Take(8))
        {
            var url = BuildDriverUrl(browser, legacyChrome, version);
            if (await IsAvailableAsync(url, ct))
            {
                return url;
            }
        }

        return null;
    }

    private static string BuildDriverUrl(string browser, bool legacyChrome, string version)
    {
        if (browser == "edge")
        {
            return $"https://cdn.npmmirror.com/binaries/edgedriver/{version}/edgedriver_win64.zip";
        }

        return legacyChrome
            ? $"https://cdn.npmmirror.com/binaries/chromedriver/{version}/chromedriver_win32.zip"
            : $"https://cdn.npmmirror.com/binaries/chrome-for-testing/{version}/win64/chromedriver-win64.zip";
    }

    private static async Task<bool> IsAvailableAsync(string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(head, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // 个别 CDN 不支持 HEAD：用一个字节的 Range 探测。
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new RangeHeaderValue(0, 1);
            using var probe = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            return probe.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读注册表/文件版本拿到浏览器主版本号。</summary>
    private static (string Full, string Major, string ExeName)? DetectBrowser(string browser)
    {
        if (browser == "edge")
        {
            var version = ReadVersion(@"SOFTWARE\WOW6432Node\Microsoft\Edge\BLBeacon")
                          ?? ReadVersion(@"SOFTWARE\Microsoft\Edge\BLBeacon")
                          ?? FileVersion(FindFirstExisting(
                              @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                              @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"));
            return WithMajor(version, "msedgedriver.exe");
        }

        if (browser == "chrome")
        {
            var version = ReadVersion(@"SOFTWARE\WOW6432Node\Google\Chrome\BLBeacon")
                          ?? ReadVersion(@"SOFTWARE\Google\Chrome\BLBeacon")
                          ?? FileVersion(FindFirstExisting(
                              @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                              @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"));
            return WithMajor(version, "chromedriver.exe");
        }

        if (browser == "360")
        {
            var exe = FindFirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"360Chrome\Chrome\Application\360chrome.exe"),
                @"C:\Program Files (x86)\360\360Chrome\Chrome\Application\360chrome.exe",
                @"C:\Program Files\360\360Chrome\Chrome\Application\360chrome.exe");
            return WithMajor(FileVersion(exe), "chromedriver.exe");
        }

        return null;
    }

    private static (string Full, string Major, string ExeName)? WithMajor(string? version, string exeName)
    {
        var parts = ParseVersion(version);
        if (parts.Length == 0)
        {
            return null;
        }

        return (version ?? string.Empty, parts[0].ToString(), exeName);
    }

    private static string? ReadVersion(string subKey)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue("version") is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
                // 注册表读不到就走文件版本兜底。
            }
        }

        return null;
    }

    private static string? FileVersion(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFirstExisting(params string?[] paths)
        => paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    private static int[] ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<int>();
        }

        var parts = new List<int>();
        foreach (var token in raw.Split('.', '-', '+', ' '))
        {
            if (!int.TryParse(token, out var value))
            {
                break;
            }

            parts.Add(value);
            if (parts.Count == 4)
            {
                break;
            }
        }

        return parts.ToArray();
    }

    private static int CompareVersion(int[] left, int[] right)
    {
        for (var i = 0; i < 4; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 删不掉就留着，下次同名会避让。
        }
    }
}
