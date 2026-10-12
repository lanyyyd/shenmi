using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

/// <summary>云游戏上号页：一键上号、更新 Cookie、检测有效性（逻辑对齐 Steam 上号的一键式习惯）。</summary>
public sealed partial class CloudGamePage : Page
{
    private readonly CloudGameService _cloud = new();
    private readonly ObservableCollection<CloudGameItem> _status = [];

    public CloudGamePage()
    {
        InitializeComponent();
        StatusList.ItemsSource = _status;

        foreach (var (key, name) in CloudGameService.Games)
        {
            GameCombo.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        }
        GameCombo.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            await EnsureEngineAsync();
            await RefreshStatusAsync();
        };
    }

    private async Task EnsureEngineAsync()
    {
        var ok = await EngineLauncher.EnsureStartedAsync();
        if (!ok)
        {
            ShowInfo("云游戏引擎未启动：请确认已部署 engine\\steameya-engine.exe", InfoBarSeverity.Warning);
        }
    }

    private string CurrentGame => (GameCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "genshin";

    private string CurrentBrowser => (BrowserCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "edge";

    private async Task RefreshStatusAsync()
    {
        try
        {
            var mine = await _cloud.MyAsync();
            _status.Clear();
            foreach (var item in mine.Games ?? [])
            {
                _status.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"刷新状态失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRefreshStatus(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private void OnGameChanged(object sender, SelectionChangedEventArgs e) { }

    private async void OnOneClickLogin(object sender, RoutedEventArgs e)
    {
        var game = CurrentGame;
        LoginButton.IsEnabled = false;
        try
        {
            var data = await _cloud.DownloadAsync(game);
            if (!data.Ok || data.Cookies is not { } cookies)
            {
                ShowInfo(data.Msg ?? "该游戏尚未上传 Cookie，请先「更新 Cookie」", InfoBarSeverity.Warning);
                return;
            }

            var (ok, body, error) = await _cloud.EngineLoginAsync(game, CurrentBrowser, cookies, data.ComboToken ?? "");
            if (!ok)
            {
                ShowInfo($"调用本地引擎失败：{error}（请确认引擎服务已启动）", InfoBarSeverity.Error);
                return;
            }

            var loggedIn = TryGetBool(body, "logged_in");
            ShowInfo(loggedIn ? "登录成功，浏览器已打开" : "已注入 Cookie，请在浏览器确认登录状态",
                loggedIn ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            await _cloud.ReportLoginAsync(game);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        var (ok, _, error) = await _cloud.EngineOpenAsync(CurrentGame, CurrentBrowser);
        ShowInfo(ok ? "已打开浏览器，请登录后点「抓取并上传」" : $"打开浏览器失败：{error}",
            ok ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
    }

    private async void OnCaptureUpload(object sender, RoutedEventArgs e)
    {
        var game = CurrentGame;
        var (ok, body, error) = await _cloud.EngineCaptureAsync(game);
        if (!ok)
        {
            ShowInfo($"抓取失败：{error}", InfoBarSeverity.Error);
            return;
        }

        if (!TryGetSub(body, "cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
        {
            ShowInfo("未抓取到 Cookie，请在浏览器登录后重试", InfoBarSeverity.Warning);
            return;
        }

        var combo = TryGetString(body, "combo_token") ?? string.Empty;
        var result = await _cloud.UploadAsync(game, cookies, combo);
        ShowInfo(result.Ok ? $"已上传（检测：{result.Detail}）" : (result.Msg ?? "上传失败"),
            result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await RefreshStatusAsync();
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        var result = await _cloud.CheckAsync(CurrentGame);
        ShowInfo(result.Ok ? $"检测完成：{result.Detail}" : (result.Msg ?? "检测失败"),
            result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await RefreshStatusAsync();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    // ---------------- JSON 小工具（避免额外 DTO） ----------------
    private static bool TryGetBool(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetString(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetSub(string json, string key, out JsonElement value)
    {
        value = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var v))
            {
                value = v.Clone();
                return true;
            }
        }
        catch
        {
            // 忽略
        }
        return false;
    }
}