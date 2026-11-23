using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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

    /// <summary>「在玩」心跳：上号成功后每 60 秒续一次，浏览器被关闭或切换游戏时停止。</summary>
    private CancellationTokenSource? _playCts;
    private string _playingGame = "";

    /// <summary>过期提醒绑定状态；程序化切换 ToggleSwitch 时用 _suppress 防重入。</summary>
    private bool _expiryBound;
    private bool _suppressExpiryToggle;

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
            // 切到本页先把账号列表刷出来（含别人分享给我的账号），再准备引擎和检测。
            await RefreshStatusAsync();
            await LoadGamesAsync();
            await EnsureEngineAsync();
            await AutoCheckAsync();
            await RefreshExpiryPushAsync();
        };
    }

    /// <summary>
    /// 打开页面时对已上传 Cookie 的账号并行检测一次。
    /// 剩余免费时长只在「检测」时由服务端向游戏方查询并回写，不检测就一直是空，
    /// 这里主动刷新一次，用户进来即可看到时长。
    /// 同一游戏可能存了多个账号，必须按「游戏 + UID」逐个检测，否则只有一条有状态。
    /// </summary>
    private async Task AutoCheckAsync()
    {
        var accounts = _status.Where(g => g.Valid != 0 && !g.Shared).ToArray();
        if (accounts.Length == 0)
        {
            return;
        }
        try
        {
            await Task.WhenAll(accounts.Select(g => _cloud.CheckAsync(g.Game, g.GameUid)));
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

            // 登记 key → 名称，供「我的账号」列表里只拿到 key 的地方反查。
            CloudGameService.RememberGames(games);

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

    /// <summary>当前游戏的显示名，传给引擎做提示文案，避免出现 custom_xxxxxxxx。</summary>
    private string CurrentGameName => CloudGameService.DisplayName(CurrentGame);

    /// <summary>当前游戏下选中的账号；同一游戏可以有多个，靠 UID 区分。</summary>
    private CloudGameItem? CurrentAccount => (AccountCombo.SelectedItem as ComboBoxItem)?.Tag as CloudGameItem;

    private string CurrentAccountUid => CurrentAccount?.GameUid ?? string.Empty;

    /// <summary>
    /// 按当前游戏重建「账号」下拉，尽量保持原先选中的 UID。
    /// 用 ComboBoxItem + Tag 承载数据：WinUI 3 里直接给 ItemsSource 绑 internal 模型再指定
    /// DisplayMemberPath，项会渲染成空白（左侧账号栏没内容就是这个原因），这里改为显式文本。
    /// </summary>
    private void RebuildAccountCombo(string? preferUid = null)
    {
        var items = _status.Where(i => string.Equals(i.Game, CurrentGame, StringComparison.Ordinal)).ToList();
        AccountCombo.Items.Clear();
        foreach (var it in items)
        {
            AccountCombo.Items.Add(new ComboBoxItem { Content = it.PickerLabel, Tag = it });
        }
        var pick = (!string.IsNullOrEmpty(preferUid)
                       ? items.FirstOrDefault(i => string.Equals(i.GameUid, preferUid, StringComparison.Ordinal))
                       : null)
                   ?? items.FirstOrDefault();
        AccountCombo.SelectedIndex = pick is null ? -1 : items.IndexOf(pick);
    }

    /// <summary>刷新右侧全部账号；preferUid 用于上传/点击后保持选中。</summary>
    private async Task RefreshStatusAsync(string? preferUid = null)
    {
        var keepUid = preferUid ?? CurrentAccountUid;
        try
        {
            var mine = await _cloud.MyAsync();
            _status.Clear();
            foreach (var item in mine.Games ?? [])
            {
                _status.Add(item);
            }
            RebuildAccountCombo(keepUid);
        }
        catch (Exception ex)
        {
            ShowInfo($"刷新状态失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRefreshStatus(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private void OnGameChanged(object sender, SelectionChangedEventArgs e) => RebuildAccountCombo();

    private void OnAccountChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>点右侧账号卡片：把左侧游戏和账号都切到这条，再点「一键上号」即可。</summary>
    private void OnAccountItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CloudGameItem item)
        {
            return;
        }

        var match = GameCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, item.Game, StringComparison.Ordinal));
        if (match is not null)
        {
            GameCombo.SelectedItem = match;   // 触发 OnGameChanged，先按游戏重建账号列表
        }
        RebuildAccountCombo(item.GameUid);

        var name = string.IsNullOrWhiteSpace(item.Nickname) ? item.GameUid : item.Nickname;
        ShowInfo($"已选择：{item.GameName} · {name} · UID {item.GameUid}，点「一键上号」开始",
            InfoBarSeverity.Informational);
    }

    private async void OnOneClickLogin(object sender, RoutedEventArgs e)
    {
        var game = CurrentGame;
        var account = CurrentAccount;
        if (account is null)
        {
            ShowInfo("该游戏还没有上传账号，请先「更新 Cookie」并「抓取并上传」，或在右侧选择已有账号",
                InfoBarSeverity.Warning);
            return;
        }

        var uid = account.GameUid;
        LoginButton.IsEnabled = false;
        try
        {
            // 同一个账号已经有人在玩时先提醒，避免两个人互相挤号。
            try
            {
                var occupant = await _cloud.PlayingAsync(game, uid);
                if (occupant is not null)
                {
                    var who = !string.IsNullOrWhiteSpace(occupant.Username)
                        ? occupant.Username
                        : (!string.IsNullOrWhiteSpace(occupant.Nickname) ? occupant.Nickname : "其他用户");
                    var dialog = new ContentDialog
                    {
                        Title = "账号正在使用",
                        Content = $"这个账号似乎已经被{who}在使用了喵~小心被挤号喵~",
                        PrimaryButtonText = "继续登录",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // 查冲突失败不影响上号
            }

            // 先把驱动准备好（命中缓存就秒过，缺失则从国内镜像多线程下），
            // 避免 Selenium Manager 在上号过程中同步联网下载把界面卡住。
            var driverProgress = new Progress<DriverProgress>(p => SetLoginProgress(
                p.Stage,
                5 + (p.Percent * 0.55),
                indeterminate: p.Total <= 0,
                p.Bytes,
                p.Total,
                p.Speed,
                p.Threads));
            SetLoginProgress("正在准备浏览器驱动", 2);
            var driverPath = await BrowserDriverProvisioner.EnsureAsync(CurrentBrowser, driverProgress);

            SetLoginProgress("正在获取账号 Cookie", 62, indeterminate: true);
            // 带 uid / owner 下载，确保上的是选中的那个号（分享账号还要带分享者 id）。
            var data = await _cloud.DownloadAsync(game, uid, account.OwnerId);
            if (!data.Ok || data.Cookies is not { } cookies)
            {
                ShowInfo(data.Msg ?? "该账号尚未上传 Cookie，请先「更新 Cookie」", InfoBarSeverity.Warning);
                HideLoginProgressSoon();
                return;
            }

            SetLoginProgress("正在打开浏览器并注入 Cookie", 72, indeterminate: true);
            var (ok, body, error) = await _cloud.EngineLoginAsync(
                game, CurrentBrowser, cookies, data.ComboToken ?? "", CurrentGameUrl, CurrentGameName, driverPath);
            if (!ok)
            {
                ShowInfo($"调用本地引擎失败：{error}（请确认引擎服务已启动）", InfoBarSeverity.Error);
                LoginProgressPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var loggedIn = TryGetBool(body, "logged_in");
            // 米哈游账号：服务端在下载前已用 stoken 换过票，这里把结果一并告诉用户。
            var refreshNote = data.RefreshOk switch
            {
                true => "，已自动续期 Cookie",
                false when data.NeedStoken => "，Cookie 里没有 stoken：请点「扫码补 stoken」",
                false when !string.IsNullOrWhiteSpace(data.RefreshMsg)
                    => $"，自动续期失败：{data.RefreshMsg}",
                _ => string.Empty
            };
            var failedRefresh = data.RefreshOk == false;
            SetLoginProgress(loggedIn ? "上号完成" : "已注入 Cookie，请在浏览器确认登录", 100);
            HideLoginProgressSoon();
            ShowInfo((loggedIn ? "登录成功，浏览器已打开" : "已注入 Cookie，请在浏览器确认登录状态") + refreshNote,
                failedRefresh ? InfoBarSeverity.Warning : (loggedIn ? InfoBarSeverity.Success : InfoBarSeverity.Warning));
            await _cloud.ReportLoginAsync(game, uid);
            StartPlayHeartbeat(game, uid);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 上号成功后开始「在玩」心跳：每 45 秒向服务端续一次，同时问本地引擎浏览器还在不在。
    /// 浏览器被用户关掉 → 立即上报退出；App 直接被 kill → 服务端 90 秒 TTL 兜底。
    /// </summary>
    private void StartPlayHeartbeat(string game, string uid)
    {
        var previous = _playingGame;
        CancelPlayHeartbeat();
        _playingGame = game;
        if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, game, StringComparison.Ordinal))
        {
            _ = _cloud.ReportStopAsync(previous);
        }

        _playCts = new CancellationTokenSource();
        var ct = _playCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // 先续心跳，再查浏览器；即使引擎查询卡住，上报也已经续上了
                await _cloud.ReportPlayAsync(game, uid, ct);

                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probe.CancelAfter(TimeSpan.FromSeconds(8));
                    var alive = await _cloud.EngineStatusAsync(game, probe.Token);
                    if (alive == false)
                    {
                        await _cloud.ReportStopAsync(game, ct);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
                catch
                {
                    // 单次查询失败不影响下一轮
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }

    /// <summary>
    /// 只停心跳、不上报退出：切换游戏时由 StartPlayHeartbeat 单独上报旧游戏的退出；
    /// 用户离开本页面但浏览器还开着仍算在玩，关闭 App 则靠服务端 90 秒 TTL 自然过期。
    /// </summary>
    private void CancelPlayHeartbeat()
    {
        var cts = _playCts;
        _playCts = null;
        _playingGame = "";
        cts?.Cancel();
        cts?.Dispose();
    }

    /// <summary>更新上号进度：stage 文字、下方进度条（右侧显示总百分比），下载时带速度。</summary>
    private void SetLoginProgress(
        string stage, double percent, bool indeterminate = false,
        long bytes = 0, long total = 0, double speed = 0, int threads = 0)
    {
        LoginProgressPanel.Visibility = Visibility.Visible;
        LoginProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate)
        {
            LoginProgressBar.Value = Math.Clamp(percent, 0, 100);
        }

        var text = stage;
        if (total > 0 && !indeterminate)
        {
            text += $" · {FormatBytes(bytes)} / {FormatBytes(total)}";
        }

        if (speed > 0)
        {
            text += speed >= 1024 * 1024
                ? $" · {speed / 1024 / 1024:0.0} MB/s"
                : $" · {speed / 1024:0} KB/s";
        }

        if (threads > 1)
        {
            text += $" · {threads} 线程";
        }

        LoginProgressText.Text = text;
        LoginPercentText.Text = indeterminate ? "···" : $"{Math.Clamp(percent, 0, 100):0}%";
    }

    private void HideLoginProgressSoon()
    {
        _ = Task.Delay(1200).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            LoginProgressPanel.Visibility = Visibility.Collapsed;
        }));
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1024L * 1024 * 1024 => $"{value / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{value / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{value / 1024.0:0} KB",
        _ => $"{value} B"
    };

    private async void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        // 更新 Cookie 也先把驱动准备好：否则这里走 Selenium Manager 下一份，
        // 随后「一键上号」在应用缓存里找不到，又会从镜像再下一份。
        var driverPath = BrowserDriverProvisioner.TryGetCachedPath(CurrentBrowser);
        if (driverPath is null)
        {
            ShowInfo("首次使用，正在准备浏览器驱动…", InfoBarSeverity.Informational);
            driverPath = await BrowserDriverProvisioner.EnsureAsync(CurrentBrowser, null);
        }

        var (ok, _, error) = await _cloud.EngineOpenAsync(
            CurrentGame, CurrentBrowser, CurrentGameUrl, CurrentGameName, driverPath);
        ShowInfo(ok ? "已打开浏览器，请登录后点「抓取并上传」" : $"打开浏览器失败：{error}",
            ok ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
    }

    private async void OnCaptureUpload(object sender, RoutedEventArgs e)
    {
        var game = CurrentGame;
        var (ok, body, error) = await _cloud.EngineCaptureAsync(game, CurrentGameUrl, CurrentGameName);
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
        var refreshNote = result.RefreshOk switch
        {
            true => "，stoken 自动续期已开启",
            false when result.NeedStoken => "（Cookie 里没有 stoken，请点「扫码补 stoken」）",
            false when !string.IsNullOrWhiteSpace(result.RefreshMsg) => $"（自动续期失败：{result.RefreshMsg}）",
            _ => string.Empty
        };
        ShowInfo(result.Ok ? $"已上传（检测：{result.Detail}{refreshNote}）" : (result.Msg ?? "上传失败"),
            result.Ok && result.RefreshOk != false ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        // 上传成功后选中刚上传的那个账号（同一个游戏可能已经有好几个号）
        await RefreshStatusAsync(result.Uid);
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        var account = CurrentAccount;
        if (account is null)
        {
            ShowInfo("该游戏还没有上传账号，请先「更新 Cookie」并「抓取并上传」", InfoBarSeverity.Warning);
            return;
        }
        if (account.Shared)
        {
            ShowInfo("这是别人分享给你的账号，只有分享者可以检测有效性", InfoBarSeverity.Warning);
            return;
        }

        var uid = account.GameUid;
        CheckButton.IsEnabled = false;
        try
        {
            var result = await _cloud.CheckAsync(CurrentGame, uid);
            // 服务端 check 的 msg 已含「有效，剩余免费时长 X 分钟」，优先展示它（detail 只有 upload 才返回）。
            var text = result.Ok
                ? (result.Msg ?? result.Detail ?? "检测完成")
                : (result.Msg ?? result.Detail ?? "检测失败");
            // 服务端检测到失效时会先用 stoken 续期再复检，把这一步的结果告诉用户。
            if (!string.IsNullOrWhiteSpace(result.RefreshMsg))
            {
                text += $"\n{result.RefreshMsg}";
            }
            if (result.NeedStoken)
            {
                text += "\nCookie 里没有 stoken：请点「扫码补 stoken」用米游社 App 扫一次";
            }
            var severity = result.Valid switch
            {
                true => InfoBarSeverity.Success,
                false => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Warning
            };
            ShowInfo(text, severity);
            await RefreshStatusAsync(uid);
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    // ---------------- 账号分享 ----------------

    /// <summary>
    /// 分享当前选中的账号：弹窗里勾选要分享给的用户，保存时整组替换。
    /// 再次打开会回显已分享的人，取消勾选保存后对方就看不到这个账号了。
    /// </summary>
    private async void OnShareAccount(object sender, RoutedEventArgs e)
    {
        var account = CurrentAccount;
        if (account is null)
        {
            ShowInfo("先在「账号」里选择一个要分享的账号", InfoBarSeverity.Warning);
            return;
        }
        if (account.Shared)
        {
            ShowInfo("这是别人分享给你的账号，只有分享者可以修改共享设置", InfoBarSeverity.Warning);
            return;
        }

        List<CloudUserDto> users;
        HashSet<int> selected;
        try
        {
            users = await _cloud.ShareUsersAsync();
            selected = (await _cloud.ShareTargetsAsync(account.Game, account.GameUid))
                .Select(u => u.Id).ToHashSet();
        }
        catch (Exception ex)
        {
            ShowInfo($"读取分享列表失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }
        if (users.Count == 0)
        {
            ShowInfo("还没有其他用户可以分享", InfoBarSeverity.Warning);
            return;
        }

        var boxes = new List<CheckBox>();
        var panel = new StackPanel { Spacing = 4 };
        foreach (var user in users)
        {
            var box = new CheckBox
            {
                Content = user.DisplayName,
                IsChecked = selected.Contains(user.Id),
                Tag = user.Id
            };
            boxes.Add(box);
            panel.Children.Add(box);
        }
        var dialog = new ContentDialog
        {
            Title = "分享账号",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var ids = boxes.Where(b => b.IsChecked == true)
            .Select(b => (int)b.Tag)
            .ToList();
        try
        {
            var result = await _cloud.SaveSharesAsync(account.Game, account.GameUid, ids);
            ShowInfo(result.Count > 0
                    ? $"已分享给 {result.Count} 位用户，他们可以在自己的云游戏页面直接上号"
                    : "已取消该账号的全部分享",
                InfoBarSeverity.Success);
            await RefreshStatusAsync(account.GameUid);
        }
        catch (Exception ex)
        {
            ShowInfo($"保存分享失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 米游社扫码补 stoken ----------------

    private async void OnScanStoken(object sender, RoutedEventArgs e)
    {
        StokenButton.IsEnabled = false;
        try
        {
            var created = await _cloud.StokenCreateAsync();
            if (!created.Ok || created.Rows.Count == 0)
            {
                ShowInfo(created.Msg ?? "生成米游社二维码失败", InfoBarSeverity.Error);
                return;
            }

            var status = new TextBlock
            {
                Text = "请用米游社 App 扫描二维码",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var canvas = BuildQrCanvas(created.Rows, Math.Max(1, created.Size));
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = canvas
            });
            panel.Children.Add(status);
            panel.Children.Add(new TextBlock
            {
                Text = "扫码只用来获取长期 stoken，不会上传账号密码；云游戏 combo_token 仍需按原方式抓取。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "扫码补 stoken（米游社）",
                Content = panel,
                CloseButtonText = "关闭"
            };

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => PollStokenAsync(created.Session, status, dialog, cts.Token));
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                cts.Cancel();
            }
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("扫码补 stoken 失败。", ex);
            ShowInfo($"扫码补 stoken 失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            StokenButton.IsEnabled = true;
        }
    }

    /// <summary>2 秒一次查询扫码状态；确认后服务端已入库并续期，直接关掉弹窗刷新列表。</summary>
    private async Task PollStokenAsync(string session, TextBlock status, ContentDialog dialog, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            StokenPollDto result;
            try
            {
                result = await _cloud.StokenPollAsync(session, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => status.Text = $"查询扫码状态失败：{ex.Message}");
                continue;
            }

            DispatcherQueue.TryEnqueue(() => status.Text = result.Msg ?? "请用米游社 App 扫描二维码");
            if (result.State == "confirmed")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    status.Text = result.Msg ?? "扫码成功";
                    dialog.Hide();
                    ShowInfo(result.Msg ?? "扫码成功，stoken 已保存", InfoBarSeverity.Success);
                });
                return;
            }
            if (result.State is "expired" or "canceled" or "error")
            {
                return;
            }
        }
    }

    /// <summary>把服务端给的 0/1 矩阵画成二维码（不引第三方 QR 库）。</summary>
    private static Canvas BuildQrCanvas(IReadOnlyList<string> rows, int size)
    {
        const double side = 320;
        var cell = side / size;
        var canvas = new Canvas
        {
            Width = side,
            Height = side,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        var fill = new SolidColorBrush(Microsoft.UI.Colors.Black);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c] != '1')
                {
                    continue;
                }
                var rect = new Rectangle { Width = cell, Height = cell, Fill = fill };
                Canvas.SetLeft(rect, c * cell);
                Canvas.SetTop(rect, r * cell);
                canvas.Children.Add(rect);
            }
        }
        return canvas;
    }

    // ---------------- 抽卡链接（米游社 stoken -> Auth Key B） ----------------

    private async void OnGachaLink(object sender, RoutedEventArgs e)
    {
        GachaLinkButton.IsEnabled = false;
        try
        {
            var rolesDto = await _cloud.GachaRolesAsync();
            if (!rolesDto.Ok || rolesDto.Roles.Count == 0)
            {
                var error = rolesDto.Detail;
                if (string.IsNullOrWhiteSpace(error) && rolesDto.Errors.Count > 0)
                {
                    error = string.Join("；", rolesDto.Errors.Distinct());
                }
                ShowInfo(error ?? "没有找到绑定的原神/星铁角色，请先用米游社 App 扫码补 stoken，并确认米游社已绑定游戏角色。",
                    InfoBarSeverity.Error);
                return;
            }

            var roles = rolesDto.Roles;
            var picker = new ComboBox
            {
                MinWidth = 460,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = roles.Select(RoleLabel).ToList(),
                SelectedIndex = 0
            };
            var linkBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 90,
                PlaceholderText = "点「生成链接」后显示，复制后粘贴到提瓦特小助手等抽卡分析工具"
            };
            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Text = "链接约 24 小时有效，只用来读取抽卡记录，不要发给陌生人。"
            };
            var generate = new Button
            {
                Content = "生成链接",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            var copy = new Button { Content = "复制链接", IsEnabled = false };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(generate);
            buttons.Children.Add(copy);

            var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
            panel.Children.Add(new TextBlock { Text = "选择游戏账号", FontSize = 12 });
            panel.Children.Add(picker);
            panel.Children.Add(buttons);
            panel.Children.Add(linkBox);
            panel.Children.Add(status);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "获取抽卡链接（米游社）",
                Content = panel,
                CloseButtonText = "关闭"
            };

            generate.Click += async (_, _) =>
            {
                var index = picker.SelectedIndex;
                if (index < 0 || index >= roles.Count)
                {
                    return;
                }
                var role = roles[index];
                generate.IsEnabled = false;
                status.Text = "正在生成…";
                try
                {
                    var result = await _cloud.GachaLinkAsync(role.Game, role.GameUid, role.Region, role.Mid);
                    if (string.IsNullOrWhiteSpace(result.Url))
                    {
                        status.Text = $"生成失败：{result.Detail ?? "请确认该账号已绑定米游社，并重新扫码补 stoken"}";
                        return;
                    }
                    linkBox.Text = result.Url;
                    copy.IsEnabled = true;
                    status.Text = "生成成功，链接约 24 小时有效；复制后粘贴到抽卡分析工具。";
                }
                catch (Exception ex)
                {
                    status.Text = $"生成失败：{ex.Message}";
                }
                finally
                {
                    generate.IsEnabled = true;
                }
            };

            copy.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(linkBox.Text))
                {
                    return;
                }
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(linkBox.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                status.Text = "已复制到剪贴板。";
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("获取抽卡链接失败。", ex);
            ShowInfo($"获取抽卡链接失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            GachaLinkButton.IsEnabled = true;
        }
    }

    private static string RoleLabel(GachaRoleDto role)
    {
        var region = role.RegionName;
        if (!role.IsOfficial)
        {
            region = string.IsNullOrEmpty(region) ? "渠道服" : $"{region}·渠道服";
        }
        var nick = string.IsNullOrWhiteSpace(role.Nickname) ? "未命名" : role.Nickname;
        return $"{role.GameName} · {nick} · UID {role.GameUid} · {region}";
    }

    // ---------------- Cookie 过期微信提醒（PushPlus 扫码绑定） ----------------

    private async Task RefreshExpiryPushAsync()
    {
        try
        {
            ApplyExpiryPushStatus(await _cloud.ExpiryPushStatusAsync());
        }
        catch (Exception ex)
        {
            AppLog.Debug($"查询过期提醒绑定状态失败：{ex.Message}");
        }
    }

    private void ApplyExpiryPushStatus(ExpiryPushStatusDto st)
    {
        _expiryBound = st.Bound;
        _suppressExpiryToggle = true;
        ExpiryPushSwitch.IsOn = st.Bound && st.Enabled;
        ExpiryPushSwitch.IsEnabled = st.Configured;
        _suppressExpiryToggle = false;

        ExpiryBindButton.IsEnabled = st.Configured;
        ExpiryBindButton.Content = st.Bound ? "重新绑定" : "扫码绑定";
        ExpiryTestButton.Visibility = st.Bound ? Visibility.Visible : Visibility.Collapsed;
        ExpiryUnbindButton.Visibility = st.Bound ? Visibility.Visible : Visibility.Collapsed;

        ExpiryPushText.Text = !st.Configured
            ? "管理员尚未配置 PushPlus，暂时无法绑定微信提醒。"
            : st.Bound
                ? $"已绑定{(string.IsNullOrWhiteSpace(st.Nickname) ? "" : $"（{st.Nickname}）")}：每日检测到 Cookie 失效会推送微信提醒。"
                : "微信扫码绑定后，每日检测到 Cookie 失效会推送提醒。";
    }

    /// <summary>未绑定时打开扫码流程；已绑定时只切换开关，不改绑定关系。</summary>
    private async void OnExpiryPushToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressExpiryToggle)
        {
            return;
        }

        if (!_expiryBound)
        {
            _suppressExpiryToggle = true;
            ExpiryPushSwitch.IsOn = false;
            _suppressExpiryToggle = false;
            await StartExpiryPushBindingAsync();
            return;
        }

        try
        {
            var st = await _cloud.ExpiryPushEnabledAsync(ExpiryPushSwitch.IsOn);
            if (st is { Ok: true })
            {
                ApplyExpiryPushStatus(st);
                ShowInfo(ExpiryPushSwitch.IsOn ? "已开启 Cookie 过期微信提醒" : "已关闭 Cookie 过期微信提醒",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"保存提醒开关失败：{ex.Message}", InfoBarSeverity.Error);
            await RefreshExpiryPushAsync();
        }
    }

    private async void OnExpiryBindClick(object sender, RoutedEventArgs e) => await StartExpiryPushBindingAsync();

    private async void OnExpiryTestClick(object sender, RoutedEventArgs e)
    {
        ExpiryTestButton.IsEnabled = false;
        try
        {
            var result = await _cloud.ExpiryPushTestAsync();
            ShowInfo(result.Ok ? "测试推送已发送，请查看微信" : (result.Msg ?? "测试推送失败"),
                result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowInfo($"测试推送失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ExpiryTestButton.IsEnabled = true;
        }
    }

    private async void OnExpiryUnbindClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var st = await _cloud.ExpiryPushUnbindAsync();
            if (st is not null)
            {
                ApplyExpiryPushStatus(st);
            }
            ShowInfo("已解除微信提醒绑定", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"解绑失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>生成一次性个人二维码，弹窗展示并轮询绑定结果（微信扫码，无需填 token）。</summary>
    private async Task StartExpiryPushBindingAsync()
    {
        ExpiryBindButton.IsEnabled = false;
        try
        {
            var qr = await _cloud.ExpiryPushQrAsync();
            if (!qr.Ok || string.IsNullOrWhiteSpace(qr.Url))
            {
                ShowInfo(qr.Detail ?? "生成二维码失败：请确认管理员已配置 PushPlus", InfoBarSeverity.Warning);
                return;
            }

            var image = new Image
            {
                Width = 300,
                Height = 300,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(qr.Url!))
            };
            var status = new TextBlock
            {
                Text = "请用微信扫码，扫码成功后会自动绑定",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var tip = new TextBlock
            {
                Text = "二维码 10 分钟内有效，仅能扫描一次。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
            var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(image);
            panel.Children.Add(status);
            panel.Children.Add(tip);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "微信扫码绑定过期提醒",
                Content = panel,
                CloseButtonText = "关闭"
            };

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => PollExpiryPushBindingAsync(status, dialog, cts.Token));
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                cts.Cancel();
            }
            await RefreshExpiryPushAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("生成微信提醒二维码失败。", ex);
            ShowInfo($"生成二维码失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ExpiryBindButton.IsEnabled = true;
        }
    }

    /// <summary>2 秒一次查询绑定状态；pushplus 回调命中的那一刻关掉弹窗并刷新开关。</summary>
    private async Task PollExpiryPushBindingAsync(TextBlock status, ContentDialog dialog, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ExpiryPushStatusDto st;
            try
            {
                st = await _cloud.ExpiryPushStatusAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => status.Text = $"查询绑定状态失败：{ex.Message}");
                continue;
            }

            if (!st.Bound)
            {
                continue;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                status.Text = "绑定成功";
                dialog.Hide();
                ShowInfo("微信提醒绑定成功，检测到 Cookie 失效会推送通知", InfoBarSeverity.Success);
                _ = RefreshExpiryPushAsync();
            });
            return;
        }
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