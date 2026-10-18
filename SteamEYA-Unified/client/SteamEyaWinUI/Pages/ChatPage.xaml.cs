using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 聊天室页面。默认内嵌「网页版」聊天界面（WebView2 指向服务端 /api/autologin 免二次登录，
/// 与浏览器里完全一致：表情包 / @提及 / 头像 / 搜索 / 个人中心一应俱全）；若 WebView2 运行时不
/// 可用或加载失败，则自动回退到原生 WinUI 界面（Win10 上 WebView2 初始化卡死时的兜底）。
/// 原生界面本身也补齐了表情包、@提及、头像与回车发送。
/// </summary>
public sealed partial class ChatPage : Page, INotifyPropertyChanged
{
    private static readonly Brush MyBubble = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private static readonly Brush OtherBubble = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
    private static readonly Brush OnAccentBrush = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
    private static readonly Brush PrimaryTextBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    private static readonly Brush TertiaryTextBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    /// <summary>头像底色的确定性调色板（按用户 id 取模，保证同一人颜色稳定）。</summary>
    private static readonly string[] AvatarColors =
        ["#5865F2", "#EB459E", "#57F287", "#FEE75C", "#ED4245", "#00B0F4", "#9B59B6", "#E67E22"];

    /// <summary>下载聊天附件用（文件可能较大，超时放宽）。</summary>
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>内嵌聊天室不可用时的兜底：用默认浏览器打开网页版聊天室（用户自行登录）。</summary>
    private const string ExternalChatUrl = "https://chat.teatt.top";

    // 复用全局常驻连接（ChatHub），离开本页也不断线，保证其它页面能收消息并提醒。
    private readonly ChatService _chat = ChatHub.Chat;
    // 同时 using 了 Microsoft.UI.Dispatching 与 Windows.System，两者都有 DispatcherQueue，必须写全名消歧义。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<ChatMessageVm> _messages = [];
    private readonly ObservableCollection<ChatUserVm> _users = [];
    private readonly ObservableCollection<StickerVm> _stickers = [];

    private readonly List<ChatMessageDto> _rawMessages = [];
    private ChatUserDto[] _rawUsers = [];
    private Dictionary<string, int> _unread = [];
    private int _myId;
    private string _room = "group";
    private bool _initialized;

    // 网页版 / 原生版
    private bool _webMode;

    // @提及：显示名 -> 用户 id（-1 表示“所有人”）
    private readonly Dictionary<string, int> _mentionPicks = [];
    private readonly StackPanel _mentionPanel = new() { Spacing = 2, MinWidth = 200 };
    private Flyout? _mentionFlyout;

    public ChatPage()
    {
        InitializeComponent();

        MessagesList.ItemsSource = _messages;
        UsersList.ItemsSource = _users;
        StickerGrid.ItemsSource = _stickers;

        _chat.MessageReceived += OnMessageReceived;
        _chat.MessageRecalled += OnMessageRecalled;
        _chat.UsersChanged += OnUsersChanged;
        _chat.ConnectionChanged += OnConnectionChanged;
        ChatHub.RoomOpenRequested += OnRoomOpenRequested;

        Loc.LanguageChanged += OnLanguageChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    // ---------------- 生命周期 ----------------
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        // 标记“正在看聊天室”，用于抑制本页可见时的系统通知。
        ChatHub.IsChatPageOpen = true;

        ApplyStaticText();

        try
        {
            var me = await _chat.GetMeAsync();
            _myId = me?.Id ?? 0;
            ChatHub.SetMyId(_myId);
            _rawUsers = await _chat.GetUsersAsync();
            RebuildUsers();
            // 点击系统通知进入时可能指定了房间；否则默认进群聊。
            var initialRoom = ChatHub.PendingRoom ?? "group";
            ChatHub.PendingRoom = null;
            await OpenRoomAsync(initialRoom);
            ChatHub.EnsureStarted();
            await RefreshUnreadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("聊天室初始化失败。", ex);
        }

        await InitializeModeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _mentionFlyout?.Hide();
        ChatHub.IsChatPageOpen = false;

        // 连接由 ChatHub 常驻保持（离开本页仍收消息 + 弹系统提醒），这里只退订本页的 UI 回调。
        _chat.MessageReceived -= OnMessageReceived;
        _chat.MessageRecalled -= OnMessageRecalled;
        _chat.UsersChanged -= OnUsersChanged;
        _chat.ConnectionChanged -= OnConnectionChanged;
        ChatHub.RoomOpenRequested -= OnRoomOpenRequested;
    }

    /// <summary>点击系统通知「打开该房间」：本页已打开时即时切房（未打开时由 OnLoaded 消费 PendingRoom）。</summary>
    private void OnRoomOpenRequested(string room)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (_rawUsers.Length == 0)
                {
                    _rawUsers = await _chat.GetUsersAsync();
                    RebuildUsers();
                }
                ChatHub.PendingRoom = null;
                await OpenRoomAsync(room);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"打开通知对应房间失败：{ex.Message}");
            }
        });
    }

    private void OnLanguageChanged()
    {
        _dispatcher.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            ApplyStaticText();
            RebuildUsers();
            RebuildMessages();
        });
    }

    private void ApplyStaticText()
    {
        HeaderText.Text = Loc.T("Nav_Chat");
        GroupText.Text = Loc.T("Chat_Group");
        ModeButton.Content = Loc.T(_webMode ? "Chat_Mode_ToNative" : "Chat_Mode_ToWeb");
        if (_room == "group")
        {
            RoomTitle.Text = Loc.T("Chat_Group");
        }
        SetConnectionText(_chat.IsConnected);
    }

    // ---------------- 网页版 / 原生版 ----------------
    /// <summary>读取设置的运行模式（Auto / Web / Native）；未知取值一律当 Auto。</summary>
    private static string ChatModeSetting() => AppState.SettingsService.Load().ChatMode switch
    {
        "Web" => "Web",
        "Native" => "Native",
        _ => "Auto"
    };

    /// <summary>
    /// 按设置的运行模式选择界面：Auto（优先完整版，加载失败回退原生）/ Web（完整版）/ Native（原生版）。
    /// 离线（局域网）无统一账号，网页版登录态无从取得，一律用原生。
    /// 内嵌网页版在部分 Win10 上会初始化卡死，此时可在设置里强制「原生版」避开 WebView2。
    /// </summary>
    private async Task InitializeModeAsync()
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            SwitchMode(false);
            ModeButton.IsEnabled = false;
            return;
        }

        if (ChatModeSetting() == "Native")
        {
            SwitchMode(false);
            return;
        }

        if (await TryStartWebAsync())
        {
            SwitchMode(true);
            return;
        }

        // 完整版加载失败：回退原生，并提示可用右上角「在浏览器打开」。
        SwitchMode(false);
        MainWindow.Instance?.ShowStatus(Loc.T("Chat_WebFailed"), InfoBarSeverity.Warning);
    }

    /// <summary>把用户在聊天页的切换结果写回设置，下次打开沿用。</summary>
    private static void PersistChatMode(string mode)
    {
        var settings = AppState.SettingsService.Load();
        if (settings.ChatMode == mode)
        {
            return;
        }
        settings.ChatMode = mode;
        AppState.SettingsService.Save(settings);
    }

    private void SwitchMode(bool web)
    {
        _webMode = web;
        WebRoot.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        NativeRoot.Visibility = web ? Visibility.Collapsed : Visibility.Visible;
        ModeButton.Content = Loc.T(web ? "Chat_Mode_ToNative" : "Chat_Mode_ToWeb");

        if (!web)
        {
            // 原生列表此刻才变为可见。首次加载时它是 Collapsed，早先那次滚动必然落空，这里补一次。
            ScrollToBottom();
        }
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_webMode)
        {
            SwitchMode(false);
            PersistChatMode("Native");
            return;
        }

        ModeButton.IsEnabled = false;
        var ok = await TryStartWebAsync();
        ModeButton.IsEnabled = true;
        if (ok)
        {
            SwitchMode(true);
            PersistChatMode("Web");
        }
        else
        {
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_WebFailed"), InfoBarSeverity.Warning);
        }
    }

    /// <summary>内嵌界面（网页版/原生版）都打不开时的兜底：用默认浏览器打开网页版聊天室。</summary>
    private async void Browser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(ExternalChatUrl));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开默认浏览器失败：{ex.Message}");
            MainWindow.Instance?.ShowStatus(Loc.T("AppState_Url_OpenFailed"), InfoBarSeverity.Warning);
        }
    }

    /// <summary>初始化 WebView2 并导航到免登录网页版；任一步失败/超时都返回 false（不改动界面）。</summary>
    private async Task<bool> TryStartWebAsync()
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return false;
        }

        try
        {
            WebLoading.IsActive = true;

            // 用户数据目录放到用户 AppData：安装目录（如 Program Files）通常不可写，默认目录会导致初始化失败。
            // WinUI 3 既没有 WPF/WinForms 的 CreationProperties，其 CoreWebView2Environment.CreateAsync
            // 签名也与桌面版不同；这里用官方支持的环境变量 WEBVIEW2_USER_DATA_FOLDER 指定，再走无参初始化。
            try
            {
                var dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamEYA", "webview2");
                Directory.CreateDirectory(dataFolder);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dataFolder);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"设置 WebView2 数据目录失败：{ex.Message}");
            }

            // Win10 上 WebView2 初始化偶发卡死，加超时兜底，超时即回退原生。
            var ensure = WebChat.EnsureCoreWebView2Async().AsTask();
            if (await Task.WhenAny(ensure, Task.Delay(TimeSpan.FromSeconds(10))) != ensure)
            {
                AppLog.Warn("网页版聊天初始化超时，改用原生界面。");
                return false;
            }
            await ensure;

            if (WebChat.CoreWebView2 is { } core)
            {
                core.Settings.IsStatusBarEnabled = false;
            }

            var tcs = new TaskCompletionSource<bool>();
            void OnNav(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => tcs.TrySetResult(args.IsSuccess);

            WebChat.NavigationCompleted += OnNav;
            try
            {
                WebChat.Source = new Uri(AutologinUrl());
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                return done == tcs.Task && tcs.Task.Result;
            }
            finally
            {
                WebChat.NavigationCompleted -= OnNav;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"网页版聊天不可用，改用原生界面：{ex.Message}");
            return false;
        }
        finally
        {
            WebLoading.IsActive = false;
        }
    }

    /// <summary>服务端用统一 JWT 换聊天 cookie 并跳转网页版聊天界面，客户端免二次登录。</summary>
    private static string AutologinUrl()
        => SessionState.BaseUrl.TrimEnd('/') + "/api/autologin?jwt=" + Uri.EscapeDataString(SessionState.Token ?? string.Empty);

    // ---------------- 会话切换 ----------------
    private async Task OpenRoomAsync(string room)
    {
        _room = room;
        ChatHub.CurrentRoom = room;   // 供 ChatHub 判断“正在看这个房间”，避免多余提醒
        _mentionPicks.Clear();
        _mentionFlyout?.Hide();
        RoomTitle.Text = room == "group"
            ? Loc.T("Chat_Group")
            : _rawUsers.FirstOrDefault(u => PmRoom(u.Id) == room)?.DisplayName ?? Loc.T("Chat_Private");

        _messages.Clear();
        _rawMessages.Clear();
        var history = await _chat.GetHistoryAsync(room);
        foreach (var dto in history.Messages ?? [])
        {
            _rawMessages.Add(dto);
            _messages.Add(BuildMessage(dto));
        }
        ScrollToBottom();

        await SafeMarkReadAsync(room);
        await RefreshUnreadAsync();
    }

    private async void Group_Click(object sender, RoutedEventArgs e) => await OpenRoomAsync("group");

    private async void UsersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsersList.SelectedItem is ChatUserVm vm && vm.Id != _myId)
        {
            await OpenRoomAsync(PmRoom(vm.Id));
        }
    }

    private string PmRoom(int otherId)
    {
        var a = Math.Min(_myId, otherId);
        var b = Math.Max(_myId, otherId);
        return $"pm:{a}:{b}";
    }

    // ---------------- 发送 / 上传 ----------------
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendTextAsync();

    /// <summary>
    /// 回车发送、Shift+回车换行。这里必须用 PreviewKeyDown：AcceptsReturn=true 的 TextBox
    /// 会在类处理阶段把 Enter 先标记为已处理（用来插入换行），普通 KeyDown 事件收不到，
    /// 表现就是「回车没反应」。PreviewKeyDown 走隧道阶段，早于控件自身处理。
    /// </summary>
    private async void InputBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            e.Handled = true;
            await SendTextAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            HideMention();
        }
    }

    private static bool IsShiftDown() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
         & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private async Task SendTextAsync()
    {
        var text = InputBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }
        InputBox.Text = string.Empty;
        await _chat.SendChatAsync("text", text, to: TargetId(), mentions: BuildMentions(text));
        _mentionPicks.Clear();
        HideMention();
    }

    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickAsync([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"]);
        if (file is not null)
        {
            await SendFileAsync(file);
        }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickAsync(["*"]);
        if (file is not null)
        {
            await SendFileAsync(file);
        }
    }

    private static async Task<StorageFile?> PickAsync(string[] filters)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
        foreach (var f in filters)
        {
            picker.FileTypeFilter.Add(f);
        }
        return await picker.PickSingleFileAsync();
    }

    private async Task SendFileAsync(StorageFile file)
    {
        try
        {
            var uploaded = await _chat.UploadAsync(file.Path);
            if (!uploaded.Ok || string.IsNullOrEmpty(uploaded.Url))
            {
                return;
            }
            var kind = uploaded.IsImage ? "image" : (uploaded.IsVideo ? "video" : "file");
            var payload = new ChatFileDto { Url = uploaded.Url, Name = uploaded.Name, Size = uploaded.Size };
            await _chat.SendChatAsync(kind, string.Empty, payload, TargetId());
        }
        catch (Exception ex)
        {
            AppLog.Error("发送文件失败。", ex);
        }
    }

    private int? TargetId() => _room.StartsWith("pm:", StringComparison.Ordinal) ? OtherIdFromRoom(_room) : null;

    private int? OtherIdFromRoom(string room)
    {
        var parts = room.Split(':');
        if (parts.Length != 3)
        {
            return null;
        }
        var a = int.TryParse(parts[1], out var x) ? x : 0;
        var b = int.TryParse(parts[2], out var y) ? y : 0;
        return a == _myId ? b : a;
    }

    // ---------------- 表情包 ----------------
    private async void StickerFlyout_Opening(object sender, object e) => await LoadStickersAsync();

    private async Task LoadStickersAsync()
    {
        try
        {
            var result = await _chat.GetStickersAsync();
            _stickers.Clear();
            foreach (var s in result.Stickers ?? [])
            {
                if (string.IsNullOrEmpty(s.Url))
                {
                    continue;
                }
                _stickers.Add(new StickerVm(s.Id, new BitmapImage(new Uri(Absolute(s.Url)))));
            }
            StickerEmptyText.Text = Loc.T("Chat_Sticker_Empty");
            StickerEmptyText.Visibility = _stickers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Error("加载表情包失败。", ex);
        }
    }

    private async void StickerGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StickerVm vm)
        {
            StickerButton.Flyout?.Hide();
            await _chat.SendStickerAsync(vm.Id, TargetId());
        }
    }

    private async void UploadSticker_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickAsync([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"]);
        if (file is null)
        {
            return;
        }
        try
        {
            var result = await _chat.UploadStickerAsync(file.Path);
            if (result.Ok)
            {
                await LoadStickersAsync();
            }
            else
            {
                MainWindow.Instance?.ShowStatus(result.Error ?? Loc.T("Chat_Sticker_Empty"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("上传表情包失败。", ex);
        }
    }

    // ---------------- @提及 ----------------
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_room != "group")
        {
            HideMention();
            return;
        }

        var text = InputBox.Text ?? string.Empty;
        var at = text.LastIndexOf('@');
        if (at < 0 || at < text.Length - 21)
        {
            HideMention();
            return;
        }

        var tail = text[(at + 1)..];
        if (tail.IndexOfAny([' ', '\n', '\r', '\t']) >= 0)
        {
            HideMention();
            return;
        }

        ShowMention(tail);
    }

    private void ShowMention(string query)
    {
        _mentionPanel.Children.Clear();
        var q = query.ToLowerInvariant();

        if (q.Length == 0 || "所有人".Contains(q, StringComparison.Ordinal))
        {
            _mentionPanel.Children.Add(MentionRow(Loc.T("Chat_Mention_All"), -1));
        }

        var matches = _rawUsers
            .Where(u => u.Id != _myId)
            .Select(u => (User: u, Name: DisplayNameOf(u)))
            .Where(t => q.Length == 0 || t.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal))
            .OrderByDescending(t => t.User.Online)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(30);

        foreach (var (user, name) in matches)
        {
            _mentionPanel.Children.Add(MentionRow(name, user.Id));
        }

        if (_mentionPanel.Children.Count == 0)
        {
            _mentionPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Chat_Mention_None"),
                Margin = new Thickness(10, 8, 10, 8),
                Foreground = TertiaryTextBrush
            });
        }

        _mentionFlyout ??= new Flyout { Placement = FlyoutPlacementMode.Top, Content = _mentionPanel };
        if (!_mentionFlyout.IsOpen)
        {
            _mentionFlyout.ShowAt(InputBox);
        }
    }

    private Button MentionRow(string name, int id)
    {
        var button = new Button
        {
            Content = name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => InsertMention(name, id);
        return button;
    }

    private void InsertMention(string name, int id)
    {
        var text = InputBox.Text ?? string.Empty;
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            return;
        }

        var head = text[..at];
        var insert = "@" + name + " ";
        InputBox.Text = head + insert;
        InputBox.SelectionStart = InputBox.Text.Length;
        _mentionPicks[name] = id;
        HideMention();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void HideMention() => _mentionFlyout?.Hide();

    /// <summary>按「文本里仍然存在的 @对象」生成 mentions 数组（id 或 all），与网页版一致。</summary>
    private string[]? BuildMentions(string text)
    {
        if (_room != "group" || _mentionPicks.Count == 0)
        {
            return null;
        }
        var ids = new List<string>();
        foreach (var (name, id) in _mentionPicks)
        {
            if (text.Contains("@" + name, StringComparison.Ordinal))
            {
                ids.Add(id < 0 ? "all" : id.ToString());
            }
        }
        return ids.Count > 0 ? [.. ids] : null;
    }

    private static string DisplayNameOf(ChatUserDto user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;

    // ---------------- 撤回 / 附件 ----------------
    private async void Recall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageVm vm)
        {
            await _chat.RecallAsync(vm.Id);
        }
    }

    /// <summary>附件按钮：视频在应用内直接播放，其它文件走「另存为」下载。</summary>
    private async void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessageVm vm
            || string.IsNullOrEmpty(vm.FileUrl))
        {
            return;
        }
        if (vm.Kind == "video")
        {
            await PlayVideoAsync(vm);
        }
        else
        {
            await SaveFileAsync(vm);
        }
    }

    /// <summary>弹出播放器直接播放视频（离线由本机网关提供，在线由服务器提供）。</summary>
    private async Task PlayVideoAsync(ChatMessageVm vm)
    {
        var player = new MediaPlayerElement
        {
            Source = MediaSource.CreateFromUri(new Uri(vm.FileUrl)),
            AutoPlay = true,
            AreTransportControlsEnabled = true,
            MaxWidth = 720,
            MaxHeight = 405
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = string.IsNullOrWhiteSpace(vm.FileName) ? "视频" : vm.FileName,
            Content = player,
            CloseButtonText = Loc.T("Chat_Cancel")
        };
        dialog.Closed += (_, _) => player.Source = null;   // 关掉即释放解码器
        await dialog.ShowAsync();
    }

    /// <summary>把消息中的文件下载到用户选择的位置。</summary>
    private async Task SaveFileAsync(ChatMessageVm vm)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(vm.FileName) ? "download" : vm.FileName;
            var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            var picker = new FileSavePicker { SuggestedFileName = safeName };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

            var ext = Path.GetExtension(safeName);
            picker.FileTypeChoices.Add("文件", [string.IsNullOrEmpty(ext) ? ".bin" : ext]);

            var target = await picker.PickSaveFileAsync();
            if (target is null)
            {
                return;
            }
            var bytes = await DownloadClient.GetByteArrayAsync(vm.FileUrl);
            await FileIO.WriteBytesAsync(target, bytes);
            MainWindow.Instance?.ShowStatus($"已保存到：{target.Path}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.ShowStatus($"下载失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------------- 实时事件 ----------------
    private void OnMessageReceived(ChatMessageDto dto)
    {
        _dispatcher.TryEnqueue(() =>
        {
            // 系统通知由 ChatHub 统一负责（它不区分当前页面），这里只做本页 UI 更新。
            if (dto.Room != _room)
            {
                if (dto.FromId != _myId)
                {
                    _unread[dto.Room] = _unread.GetValueOrDefault(dto.Room) + 1;
                    RebuildUsers();
                }
                return;
            }
            _rawMessages.Add(dto);
            _messages.Add(BuildMessage(dto));
            ScrollToBottom();
            _ = SafeMarkReadAsync(_room);
        });
    }

    private void OnMessageRecalled(long id)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var dto = _rawMessages.FirstOrDefault(m => m.Id == id);
            if (dto is null)
            {
                return;
            }
            dto.Recalled = true;
            var index = _rawMessages.IndexOf(dto);
            if (index >= 0)
            {
                _messages[index] = BuildMessage(dto);
            }
        });
    }

    private async void OnUsersChanged()
    {
        try
        {
            var users = await _chat.GetUsersAsync();
            _dispatcher.TryEnqueue(() =>
            {
                _rawUsers = users;
                RebuildUsers();
            });
        }
        catch
        {
            // 刷新失败不打断
        }
        await RefreshUnreadAsync();
    }

    private void OnConnectionChanged(bool connected)
    {
        _dispatcher.TryEnqueue(() =>
        {
            SetConnectionText(connected);
            if (connected)
            {
                _ = RefreshUnreadAsync();
            }
        });
    }

    private void SetConnectionText(bool connected)
    {
        ConnText.Text = Loc.T(connected ? "Chat_Connected" : "Chat_Connecting");
        ConnDot.Fill = (Brush)Application.Current.Resources[connected
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorCautionBrush"];
    }

    // ---------------- 未读 ----------------
    private async Task SafeMarkReadAsync(string room)
    {
        try
        {
            await _chat.MarkReadAsync(room);
        }
        catch
        {
            // 忽略
        }
    }

    private async Task RefreshUnreadAsync()
    {
        try
        {
            var result = await _chat.GetUnreadAsync();
            _unread = result.Unread ?? [];
        }
        catch
        {
            // 忽略
        }
        _dispatcher.TryEnqueue(RebuildUsers);
    }

    // ---------------- 渲染 ----------------
    private void RebuildUsers()
    {
        _users.Clear();
        foreach (var u in _rawUsers.OrderByDescending(u => u.Online).ThenBy(u => DisplayNameOf(u)))
        {
            if (u.Id == _myId)
            {
                continue;
            }
            var room = PmRoom(u.Id);
            _users.Add(new ChatUserVm(u, room, _unread.GetValueOrDefault(room)));
        }

        var groupCount = _unread.GetValueOrDefault("group");
        GroupBadge.Visibility = groupCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupBadgeText.Text = groupCount.ToString();
    }

    private void RebuildMessages()
    {
        for (var i = 0; i < _rawMessages.Count && i < _messages.Count; i++)
        {
            _messages[i] = BuildMessage(_rawMessages[i]);
        }
    }

    private ChatMessageVm BuildMessage(ChatMessageDto dto)
    {
        var isMine = dto.FromId == _myId;
        var kind = dto.EffectiveKind;
        var absolute = Absolute(dto.FilePath);
        var sizeText = FormatSize(dto.FileSize ?? 0);
        var displayName = string.IsNullOrWhiteSpace(dto.FromName) ? "?" : dto.FromName;

        ImageSource? image = null;
        if (!dto.Recalled && (kind == "image" || kind == "sticker") && !string.IsNullOrEmpty(absolute))
        {
            image = new BitmapImage(new Uri(absolute));
        }

        return new ChatMessageVm
        {
            Id = dto.Id,
            Kind = kind,
            Content = dto.Content,
            FileUrl = absolute,
            FileName = dto.FileName ?? string.Empty,
            FileSizeText = sizeText,
            Image = image,
            Recalled = dto.Recalled,
            CanRecall = isMine && !dto.Recalled && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - dto.CreatedAt <= 120000,
            MetaText = isMine
                ? FormatTime(dto.CreatedAt)
                : $"{displayName} · {FormatTime(dto.CreatedAt)}",
            RowAlign = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = isMine ? MyBubble : OtherBubble,
            ContentForeground = isMine ? OnAccentBrush : PrimaryTextBrush,
            RecallLabel = Loc.T("Chat_Recall"),
            RecalledLabel = Loc.T("Chat_Recalled"),
            AvatarText = displayName[..1].ToUpperInvariant(),
            AvatarImage = string.IsNullOrWhiteSpace(dto.FromAvatar) ? null : new BitmapImage(new Uri(Absolute(dto.FromAvatar))),
            AvatarBrush = AvatarBrushFor(dto.FromId),
            AvatarLeftVisibility = isMine ? Visibility.Collapsed : Visibility.Visible,
            AvatarRightVisibility = isMine ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private static Brush AvatarBrushFor(int userId)
    {
        var hex = AvatarColors[Math.Abs(userId) % AvatarColors.Length];
        var color = Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
        return new SolidColorBrush(color);
    }

    private static string Absolute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return SessionState.BaseUrl.TrimEnd('/') + path;
    }

    private static string FormatTime(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var now = DateTimeOffset.Now;
        return t.Date == now.Date ? t.ToString("HH:mm") : t.ToString("MM-dd HH:mm");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.#}{units[i]}";
    }

    /// <summary>
    /// 滚动到最新一条消息。刚插入条目时 ListView 的容器还没生成，同步 ScrollIntoView 会落空，
    /// 所以延后到下一轮、先 UpdateLayout 完成布局，再直接把内部 ScrollViewer 滚到底
    /// （ChangeView 不依赖容器是否已生成，比 ScrollIntoView 稳）；后者作为兜底。
    /// </summary>
    private void ScrollToBottom()
    {
        if (_messages.Count == 0)
        {
            return;
        }
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_messages.Count == 0)
            {
                return;
            }
            MessagesList.UpdateLayout();
            if (FindScrollViewer(MessagesList) is { } viewer)
            {
                viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
            }
            MessagesList.ScrollIntoView(_messages[^1], ScrollIntoViewAlignment.Default);
        });
    }

    /// <summary>在可视树里找 ListView 内部的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}

/// <summary>消息列表项视图模型（只读，供 x:Bind OneTime 使用）。</summary>
public sealed class ChatMessageVm
{
    public long Id { get; set; }
    public string Kind { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileSizeText { get; set; } = string.Empty;
    public ImageSource? Image { get; set; }
    public bool Recalled { get; set; }
    public bool CanRecall { get; set; }
    public string MetaText { get; set; } = string.Empty;
    public HorizontalAlignment RowAlign { get; set; }
    public Brush BubbleBrush { get; set; } = null!;
    public Brush ContentForeground { get; set; } = null!;
    public string RecallLabel { get; set; } = string.Empty;
    public string RecalledLabel { get; set; } = string.Empty;

    // 头像
    public string AvatarText { get; set; } = string.Empty;
    public ImageSource? AvatarImage { get; set; }
    public Brush AvatarBrush { get; set; } = null!;
    public Visibility AvatarLeftVisibility { get; set; }
    public Visibility AvatarRightVisibility { get; set; }

    public Visibility TextVisibility =>
        Recalled || Kind == "text" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageVisibility =>
        !Recalled && Image is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>图片显示上限：表情包与网页端一致（130），普通图片消息保持 360。</summary>
    public double ImageMaxSize => Kind == "sticker" ? 130 : 360;

    public Visibility FileVisibility =>
        !Recalled && (Kind is "file" or "video") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RecallVisibility =>
        CanRecall && !Recalled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>文本内容：撤回时显示「已撤回」占位。</summary>
    public string DisplayContent => Recalled ? RecalledLabel : Content;

    public string FileLabel
    {
        get
        {
            var icon = Kind == "video" ? "▶ " : string.Empty;
            return string.IsNullOrEmpty(FileSizeText)
                ? $"{icon}{FileName}"
                : $"{icon}{FileName} ({FileSizeText})";
        }
    }
}

/// <summary>表情包列表项视图模型。</summary>
public sealed class StickerVm
{
    internal StickerVm(long id, ImageSource image)
    {
        Id = id;
        Image = image;
    }

    public long Id { get; }

    public ImageSource Image { get; }
}

/// <summary>左侧用户列表项视图模型。</summary>
public sealed class ChatUserVm
{
    private readonly ChatUserDto _user;
    private readonly int _unread;

    internal ChatUserVm(ChatUserDto user, string room, int unread)
    {
        _user = user;
        Room = room;
        _unread = unread;
    }

    public int Id => _user.Id;

    public string Room { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(_user.DisplayName) ? _user.Username : _user.DisplayName;

    public string AvatarText => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    public ImageSource? Avatar =>
        string.IsNullOrWhiteSpace(_user.Avatar) ? null : new BitmapImage(new Uri(SessionState.BaseUrl.TrimEnd('/') + _user.Avatar));

    public string OnlineText => _user.Online ? Loc.T("Chat_Online") : string.Empty;

    public string LastSeenText => _user.Online ? string.Empty : ChatPageFormatting.Ago(_user.LastSeen);

    public string BadgeText => _unread > 0 ? _unread.ToString() : string.Empty;

    public Visibility BadgeVisibility => _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>供视图模型复用的时间格式化（与页面逻辑一致）。</summary>
internal static class ChatPageFormatting
{
    public static string Ago(long ms)
    {
        if (ms <= 0)
        {
            return Loc.T("Chat_NeverLoggedIn");
        }
        var delta = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms;
        if (delta < 60000)
        {
            return Loc.T("Chat_JustNow");
        }
        if (delta < 3600000)
        {
            return Loc.Tf("Chat_MinutesAgo", delta / 60000);
        }
        if (delta < 86400000)
        {
            return Loc.Tf("Chat_HoursAgo", delta / 3600000);
        }
        if (delta < 7 * 86400000L)
        {
            return Loc.Tf("Chat_DaysAgo", delta / 86400000);
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd");
    }
}
