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

    /// <summary>game key → 打开地址。自定义游戏靠它导航，内置游戏留空走引擎查表。</summary>
    private readonly Dictionary<string, string> _gameUrls = new(StringComparer.Ordinal);
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
            await LoadGamesAsync();
            await EnsureEngineAsync();
            await RefreshStatusAsync();
            await AutoCheckAsync();
        };
    }

    /// <summary>
    /// 打开页面时对已上传 Cookie 的游戏并行检测一次。
    /// 剩余免费时长只在「检测」时由服务端向游戏方查询并回写，不检测就一直是空，
    /// 这里主动刷新一次，用户进来即可看到时长。
    /// </summary>
    private async Task AutoCheckAsync()
    {
        var games = _status.Where(g => g.Valid != 0).Select(g => g.Game).ToArray();
        if (games.Length == 0)
        {
            return;
        }
        try
        {
            await Task.WhenAll(games.Select(g => _cloud.CheckAsync(g)));
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"自动检测云游戏 Cookie 失败：{ex.Message}");
        }
    }

    private async Task EnsureEngineAsync()
    {
        var ok = await EngineLauncher.EnsureStartedAsync();
        if (!ok)
        {
            ShowInfo("云游戏引擎未启动：请确认已部署 engine\\steameya-engine.exe", InfoBarSeverity.Warning);
        }
    }

    // ---------------- 游戏列表 ----------------

    /// <summary>从服务端拉可见游戏（内置 + 别人共享的自定义游戏 + 自己的）。失败就保留内置兜底。</summary>
    private async Task LoadGamesAsync(string? selectKey = null)
    {
        try
        {
            var dto = await _cloud.GamesAsync();
            if (!dto.Ok || dto.Games is not { Count: > 0 } games)
            {
                return;
            }

            var target = selectKey ?? CurrentGame;
            GameCombo.Items.Clear();
            _gameUrls.Clear();
            foreach (var game in games)
            {
                GameCombo.Items.Add(new ComboBoxItem { Content = game.Name, Tag = game.Key });
                if (!game.Builtin && !string.IsNullOrWhiteSpace(game.Url))
                {
                    _gameUrls[game.Key] = game.Url;
                }
            }

            var match = GameCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, target, StringComparison.Ordinal));

            GameCombo.SelectedItem = match ?? GameCombo.Items[0];
        }
        catch (Exception ex)
        {
            AppLog.Debug($"拉取云游戏列表失败，保留内置列表：{ex.Message}");
        }
    }

    /// <summary>添加自定义游戏：填名称 + 默认地址，地址不写协议由服务端补 https://。</summary>
    private async void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "游戏名",
            PlaceholderText = "例如：抖音"
        };
        var urlBox = new TextBox
        {
            Header = "默认打开地址",
            PlaceholderText = "douyin.com（不用写 https://）"
        };
        var tip = new TextBlock
        {
            Text = "保存后只有你能看到；成功抓取一次 Cookie 后，所有用户都能在游戏列表里选到它。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(tip);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加自定义游戏",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var url = urlBox.Text.Trim();
        if (name.Length == 0 || url.Length == 0)
        {
            ShowInfo("请填写游戏名和打开地址", InfoBarSeverity.Warning);
            return;
        }

        AddGameButton.IsEnabled = false;
        try
        {
            var result = await _cloud.CreateGameAsync(name, url);
            if (result is null || !result.Ok || string.IsNullOrWhiteSpace(result.Key))
            {
                ShowInfo("添加失败，请稍后重试", InfoBarSeverity.Error);
                return;
            }

            await LoadGamesAsync(result.Key);
            ShowInfo(
                result.Existing
                    ? $"「{name}」已经在列表里了"
                    : $"已添加「{name}」；抓取一次 Cookie 后所有人都能看到",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("添加自定义云游戏失败。", ex);
            ShowInfo($"添加失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            AddGameButton.IsEnabled = true;
        }
    }

    private string CurrentGame => (GameCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "genshin";

    private string CurrentBrowser => (BrowserCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "edge";

    /// <summary>当前游戏的打开地址；内置游戏返回 null。</summary>
    private string? CurrentGameUrl => _gameUrls.GetValueOrDefault(CurrentGame);

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

            var (ok, body, error) = await _cloud.EngineLoginAsync(game, CurrentBrowser, cookies, data.ComboToken ?? "", CurrentGameUrl);
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
        var (ok, _, error) = await _cloud.EngineOpenAsync(CurrentGame, CurrentBrowser, CurrentGameUrl);
        ShowInfo(ok ? "已打开浏览器，请登录后点「抓取并上传」" : $"打开浏览器失败：{error}",
            ok ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
    }

    private async void OnCaptureUpload(object sender, RoutedEventArgs e)
    {
        var game = CurrentGame;
        var (ok, body, error) = await _cloud.EngineCaptureAsync(game, CurrentGameUrl);
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
        // 服务端 check 的 msg 已含「有效，剩余免费时长 X 分钟」，优先展示它（detail 只有 upload 才返回）。
        var text = result.Ok
            ? (result.Msg ?? result.Detail ?? "检测完成")
            : (result.Msg ?? result.Detail ?? "检测失败");
        ShowInfo(text, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
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