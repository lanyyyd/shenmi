using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 把「个性化」设置跟随账号同步到服务端：主题、主题色、自定义背景（含文件）与不透明度。
///
/// 服务端只存 JSON + 一份背景文件；客户端在登录/启动后拉取一次，本地改动防抖后推送。
/// 换机器登录同一账号时，背景会按 sha256 判断是否需要重新下载。
/// </summary>
internal static class SettingsSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly object Gate = new();

    private static CancellationTokenSource? _debounceCts;
    private static int _pushing;
    private static int _queued;
    private static int _wantBackground;

    /// <summary>登录成功 / 启动时调用：拉取并应用服务端保存的个性化设置。</summary>
    public static async Task PullAsync()
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }

        try
        {
            using var req = NewRequest(HttpMethod.Get, "/api/client-settings");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"拉取账号设置失败：HTTP {(int)resp.StatusCode}");
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var serverUpdatedAt = root.TryGetProperty("updatedAt", out var ua) && ua.TryGetInt64(out var ts) ? ts : 0;
            if (!root.TryGetProperty("settings", out var server) || server.ValueKind != JsonValueKind.Object)
            {
                // 服务端还没有这个账号的设置：把本机当前状态作为初始值传上去。
                await PushAsync(uploadBackground: true);
                return;
            }

            var local = AppState.SettingsService.Load();
            var ownerChanged = !string.Equals(local.SettingsOwnerUsername, SessionState.Username,
                StringComparison.OrdinalIgnoreCase);
            if (!ownerChanged && local.SettingsUpdatedAt > serverUpdatedAt)
            {
                // 本机改得更晚（例如离线时调过），不要把本地覆盖掉，改为把本地推上去。
                await PushAsync(uploadBackground: true);
                return;
            }

            local.SettingsOwnerUsername = SessionState.Username;
            local.SettingsUpdatedAt = serverUpdatedAt;
            ApplyServerFields(local, server);
            await ApplyServerBackgroundAsync(local, server);
            AppState.SettingsService.Save(local);
            ApplyRuntime(local);
            AppLog.Info($"已应用账号设置：theme={local.Theme}，背景={(local.CustomBackgroundEnabled ? "开" : "关")}。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"拉取账号设置失败：{ex.Message}");
        }
    }

    /// <summary>设置页改动后调用；<paramref name="backgroundChanged"/> 时会把背景文件一并上传。</summary>
    public static void NotifyChanged(bool backgroundChanged = false)
    {
        if (backgroundChanged)
        {
            Interlocked.Exchange(ref _wantBackground, 1);
        }

        // 先本地记下修改时间，即使现在离线、之后才联网，也不会被旧云端设置覆盖。
        try
        {
            var settings = AppState.SettingsService.Load();
            settings.SettingsOwnerUsername ??= SessionState.Username;
            settings.SettingsUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AppState.SettingsService.Save(settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"记录设置修改时间失败：{ex.Message}");
        }

        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
            }
            catch
            {
                // 已释放
            }

            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebounceAsync(cts.Token);
        }
    }

    private static async Task DebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await PushAsync(Interlocked.Exchange(ref _wantBackground, 0) == 1);
    }

    private static async Task PushAsync(bool uploadBackground)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }

        Interlocked.Exchange(ref _queued, 1);
        if (Interlocked.Exchange(ref _pushing, 1) == 1)
        {
            return;   // 已有推送在跑，它会把 _queued 里的最新状态再推一次
        }

        try
        {
            while (Interlocked.Exchange(ref _queued, 0) == 1)
            {
                var wantBg = uploadBackground || Interlocked.Exchange(ref _wantBackground, 0) == 1;
                await PushOnceAsync(wantBg);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"同步账号设置失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pushing, 0);
        }
    }

    private static async Task PushOnceAsync(bool uploadBackground)
    {
        var settings = AppState.SettingsService.Load();
        if (uploadBackground && settings.CustomBackgroundEnabled)
        {
            var path = AppState.SettingsService.GetCustomBackgroundImagePath(settings);
            if (path is not null)
            {
                await UploadBackgroundAsync(path);
            }
        }

        var payload = new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["theme"] = settings.Theme,
                ["uiColor"] = settings.UiColor,
                ["uiColorAnimated"] = settings.UiColorAnimated,
                ["customBackgroundEnabled"] = settings.CustomBackgroundEnabled,
                ["customBackgroundOpacity"] = settings.CustomBackgroundOpacity
            }
        };

        using var req = NewRequest(HttpMethod.Put, "/api/client-settings");
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            AppLog.Warn($"同步账号设置失败：HTTP {(int)resp.StatusCode}");
            return;
        }

        settings.SettingsOwnerUsername = SessionState.Username;
        AppState.SettingsService.Save(settings);
    }

    private static async Task UploadBackgroundAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0 || bytes.Length > 60 * 1024 * 1024)
            {
                AppLog.Warn("背景文件为空或超过 60MB，跳过上传。");
                return;
            }

            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(path));
            form.Add(content, "file", Path.GetFileName(path));

            using var req = NewRequest(HttpMethod.Post, "/api/client-settings/background");
            req.Content = form;
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"上传账号背景失败：HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"上传账号背景失败：{ex.Message}");
        }
    }

    private static void ApplyServerFields(AppSettings local, JsonElement server)
    {
        if (server.TryGetProperty("theme", out var v) && v.ValueKind == JsonValueKind.String)
        {
            local.Theme = v.GetString() ?? local.Theme;
        }
        if (server.TryGetProperty("uiColor", out v))
        {
            local.UiColor = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        if (server.TryGetProperty("uiColorAnimated", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            local.UiColorAnimated = v.GetBoolean();
        }
        if (server.TryGetProperty("customBackgroundEnabled", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            local.CustomBackgroundEnabled = v.GetBoolean();
        }
        if (server.TryGetProperty("customBackgroundOpacity", out v) && v.ValueKind == JsonValueKind.Number)
        {
            local.CustomBackgroundOpacity = Math.Clamp(v.GetDouble(), 0.1, 0.9);
        }
    }

    private static async Task ApplyServerBackgroundAsync(AppSettings local, JsonElement server)
    {
        string? url = null;
        string? sha = null;
        if (server.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.Object)
        {
            if (bg.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                url = u.GetString();
            }
            if (bg.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String)
            {
                sha = h.GetString();
            }
        }

        // 服务端没有背景文件但设置里写着启用：按关闭处理，避免出现「开关开着但没图」。
        if (local.CustomBackgroundEnabled && string.IsNullOrWhiteSpace(url))
        {
            local.CustomBackgroundEnabled = false;
            return;
        }
        if (!local.CustomBackgroundEnabled || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var current = AppState.SettingsService.GetCustomBackgroundImagePath(local);
        if (current is not null && !string.IsNullOrWhiteSpace(sha))
        {
            var localSha = await Task.Run(() => Sha256File(current));
            if (string.Equals(localSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var fullUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : SessionState.BaseUrl.TrimEnd('/') + url;
        var extension = Path.GetExtension(fullUrl.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".png";
        }
        var temp = Path.Combine(Path.GetTempPath(), "eya-account-bg-" + Guid.NewGuid().ToString("N") + extension);

        try
        {
            using var resp = await Http.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using (var input = await resp.Content.ReadAsStreamAsync())
            await using (var output = File.Create(temp))
            {
                await input.CopyToAsync(output);
            }

            var fileName = await Task.Run(() => AppState.SettingsService.ImportCustomBackgroundImage(temp));
            local.CustomBackgroundImageFileName = fileName;
            AppState.SettingsService.DeleteOtherCustomBackgroundImages(fileName);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件删不掉不影响设置
            }
        }
    }

    private static void ApplyRuntime(AppSettings settings)
    {
        var ui = MainWindow.Instance?.DispatcherQueue;
        if (ui is null)
        {
            return;
        }
        ui.TryEnqueue(() =>
        {
            AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);
            MainWindow.Instance?.ApplyTheme(UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor));
            MainWindow.Instance?.ApplyCustomBackground(settings);
        });
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, SessionState.BaseUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionState.Token);
        return req;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        _ => "image/jpeg"
    };
}
