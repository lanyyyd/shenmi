using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 聊天室页面。默认内嵌网页版（WebView2 指向服务端 /api/autologin 免二次登录）；WebView2 不可用或
/// 加载失败时回退到原生界面。原生界面按网页版源码重做：浅/深色配色、侧栏分组与搜索、等级/AI 徽章、
/// 未读角标、日期分隔、已读人数、@高亮、撤回三态、图片灯箱、历史分页、粘贴上传与上传进度，
/// 并补齐搜索、个人资料、联系人备注/置顶、表情排序删除。
/// </summary>
public sealed partial class ChatPage : Page, INotifyPropertyChanged
{
    /// <summary>头像底色的确定性调色板（按用户 id 取模，保证同一人颜色稳定）。</summary>
    private static readonly string[] AvatarColors =
        ["#5865F2", "#EB459E", "#57F287", "#F59E0B", "#ED4245", "#00B0F4", "#9B59B6", "#E67E22"];

    /// <summary>等级徽章底色（0–6，与网页块级一致）。</summary>
    private static readonly string[] LevelColors =
        ["#64748B", "#10B981", "#06B6D4", "#3B82F6", "#8B5CF6", "#F59E0B", "#EF4444"];

    /// <summary>下载聊天附件用（文件可能较大，超时放宽）。</summary>
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>内嵌聊天室不可用时的兜底：用默认浏览器打开网页版聊天室（用户自行登录）。</summary>
    private const string ExternalChatUrl = "https://chat.teatt.top";

    /// <summary>附件上限（与网页版一致）。</summary>
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    private const int PageSize = 80;

    private static readonly Regex MentionRegex = new("@[^\\s@]{1,24}", RegexOptions.Compiled);

    // 复用全局常驻连接（ChatHub），离开本页也不断线，保证其它页面能收消息并提醒。
    private readonly ChatService _chat = ChatHub.Chat;
    // 同时 using 了 Microsoft.UI.Dispatching 与 Windows.System，两者都有 DispatcherQueue，必须写全名消歧义。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<ChatMessageVm> _messages = [];
    private readonly ObservableCollection<SidebarItemVm> _users = [];
    private readonly ObservableCollection<StickerVm> _stickers = [];
    private readonly ObservableCollection<SearchResultVm> _searchResults = [];

    private readonly List<ChatMessageDto> _rawMessages = [];
    private ChatUserDto[] _rawUsers = [];
    private readonly Dictionary<int, (string Username, long LastReadId)> _readState = [];
    private readonly Dictionary<string, int> _unread = [];
    private readonly Dictionary<int, string> _notes = [];
    private readonly List<int> _pinned = [];
    private int _aiId;
    private string _aiName = "小火人";
    private string _aiAvatar = string.Empty;

    private int _myId;
    private string _myUsername = string.Empty;
    private string _myName = string.Empty;
    private string _myAvatar = string.Empty;
    private int _myLevel;
    private long _myExp;
    private bool _isAdmin;

    private string _room = "group";
    private bool _initialized;
    private bool _webMode;

    private long _oldestId;
    private bool _hasMore;
    private bool _loadingMore;
    private ScrollViewer? _scrollViewer;
    private bool _profileLoading;

    // 玩法中心（积分 / 抽卡 / 等级）与红包
    private readonly ObservableCollection<PointsItemVm> _pointsItems = [];
    private readonly ObservableCollection<PointsLogVm> _pointsLogs = [];
    private readonly ObservableCollection<GachaResultVm> _gachaResults = [];
    private readonly ObservableCollection<LevelSourceVm> _levelSources = [];
    private readonly ObservableCollection<RedPacketGrabVm> _rpGrabs = [];
    private readonly List<GachaPoolVm> _gachaPools = [];
    private readonly Dictionary<string, ChatMessageVm> _aiStreams = [];
    private long _rpCurrentId;

    private string _stickerSort = "hot";
    private bool _suppressStickerClick;
    private readonly List<string> _searchTypes = [];
    private CancellationTokenSource? _searchCts;

    // 主题色板
    private Brush _selfBubble = null!;
    private Brush _otherBubble = null!;
    private Brush _selfText = null!;
    private Brush _otherText = null!;
    private Brush _accent = null!;
    private Brush _transparent = null!;
    private Brush? _flash;

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
        SearchResultsList.ItemsSource = _searchResults;
        PointsItemsList.ItemsSource = _pointsItems;
        PointsLogsList.ItemsSource = _pointsLogs;
        GachaResultsList.ItemsSource = _gachaResults;
        LevelSourcesList.ItemsSource = _levelSources;
        RpGrabsList.ItemsSource = _rpGrabs;

        _chat.MessageReceived += OnMessageReceived;
        _chat.MessageRecalled += OnMessageRecalled;
        _chat.ReadStateChanged += OnReadStateChanged;
        _chat.UsersChanged += OnUsersChanged;
        _chat.ConnectionChanged += OnConnectionChanged;
        _chat.AiStreamReceived += OnAiStream;
        _chat.RedPacketChanged += OnRedPacketChanged;
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

        ChatHub.IsChatPageOpen = true;
        ChatHub.ResetPendingNotifications();
        EnsurePalette();
        ApplyStaticText();

        try
        {
            var me = await _chat.GetMeAsync();
            if (me is not null)
            {
                _myId = me.Id;
                _myUsername = me.Username;
                _myName = DisplayNameOf(me);
                _myAvatar = me.Avatar;
                _myLevel = me.Level;
                _myExp = me.Exp;
                _isAdmin = me.IsAdmin;
                ChatHub.SetMyId(_myId);
                UpdateProfileButton();
            }

            await LoadAiConfigAsync();
            await LoadContactsAsync();
            _rawUsers = await _chat.GetUsersAsync();
            RebuildUsers();

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

        _chat.MessageReceived -= OnMessageReceived;
        _chat.MessageRecalled -= OnMessageRecalled;
        _chat.ReadStateChanged -= OnReadStateChanged;
        _chat.UsersChanged -= OnUsersChanged;
        _chat.ConnectionChanged -= OnConnectionChanged;
        _chat.AiStreamReceived -= OnAiStream;
        _chat.RedPacketChanged -= OnRedPacketChanged;
        ChatHub.RoomOpenRequested -= OnRoomOpenRequested;
    }

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
            UpdateRoomHeader();
            RebuildMessages();
            BuildSearchOptions();
        });
    }

    private void ApplyStaticText()
    {
        HeaderText.Text = Loc.T("Nav_Chat");
        ModeButton.Content = Loc.T(_webMode ? "Chat_Mode_ToNative" : "Chat_Mode_ToWeb");
        ConnText.Text = Loc.T(_chat.IsConnected ? "Chat_Connected" : "Chat_Connecting");
        UpdateRoomHeader();
        UpdateProfileButton();
        EatButton.Visibility = SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ---------------- 主题色板 ----------------
    private void EnsurePalette()
    {
        if (_selfBubble is not null)
        {
            return;
        }
        var dark = ActualTheme == ElementTheme.Dark;
        _selfBubble = BrushOf(dark ? "#7C5CFF" : "#10B981");
        _otherBubble = BrushOf(dark ? "#1D2130" : "#FFFFFF");
        _selfText = BrushOf("#FFFFFF");
        _otherText = BrushOf(dark ? "#E8EAF0" : "#111827");
        _accent = BrushOf(dark ? "#8B5CF6" : "#10B981");
        _transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _flash = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(0x3A, 0x8B, 0x5C, 0xF6)
            : Windows.UI.Color.FromArgb(0x2E, 0x10, 0xB9, 0x81));
    }

    private static Brush BrushOf(string hex) => new SolidColorBrush(ColorOf(hex));

    private static Windows.UI.Color ColorOf(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[4..6], 16), Convert.ToByte(s[6..8], 16));
        }
        return Windows.UI.Color.FromArgb(
            255, Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
    }

    private static Brush LevelBrushOf(int level) =>
        BrushOf(LevelColors[Math.Clamp(level, 0, LevelColors.Length - 1)]);

    private static string LevelTextOf(int level) => "Lv" + Math.Clamp(level, 0, 6);

    // ---------------- 网页版 / 原生版 ----------------
    private static string ChatModeSetting() => AppState.SettingsService.Load().ChatMode switch
    {
        "Web" => "Web",
        "Native" => "Native",
        _ => "Auto"
    };

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

        SwitchMode(false);
        MainWindow.Instance?.ShowStatus(Loc.T("Chat_WebFailed"), InfoBarSeverity.Warning);
    }

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
            HookScrollViewer();
            _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, HookScrollViewer);
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

    private static string AutologinUrl()
        => SessionState.BaseUrl.TrimEnd('/') + "/api/autologin?jwt=" + Uri.EscapeDataString(SessionState.Token ?? string.Empty);

    // ---------------- 会话切换与历史 ----------------
    private async Task OpenRoomAsync(string room, long anchorId = 0)
    {
        EnsurePalette();
        _room = room;
        ChatHub.CurrentRoom = room;
        _mentionPicks.Clear();
        HideMention();
        UpdateRoomHeader();

        _readState.Clear();
        ClearAiStreams();
        await LoadHistoryAsync(room, anchorId);
        await LoadReadStateAsync(room);
        RefreshReadLabels();
        await SafeMarkReadAsync(room);
        await RefreshUnreadAsync();
        RebuildUsers();
        HookScrollViewer();
    }

    private async Task LoadHistoryAsync(string room, long anchorId)
    {
        _rawMessages.Clear();
        _messages.Clear();
        _hasMore = false;
        _loadingMore = false;
        _oldestId = 0;

        try
        {
            var history = anchorId > 0
                ? await _chat.GetHistoryAsync(room, PageSize, anchorId + 1)
                : await _chat.GetHistoryAsync(room, PageSize);
            if (_room != room)
            {
                return;
            }
            foreach (var dto in history.Messages ?? [])
            {
                _rawMessages.Add(dto);
            }
            _hasMore = history.HasMore;
            _oldestId = _rawMessages.Count > 0 ? _rawMessages[0].Id : 0;
            RebuildMessages();
            _ = HydrateRedPacketsAsync();

            EmptyText.Text = Loc.T(room == "group" ? "Chat_Empty_Group" : "Chat_Empty_Pm");
            EmptyText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (anchorId > 0)
            {
                if (!FocusMessage(anchorId))
                {
                    ScrollToBottom();
                }
            }
            else
            {
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("加载聊天记录失败。", ex);
        }
    }

    private async Task LoadMoreAsync()
    {
        if (!_hasMore || _loadingMore || _oldestId <= 0)
        {
            return;
        }
        _loadingMore = true;
        var room = _room;
        var sv = _scrollViewer ?? FindScrollViewer(MessagesList);
        var prevHeight = sv?.ScrollableHeight ?? 0;
        var prevOffset = sv?.VerticalOffset ?? 0;

        try
        {
            var older = await _chat.GetHistoryAsync(room, PageSize, _oldestId);
            if (_room != room)
            {
                return;
            }
            var list = older.Messages ?? [];
            if (list.Length == 0)
            {
                _hasMore = false;
                return;
            }
            _rawMessages.InsertRange(0, list);
            _oldestId = _rawMessages[0].Id;
            _hasMore = older.HasMore;
            RebuildMessages();

            _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                MessagesList.UpdateLayout();
                if (_scrollViewer is { } viewer)
                {
                    var added = viewer.ScrollableHeight - prevHeight;
                    viewer.ChangeView(null, prevOffset + added, null, disableAnimation: true);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载更早消息失败：{ex.Message}");
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void MessagesList_Loaded(object sender, RoutedEventArgs e) => HookScrollViewer();

    private void HookScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            return;
        }
        _scrollViewer = FindScrollViewer(MessagesList);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += (_, _) =>
            {
                if (_scrollViewer.VerticalOffset <= 40)
                {
                    _ = LoadMoreAsync();
                }
            };
        }
    }

    private void UpdateRoomHeader()
    {
        EnsurePalette();
        if (_room == "group")
        {
            RoomAvatarBorder.Visibility = Visibility.Visible;
            RoomAvatarBorder.Background = _accent;
            RoomAvatarText.Text = "群";
            RoomAvatarImage.Visibility = Visibility.Collapsed;
            RoomTitle.Text = Loc.T("Chat_Group_Title");
            RoomLevelBadge.Visibility = Visibility.Collapsed;

            var online = _rawUsers.Count(u => u.Online);
            RoomStatusText.Text = Loc.Tf("Chat_Group_Status", online);
            return;
        }

        var peerId = OtherIdFromRoom(_room) ?? 0;
        var user = _rawUsers.FirstOrDefault(u => u.Id == peerId);
        if (user is null && peerId > 0 && peerId == _aiId)
        {
            // 小火人可能不在 /api/users 返回里，用配置兜一条，会话头才显示名字与头像。
            user = new ChatUserDto
            {
                Id = _aiId,
                Username = _aiName,
                Nickname = _aiName,
                DisplayName = _aiName,
                Avatar = _aiAvatar,
                Online = true,
                IsAi = true
            };
        }
        var name = user is null ? Loc.T("Chat_Private") : ContactNameOf(user);
        RoomTitle.Text = name;
        RoomAvatarText.Text = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        RoomAvatarBorder.Background = AvatarBrushFor(peerId);
        RoomAvatarBorder.Visibility = user is null || string.IsNullOrWhiteSpace(user.Avatar) ? Visibility.Visible : Visibility.Collapsed;
        if (user is not null && !string.IsNullOrWhiteSpace(user.Avatar))
        {
            RoomAvatarImage.Source = new BitmapImage(new Uri(Absolute(user.Avatar)));
            RoomAvatarImage.Visibility = Visibility.Visible;
        }
        else
        {
            RoomAvatarImage.Visibility = Visibility.Collapsed;
        }

        if (user is not null && user.Level > 0)
        {
            RoomLevelText.Text = LevelTextOf(user.Level);
            RoomLevelBadge.Background = LevelBrushOf(user.Level);
            RoomLevelBadge.Visibility = Visibility.Visible;
        }
        else
        {
            RoomLevelBadge.Visibility = Visibility.Collapsed;
        }

        if (user is not null && user.Id == _aiId)
        {
            RoomStatusText.Text = Loc.T("Chat_Ai_Status");
        }
        else if (user is not null && user.Online)
        {
            RoomStatusText.Text = Loc.T("Chat_Online_Status");
        }
        else
        {
            RoomStatusText.Text = Loc.T("Chat_Offline_Status");
        }
    }

    private void UpdateProfileButton()
    {
        var name = string.IsNullOrWhiteSpace(_myName) ? "我" : _myName;
        ProfileButtonText.Text = name[..1].ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(_myAvatar))
        {
            ProfileButtonImage.Source = new BitmapImage(new Uri(Absolute(_myAvatar)));
            ProfileButtonImage.Visibility = Visibility.Visible;
        }
        else
        {
            ProfileButtonImage.Visibility = Visibility.Collapsed;
        }
    }

    private async void UsersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarItemVm item || item.IsHeader)
        {
            return;
        }
        await OpenRoomAsync(item.Room);
    }

    private string PmRoom(int otherId)
    {
        var a = Math.Min(_myId, otherId);
        var b = Math.Max(_myId, otherId);
        return $"pm:{a}:{b}";
    }

    // ---------------- 侧栏（置顶 / 群聊 / 在线 / 离线 + 搜索） ----------------
    private void UserSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildUsers();

    private void RebuildUsers()
    {
        EnsurePalette();
        var keyword = (UserSearchBox.Text ?? string.Empty).Trim();
        var kw = keyword.ToLowerInvariant();
        _users.Clear();

        bool Matches(ChatUserDto u) =>
            kw.Length == 0
            || DisplayNameOf(u).ToLowerInvariant().Contains(kw)
            || ContactNameOf(u).ToLowerInvariant().Contains(kw)
            || u.Username.ToLowerInvariant().Contains(kw);

        // 小火人：优先用 /api/users 里的那条；接口没带上时用 /api/ai/config 兜一条，
        // 保证它始终出现在侧栏（与网页端行为一致）。
        var aiUser = _rawUsers.FirstOrDefault(u => u.Id == _aiId || u.IsAi);
        if (aiUser is null && _aiId > 0)
        {
            aiUser = new ChatUserDto
            {
                Id = _aiId,
                Username = _aiName,
                Nickname = _aiName,
                DisplayName = _aiName,
                Avatar = _aiAvatar,
                Online = true,
                IsAi = true
            };
        }

        var others = _rawUsers.Where(u => u.Id != _myId && u.Id != _aiId && !u.IsAi).ToArray();

        // 置顶：小火人排最前，其余按用户置顶顺序。
        var pinnedRows = new List<ChatUserDto>();
        if (aiUser is not null && _pinned.Contains(aiUser.Id))
        {
            pinnedRows.Add(aiUser);
        }
        foreach (var id in _pinned)
        {
            if (aiUser is not null && id == aiUser.Id)
            {
                continue;
            }
            var pinnedUser = _rawUsers.FirstOrDefault(x => x.Id == id && x.Id != _myId);
            if (pinnedUser is not null && pinnedRows.All(r => r.Id != pinnedUser.Id))
            {
                pinnedRows.Add(pinnedUser);
            }
        }
        if (pinnedRows.Count > 0)
        {
            _users.Add(SidebarItemVm.Header(Loc.T("Chat_Sidebar_Pinned")));
            foreach (var u in pinnedRows)
            {
                _users.Add(BuildUserRow(u, keywordsFiltered: false));
            }
        }

        var groupUnread = _unread.GetValueOrDefault("group");
        _users.Add(new SidebarItemVm
        {
            Title = string.Empty,
            DisplayName = Loc.T("Chat_Group_Title"),
            SubText = Loc.T("Chat_Group_Sub"),
            TailText = others.Count(u => u.Online).ToString(),
            UnreadText = groupUnread > 99 ? "99+" : groupUnread.ToString(),
            UnreadVisibility = groupUnread > 0 ? Visibility.Visible : Visibility.Collapsed,
            AvatarText = "群",
            AvatarBrush = _accent,
            OnlineDotVisibility = Visibility.Collapsed,
            AiVisibility = Visibility.Collapsed,
            LevelVisibility = Visibility.Collapsed,
            HeaderVisibility = Visibility.Collapsed,
            RowVisibility = Visibility.Visible,
            RowBackground = _room == "group" ? RowActiveBrush() : _transparent,
            Room = "group",
            IsHeader = false
        });

        if (aiUser is not null && !_pinned.Contains(aiUser.Id) && Matches(aiUser))
        {
            _users.Add(BuildUserRow(aiUser, forceAi: true, keywordsFiltered: false));
        }

        var online = others.Where(u => u.Online).OrderBy(DisplayNameOf).ToArray();
        var offline = others.Where(u => !u.Online).OrderByDescending(u => u.LastSeen).ToArray();

        _users.Add(SidebarItemVm.Header(Loc.Tf("Chat_Sidebar_Online", online.Length)));
        var onlineShown = online.Where(Matches).ToArray();
        if (onlineShown.Length == 0)
        {
            _users.Add(SidebarItemVm.Empty(kw.Length > 0 ? Loc.T("Chat_Sidebar_NoMatch") : Loc.T("Chat_Sidebar_NoOnline")));
        }
        else
        {
            foreach (var u in onlineShown)
            {
                _users.Add(BuildUserRow(u, keywordsFiltered: false));
            }
        }

        _users.Add(SidebarItemVm.Header(Loc.Tf("Chat_Sidebar_Offline", offline.Length)));
        var offlineShown = offline.Where(Matches).ToArray();
        if (offlineShown.Length == 0)
        {
            _users.Add(SidebarItemVm.Empty(kw.Length > 0 ? Loc.T("Chat_Sidebar_NoMatch") : Loc.T("Chat_Sidebar_NoOffline")));
        }
        else
        {
            foreach (var u in offlineShown)
            {
                _users.Add(BuildUserRow(u, keywordsFiltered: false));
            }
        }
    }

    private SidebarItemVm BuildUserRow(ChatUserDto u, bool forceAi = false, bool keywordsFiltered = true)
    {
        var isAi = forceAi || u.Id == _aiId || u.IsAi;
        var room = PmRoom(u.Id);
        var unread = _unread.GetValueOrDefault(room);
        return new SidebarItemVm
        {
            Title = string.Empty,
            DisplayName = ContactNameOf(u),
            SubText = isAi
                ? Loc.T("Chat_Ai_Sub")
                : u.Online
                    ? Loc.T("Chat_Online")
                    : ChatPageFormatting.Ago(u.LastSeen),
            TailText = isAi ? string.Empty : u.Online ? string.Empty : Loc.T("Chat_Offline_Tag"),
            UnreadText = unread > 99 ? "99+" : unread.ToString(),
            UnreadVisibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed,
            AvatarText = ContactNameOf(u) is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?",
            AvatarImage = string.IsNullOrWhiteSpace(u.Avatar) ? null : SafeBitmap(u.Avatar),
            AvatarBrush = AvatarBrushFor(u.Id),
            OnlineDotVisibility = u.Online ? Visibility.Visible : Visibility.Collapsed,
            AiVisibility = isAi ? Visibility.Visible : Visibility.Collapsed,
            LevelText = LevelTextOf(u.Level),
            LevelBrush = LevelBrushOf(u.Level),
            LevelVisibility = isAi ? Visibility.Collapsed : Visibility.Visible,
            HeaderVisibility = Visibility.Collapsed,
            RowVisibility = Visibility.Visible,
            RowBackground = _room == room ? RowActiveBrush() : _transparent,
            Room = room,
            PeerId = u.Id,
            IsHeader = false
        };
    }

    private Brush RowActiveBrush() => new SolidColorBrush(
        ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0x2E, 0x8B, 0x5C, 0xF6)
            : Windows.UI.Color.FromArgb(0x1F, 0x10, 0xB9, 0x81));

    private void UsersList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (FindDataContext<SidebarItemVm>(e.OriginalSource as DependencyObject) is not { PeerId: > 0 } item)
        {
            return;
        }
        e.Handled = true;

        var menu = new MenuFlyout();
        var noteItem = new MenuFlyoutItem { Text = Loc.T("Chat_Menu_Note") };
        noteItem.Click += async (_, _) => await ShowNoteDialogAsync(item.PeerId, item.DisplayName);
        menu.Items.Add(noteItem);

        var pinned = _pinned.Contains(item.PeerId);
        var pinItem = new MenuFlyoutItem { Text = Loc.T(pinned ? "Chat_Menu_Unpin" : "Chat_Menu_Pin") };
        pinItem.Click += async (_, _) => await TogglePinAsync(item.PeerId, !pinned);
        menu.Items.Add(pinItem);

        menu.ShowAt(UsersList, new FlyoutShowOptions { Position = e.GetPosition(UsersList) });
    }

    private async Task ShowNoteDialogAsync(int peerId, string currentName)
    {
        var box = new TextBox
        {
            Text = _notes.GetValueOrDefault(peerId, string.Empty),
            PlaceholderText = Loc.T("Chat_Note_Hint")
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Tf("Chat_Note_Title", currentName),
            Content = box,
            PrimaryButtonText = Loc.T("Chat_Save"),
            SecondaryButtonText = Loc.T("Chat_Note_Clear"),
            CloseButtonText = Loc.T("Chat_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        var note = result == ContentDialogResult.Primary ? (box.Text ?? string.Empty).Trim() : string.Empty;
        try
        {
            var resp = await _chat.SaveNoteAsync(peerId, note);
            if (!resp.Ok)
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Save_Failed"), InfoBarSeverity.Warning);
                return;
            }
            if (note.Length == 0)
            {
                _notes.Remove(peerId);
            }
            else
            {
                _notes[peerId] = note;
            }
            RebuildUsers();
            UpdateRoomHeader();
        }
        catch (Exception ex)
        {
            AppLog.Error("保存联系人备注失败。", ex);
        }
    }

    private async Task TogglePinAsync(int peerId, bool pinned)
    {
        try
        {
            var resp = await _chat.TogglePinAsync(peerId, pinned);
            if (!resp.Ok)
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Save_Failed"), InfoBarSeverity.Warning);
                return;
            }
            if (pinned)
            {
                _pinned.Add(peerId);
            }
            else
            {
                _pinned.Remove(peerId);
            }
            RebuildUsers();
        }
        catch (Exception ex)
        {
            AppLog.Error("切换联系人置顶失败。", ex);
        }
    }

    /// <summary>取小火人公开配置：拿到账号 id / 名字 / 头像，侧栏才能始终把它列出来。</summary>
    private async Task LoadAiConfigAsync()
    {
        try
        {
            var cfg = await _chat.GetAiConfigAsync();
            if (cfg is not { UserId: > 0 })
            {
                return;
            }
            _aiId = cfg.UserId;
            if (!string.IsNullOrWhiteSpace(cfg.Name))
            {
                _aiName = cfg.Name;
            }
            _aiAvatar = cfg.Avatar ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"读取小火人配置失败：{ex.Message}");
        }
    }

    private async Task LoadContactsAsync()
    {
        try
        {
            var contacts = await _chat.GetContactsAsync();
            if (!contacts.Ok)
            {
                return;
            }
            _notes.Clear();
            foreach (var (key, value) in contacts.Notes ?? [])
            {
                if (int.TryParse(key, out var id) && !string.IsNullOrWhiteSpace(value))
                {
                    _notes[id] = value;
                }
            }
            _pinned.Clear();
            foreach (var id in contacts.Pinned ?? [])
            {
                _pinned.Add(id);
            }
            if (contacts.AiId > 0)
            {
                _aiId = contacts.AiId;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载联系人失败：{ex.Message}");
        }
    }

    // ---------------- 发送 / 上传 ----------------
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendTextAsync();

    private async void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
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
        if (!_chat.IsConnected)
        {
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Conn_Banner"), InfoBarSeverity.Warning);
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
            await SendFileAsync(file.Path);
        }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickAsync(["*"]);
        if (file is not null)
        {
            await SendFileAsync(file.Path);
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

    /// <summary>Ctrl+V 粘贴图片：剪贴板里有位图时转存临时文件并直接上传发送。</summary>
    private async void InputBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                return;
            }
            e.Handled = true;
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var temp = Path.Combine(Path.GetTempPath(), $"teatt-paste-{Guid.NewGuid():N}.png");
            await using (var file = File.Create(temp))
            {
                await stream.AsStreamForRead().CopyToAsync(file);
            }
            await SendFileAsync(temp);
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件清理失败可忽略
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"粘贴图片失败：{ex.Message}");
        }
    }

    private async Task SendFileAsync(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxUploadBytes)
            {
                MainWindow.Instance?.ShowStatus(Loc.T("Chat_Upload_TooLarge"), InfoBarSeverity.Warning);
                return;
            }

            UploadProgressPanel.Visibility = Visibility.Visible;
            UploadProgressBar.Value = 0;
            UploadProgressText.Text = "0%";
            var progress = new Progress<double>(p =>
            {
                UploadProgressBar.Value = p;
                UploadProgressText.Text = $"{p * 100:0}%";
            });

            var uploaded = await _chat.UploadAsync(path, progress);
            if (!uploaded.Ok || string.IsNullOrEmpty(uploaded.Url))
            {
                MainWindow.Instance?.ShowStatus(uploaded.Error ?? Loc.T("Chat_Upload_Failed"), InfoBarSeverity.Warning);
                return;
            }
            var kind = uploaded.IsImage ? "image" : (uploaded.IsVideo ? "video" : "file");
            var payload = new ChatFileDto { Url = uploaded.Url, Name = uploaded.Name, Size = uploaded.Size };
            await _chat.SendChatAsync(kind, string.Empty, payload, TargetId());
        }
        catch (Exception ex)
        {
            AppLog.Error("发送文件失败。", ex);
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Upload_Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            UploadProgressPanel.Visibility = Visibility.Collapsed;
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
    private async void StickerFlyout_Opening(object sender, object e)
    {
        UpdateStickerSortButtons();
        await LoadStickersAsync();
    }

    private void UpdateStickerSortButtons()
    {
        var hot = _stickerSort == "hot";
        var normalText = BrushOf(ActualTheme == ElementTheme.Dark ? "#E8EAF0" : "#111827");
        StkHotButton.Background = hot ? _accent : _transparent;
        StkHotButton.Foreground = hot ? BrushOf("#FFFFFF") : normalText;
        StkNewButton.Background = hot ? _transparent : _accent;
        StkNewButton.Foreground = hot ? normalText : BrushOf("#FFFFFF");
    }

    private async void StickerSort_Click(object sender, RoutedEventArgs e)
    {
        var sort = ReferenceEquals(sender, StkNewButton) ? "time" : "hot";
        if (sort == _stickerSort)
        {
            return;
        }
        _stickerSort = sort;
        UpdateStickerSortButtons();
        await LoadStickersAsync();
    }

    private async Task LoadStickersAsync()
    {
        try
        {
            var result = await _chat.GetStickersAsync(_stickerSort);
            _stickers.Clear();
            foreach (var s in result.Stickers ?? [])
            {
                if (string.IsNullOrEmpty(s.Url))
                {
                    continue;
                }
                _stickers.Add(new StickerVm(s.Id, SafeBitmap(s.Url), s.SendCount, result.IsAdmin));
            }
            UploadStickerButton.Visibility = Visibility.Visible;
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
        if (_suppressStickerClick)
        {
            _suppressStickerClick = false;
            return;
        }
        if (e.ClickedItem is StickerVm vm)
        {
            StickerButton.Flyout?.Hide();
            await _chat.SendStickerAsync(vm.Id, TargetId());
        }
    }

    private async void DeleteSticker_Click(object sender, RoutedEventArgs e)
    {
        _suppressStickerClick = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not StickerVm vm)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Chat_Sticker_Delete_Title"),
            Content = Loc.T("Chat_Sticker_Delete_Confirm"),
            PrimaryButtonText = Loc.T("Chat_Delete"),
            CloseButtonText = Loc.T("Chat_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            var resp = await _chat.DeleteStickerAsync(vm.Id);
            if (resp.Ok)
            {
                await LoadStickersAsync();
            }
            else
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Delete_Failed"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("删除表情失败。", ex);
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
                MainWindow.Instance?.ShowStatus(result.Error ?? Loc.T("Chat_Upload_Failed"), InfoBarSeverity.Warning);
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

        if (q.Length == 0 || Loc.T("Chat_Mention_All").Contains(q, StringComparison.Ordinal))
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
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6B, 0x72, 0x80))
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
        string.IsNullOrWhiteSpace(user.DisplayName)
            ? (string.IsNullOrWhiteSpace(user.Nickname) ? user.Username : user.Nickname)
            : user.DisplayName;

    private string ContactNameOf(ChatUserDto user) =>
        _notes.TryGetValue(user.Id, out var note) && !string.IsNullOrWhiteSpace(note)
            ? note
            : DisplayNameOf(user);

    // ---------------- 撤回 / 附件 ----------------
    private async void Recall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageVm vm)
        {
            await _chat.RecallAsync(vm.Id);
        }
    }

    private void ReEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessageVm vm
            || string.IsNullOrEmpty(vm.ReEditContent))
        {
            return;
        }
        var current = (InputBox.Text ?? string.Empty).TrimEnd();
        InputBox.Text = current.Length == 0 ? vm.ReEditContent : current + " " + vm.ReEditContent;
        InputBox.SelectionStart = InputBox.Text.Length;
        InputBox.Focus(FocusState.Programmatic);
    }

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
            await SaveFileAsync(vm.FileUrl, vm.FileName);
        }
    }

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
        dialog.Closed += (_, _) => player.Source = null;
        await dialog.ShowAsync();
    }

    private async Task SaveFileAsync(string url, string fileName)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
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
            var bytes = await DownloadClient.GetByteArrayAsync(url);
            await FileIO.WriteBytesAsync(target, bytes);
            MainWindow.Instance?.ShowStatus(Loc.Tf("Chat_Saved_To", target.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.ShowStatus(Loc.Tf("Chat_Download_Failed", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---------------- 消息渲染 ----------------
    private void RebuildMessages()
    {
        EnsurePalette();
        _messages.Clear();
        DateTime? lastDay = null;
        foreach (var dto in _rawMessages)
        {
            var day = DayOf(dto.CreatedAt);
            if (lastDay is null || day != lastDay)
            {
                _messages.Add(ChatMessageVm.Divider(DayLabel(dto.CreatedAt)));
                lastDay = day;
            }
            _messages.Add(BuildMessageVm(dto));
        }
        EmptyText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ChatMessageVm BuildMessageVm(ChatMessageDto dto)
    {
        var isMine = dto.FromId == _myId;
        var kind = dto.EffectiveKind;
        var url = Absolute(dto.FilePath);
        var name = string.IsNullOrWhiteSpace(dto.FromName) ? "?" : dto.FromName;
        var recalled = dto.Recalled;
        var selfRecall = recalled && isMine;
        var adminNote = recalled && !isMine && _isAdmin;
        var otherRecall = recalled && !isMine && !_isAdmin;
        var hidden = selfRecall || otherRecall;

        ImageSource? image = null;
        if (!hidden && kind is "image" or "sticker" && !string.IsNullOrEmpty(url))
        {
            image = SafeBitmap(url);
        }

        var read = isMine ? ReadLabelFor(dto.Id) : string.Empty;
        var canRecall = isMine && !recalled
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - dto.CreatedAt <= 120000;

        return new ChatMessageVm
        {
            Id = dto.Id,
            IsMine = isMine,
            Kind = kind,
            Content = dto.Content,
            ReEditContent = selfRecall ? dto.Content : string.Empty,
            DividerVisibility = Visibility.Collapsed,
            MessageVisibility = Visibility.Visible,
            RowAlign = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = isMine ? _selfBubble : _otherBubble,
            ContentForeground = isMine ? _selfText : _otherText,
            BubbleCorner = isMine ? new CornerRadius(14, 14, 5, 14) : new CornerRadius(14, 14, 14, 5),
            BubbleVisibility = hidden ? Visibility.Collapsed : Visibility.Visible,
            TextVisibility = !hidden && kind == "text" ? Visibility.Visible : Visibility.Collapsed,
            Image = image,
            ImageVisibility = image is not null ? Visibility.Visible : Visibility.Collapsed,
            ImageMaxSize = kind == "sticker" ? 130 : 360,
            FileUrl = url,
            FileName = dto.FileName ?? string.Empty,
            FileLabel = BuildFileLabel(dto),
            FileGlyph = kind == "video" ? "\uE768" : "\uE8A5",
            FileVisibility = !hidden && kind is "file" or "video" ? Visibility.Visible : Visibility.Collapsed,
            SenderText = name,
            SenderVisibility = _room == "group" && !isMine ? Visibility.Visible : Visibility.Collapsed,
            SenderLevelText = LevelTextOf(dto.FromLevel),
            SenderLevelBrush = LevelBrushOf(dto.FromLevel),
            SenderLevelVisibility = Visibility.Visible,
            RecallText = selfRecall ? Loc.T("Chat_Recall_Self") : Loc.T("Chat_Recall_Other"),
            RecallVisibility = hidden ? Visibility.Visible : Visibility.Collapsed,
            EditVisibility = selfRecall && !string.IsNullOrEmpty(dto.Content) ? Visibility.Visible : Visibility.Collapsed,
            AdminNoteText = Loc.T("Chat_Recall_Admin"),
            AdminNoteVisibility = adminNote ? Visibility.Visible : Visibility.Collapsed,
            TimeText = FormatTime(dto.CreatedAt),
            ReadText = read,
            RecallButtonVisibility = canRecall ? Visibility.Visible : Visibility.Collapsed,
            AvatarText = name[..1].ToUpperInvariant(),
            AvatarImage = string.IsNullOrWhiteSpace(dto.FromAvatar) ? null : SafeBitmap(dto.FromAvatar),
            AvatarBrush = AvatarBrushFor(dto.FromId),
            AvatarLeftVisibility = isMine ? Visibility.Collapsed : Visibility.Visible,
            AvatarRightVisibility = isMine ? Visibility.Visible : Visibility.Collapsed,
            RedPacketId = dto.RedPacketId,
            RedPacketVisibility = !hidden && kind == "redpacket" ? Visibility.Visible : Visibility.Collapsed,
            RedPacketTitle = RedPacketTitleOf(dto),
            RedPacketSub = RedPacketSubOf(dto),
            RedPacketAction = RedPacketActionOf(dto),
            FlashBackground = _transparent
        };
    }

    private string ReadLabelFor(long msgId)
    {
        var readers = _readState
            .Where(kv => kv.Key != _myId && kv.Value.LastReadId >= msgId)
            .Select(kv => string.IsNullOrWhiteSpace(kv.Value.Username) ? "用户" + kv.Key : kv.Value.Username)
            .ToArray();
        if (readers.Length == 0)
        {
            return string.Empty;
        }
        return _room == "group"
            ? Loc.Tf("Chat_Read_List", string.Join("，", readers))
            : Loc.T("Chat_Read");
    }

    private void RefreshReadLabels()
    {
        foreach (var vm in _messages)
        {
            if (!vm.IsDivider && vm.IsMine)
            {
                vm.ReadText = ReadLabelFor(vm.Id);
            }
        }
    }

    private async Task LoadReadStateAsync(string room)
    {
        try
        {
            var state = await _chat.GetReadStateAsync(room);
            if (_room != room || !state.Ok)
            {
                return;
            }
            _readState.Clear();
            foreach (var s in state.States ?? [])
            {
                _readState[s.UserId] = (s.Username, s.LastReadId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载已读状态失败：{ex.Message}");
        }
    }

    private static string BuildFileLabel(ChatMessageDto dto)
    {
        var name = string.IsNullOrWhiteSpace(dto.FileName) ? Loc.T("Chat_File") : dto.FileName!;
        var size = FormatSize(dto.FileSize ?? 0);
        return size.Length == 0 ? name : $"{name} ({size})";
    }

    /// <summary>消息文本挂载后按 @对象 上色（列表回收时 DataContextChanged 会重新触发）。</summary>
    private void MessageText_Loaded(object sender, RoutedEventArgs e) => ApplyMentionRuns(sender);

    private void MessageText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        => ApplyMentionRuns(sender);

    private void ApplyMentionRuns(object sender)
    {
        if (sender is not TextBlock tb || tb.DataContext is not ChatMessageVm vm)
        {
            return;
        }

        tb.Inlines.Clear();
        var text = vm.Content ?? string.Empty;
        var cursor = 0;
        foreach (Match match in MentionRegex.Matches(text))
        {
            if (!IsKnownMention(match.Value[1..]))
            {
                continue;
            }
            if (match.Index > cursor)
            {
                tb.Inlines.Add(new Run { Text = text[cursor..match.Index] });
            }
            tb.Inlines.Add(new Run
            {
                Text = match.Value,
                Foreground = _accent,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length)
        {
            tb.Inlines.Add(new Run { Text = text[cursor..] });
        }
    }

    private bool IsKnownMention(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        if (Loc.T("Chat_Mention_All") == name)
        {
            return true;
        }
        return _rawUsers.Any(u => DisplayNameOf(u) == name);
    }

    private void MessageImage_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessageVm vm
            || string.IsNullOrEmpty(vm.FileUrl))
        {
            return;
        }
        ShowImageViewer(vm.FileUrl, vm.FileName);
    }

    private static Brush AvatarBrushFor(int userId)
    {
        var hex = AvatarColors[Math.Abs(userId) % AvatarColors.Length];
        return BrushOf(hex);
    }

    private static BitmapImage? SafeBitmap(string url)
    {
        try
        {
            return new BitmapImage(new Uri(Absolute(url)));
        }
        catch
        {
            return null;
        }
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
        if (t.Date == now.Date)
        {
            return t.ToString("HH:mm");
        }
        if (t.Date == now.Date.AddDays(-1))
        {
            return Loc.T("Chat_Yesterday") + t.ToString("HH:mm");
        }
        return t.Year == now.Year ? t.ToString("M月d日 HH:mm") : t.ToString("yyyy年M月d日 HH:mm");
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
        return $"{value:0.#} {units[i]}";
    }

    private static DateTime DayOf(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().Date;

    private static string DayLabel(long ms)
    {
        var day = DayOf(ms);
        var today = DateTimeOffset.Now.Date;
        if (day == today)
        {
            return Loc.T("Chat_Today");
        }
        if (day == today.AddDays(-1))
        {
            return Loc.T("Chat_Yesterday");
        }
        return day.Year == today.Year ? $"{day.Month}月{day.Day}日" : day.ToString("yyyy年M月d日");
    }

    private bool FocusMessage(long id)
    {
        var vm = _messages.FirstOrDefault(m => !m.IsDivider && m.Id == id);
        if (vm is null)
        {
            return false;
        }
        MessagesList.ScrollIntoView(vm, ScrollIntoViewAlignment.Leading);
        if (_flash is not null)
        {
            vm.FlashBackground = _flash;
            _ = ClearFlashAsync(vm);
        }
        return true;
    }

    private async Task ClearFlashAsync(ChatMessageVm vm)
    {
        await Task.Delay(1800);
        _dispatcher.TryEnqueue(() => vm.FlashBackground = _transparent);
    }

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

    private static T? FindDataContext<T>(DependencyObject? node) where T : class
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: T match })
            {
                return match;
            }
            try
            {
                node = VisualTreeHelper.GetParent(node);
            }
            catch
            {
                // 文本元素等非可视对象没有父级，直接结束
                return null;
            }
        }
        return null;
    }

    // ---------------- 实时事件 ----------------
    private void OnMessageReceived(ChatMessageDto dto)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (dto.Room != _room)
            {
                if (dto.FromId != _myId)
                {
                    _unread[dto.Room] = _unread.GetValueOrDefault(dto.Room) + 1;
                    RebuildUsers();
                }
                return;
            }

            var lastDay = _rawMessages.Count > 0 ? DayOf(_rawMessages[^1].CreatedAt) : (DateTime?)null;
            // 小火流式收尾：正式消息到达后移除临时气泡
            if (!string.IsNullOrEmpty(dto.StreamId) && _aiStreams.Remove(dto.StreamId, out var streamVm))
            {
                _messages.Remove(streamVm);
            }
            var day = DayOf(dto.CreatedAt);
            var isFirst = _rawMessages.Count == 0;
            if (lastDay is null || lastDay != day)
            {
                _messages.Add(ChatMessageVm.Divider(DayLabel(dto.CreatedAt)));
            }
            _rawMessages.Add(dto);
            if (isFirst)
            {
                EmptyText.Visibility = Visibility.Collapsed;
            }
            _messages.Add(BuildMessageVm(dto));
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
            var vm = _messages.FirstOrDefault(m => !m.IsDivider && m.Id == id);
            if (vm is null)
            {
                return;
            }
            var index = _messages.IndexOf(vm);
            if (index >= 0)
            {
                _messages[index] = BuildMessageVm(dto);
            }
        });
    }

    // ---------------- 小火人流式回复 ----------------
    private void OnAiStream(ChatAiStreamDto dto)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(dto.StreamId)
                || (!string.IsNullOrEmpty(dto.Room) && dto.Room != _room))
            {
                return;
            }
            if (_aiStreams.TryGetValue(dto.StreamId, out var vm))
            {
                if (dto.Phase == "delta")
                {
                    vm.Content += dto.Text;
                    RefreshStreamText(vm);
                    ScrollToBottom();
                }
                else if (dto.Phase == "error")
                {
                    vm.Content = dto.Text;
                    RefreshStreamText(vm);
                }
                return;
            }
            if (dto.Phase != "start")
            {
                return;
            }
            var name = string.IsNullOrWhiteSpace(dto.FromName) ? _aiName : dto.FromName;
            var streamVm = new ChatMessageVm
            {
                Id = -1,
                IsMine = false,
                Kind = "text",
                Content = string.IsNullOrEmpty(dto.Text) ? Loc.T("Chat_Ai_Thinking") : dto.Text,
                DividerVisibility = Visibility.Collapsed,
                MessageVisibility = Visibility.Visible,
                RowAlign = HorizontalAlignment.Left,
                BubbleBrush = _otherBubble,
                ContentForeground = _otherText,
                BubbleCorner = new CornerRadius(14, 14, 14, 5),
                BubbleVisibility = Visibility.Visible,
                TextVisibility = Visibility.Visible,
                SenderText = name,
                SenderVisibility = _room == "group" ? Visibility.Visible : Visibility.Collapsed,
                SenderLevelText = LevelTextOf(0),
                SenderLevelBrush = LevelBrushOf(0),
                SenderLevelVisibility = Visibility.Visible,
                AvatarText = (name.Length > 0 ? name[..1] : "?").ToUpperInvariant(),
                AvatarImage = string.IsNullOrWhiteSpace(dto.FromAvatar) ? null : SafeBitmap(dto.FromAvatar),
                AvatarBrush = AvatarBrushFor(dto.FromId),
                AvatarLeftVisibility = Visibility.Visible,
                AvatarRightVisibility = Visibility.Collapsed,
                TimeText = FormatTime(dto.CreatedAt),
                FlashBackground = _transparent
            };
            _aiStreams[dto.StreamId] = streamVm;
            _messages.Add(streamVm);
            EmptyText.Visibility = Visibility.Collapsed;
            ScrollToBottom();
        });
    }

    /// <summary>流式气泡内容变了：找到已实体的 TextBlock 重新铺 @高亮文本。</summary>
    private void RefreshStreamText(ChatMessageVm vm)
    {
        if (MessagesList.ContainerFromItem(vm) is not DependencyObject container)
        {
            return;
        }
        if (FindDescendantTextBlock(container, vm) is { } tb)
        {
            ApplyMentionRuns(tb);
        }
    }

    private static TextBlock? FindDescendantTextBlock(DependencyObject root, ChatMessageVm vm)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && ReferenceEquals(tb.DataContext, vm))
            {
                return tb;
            }
            if (FindDescendantTextBlock(child, vm) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void ClearAiStreams()
    {
        foreach (var vm in _aiStreams.Values)
        {
            _messages.Remove(vm);
        }
        _aiStreams.Clear();
    }

    // ---------------- 红包 ----------------
    private static string RedPacketTitleOf(ChatMessageDto dto)
    {
        var remark = dto.RedPacket?.Remark ?? string.Empty;
        return string.IsNullOrWhiteSpace(remark) ? Loc.T("Chat_RedPacket_Greeting") : remark;
    }

    private static string RedPacketSubOf(ChatMessageDto dto)
    {
        var p = dto.RedPacket;
        if (p is null)
        {
            return Loc.T("Chat_RedPacket_Loading");
        }
        var asset = string.IsNullOrWhiteSpace(p.AssetName) ? p.Asset : p.AssetName;
        return Loc.Tf("Chat_RedPacket_Sub_Format", asset, p.Count);
    }

    private static string RedPacketActionOf(ChatMessageDto dto)
    {
        var p = dto.RedPacket;
        if (p is null)
        {
            return Loc.T("Chat_RedPacket_Action_View");
        }
        if (p.Status is "done" or "expired")
        {
            return Loc.T("Chat_RedPacket_Status_Done");
        }
        if (p.MyAmount is { } mine)
        {
            return Loc.Tf("Chat_RedPacket_Action_Mine_Format", mine);
        }
        return p.CanGrab ? Loc.T("Chat_RedPacket_Action_Grab") : Loc.T("Chat_RedPacket_Action_View");
    }

    /// <summary>历史消息只带 redpacketId，按 id 补齐卡片信息。</summary>
    private async Task HydrateRedPacketsAsync()
    {
        var pending = _rawMessages
            .Where(m => m.EffectiveKind == "redpacket" && m.RedPacketId > 0 && m.RedPacket is null)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var room = _room;
        var changed = false;
        foreach (var dto in pending)
        {
            try
            {
                var res = await _chat.GetRedPacketAsync(dto.RedPacketId);
                if (res.Ok && res.Packet is not null)
                {
                    dto.RedPacket = res.Packet;
                    changed = true;
                }
            }
            catch
            {
                // 单个红包失败不影响其它卡片
            }
        }
        if (!changed)
        {
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (_room != room)
            {
                return;
            }
            for (var i = 0; i < _messages.Count; i++)
            {
                var vm = _messages[i];
                if (vm.IsDivider || vm.Id <= 0 || vm.Kind != "redpacket")
                {
                    continue;
                }
                var dto = _rawMessages.FirstOrDefault(m => m.Id == vm.Id);
                if (dto?.RedPacket is not null)
                {
                    _messages[i] = BuildMessageVm(dto);
                }
            }
        });
    }

    private void OnRedPacketChanged(long packetId, int grabbedCount, string status)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var dto = _rawMessages.FirstOrDefault(m => m.RedPacketId == packetId);
            if (dto?.RedPacket is not null)
            {
                dto.RedPacket.GrabbedCount = grabbedCount;
                if (!string.IsNullOrEmpty(status))
                {
                    dto.RedPacket.Status = status;
                }
                var vm = _messages.FirstOrDefault(m => !m.IsDivider && m.Id == dto.Id);
                if (vm is not null)
                {
                    vm.RedPacketAction = RedPacketActionOf(dto);
                }
            }
            if (RpOverlay.Visibility == Visibility.Visible && _rpCurrentId == packetId)
            {
                _ = LoadRedPacketAsync(packetId);
            }
        });
    }

    private async void RedPacketCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessageVm vm || vm.RedPacketId <= 0)
        {
            return;
        }
        await OpenRedPacketAsync(vm.RedPacketId);
    }

    private void RedPacket_Click(object sender, RoutedEventArgs e)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }
        BuildRpCombos();
        RpSendStatusText.Text = string.Empty;
        RpSendOverlay.Visibility = Visibility.Visible;
    }

    private void BuildRpCombos()
    {
        RpAssetBox.Items.Clear();
        RpAssetBox.Items.Add(Loc.T("Chat_RedPacket_Asset_Points"));
        if (_isAdmin)
        {
            RpAssetBox.Items.Add(Loc.T("Chat_RedPacket_Asset_Exp"));
        }
        RpAssetBox.SelectedIndex = 0;

        RpModeBox.Items.Clear();
        RpModeBox.Items.Add(Loc.T("Chat_RedPacket_Mode_Random"));
        RpModeBox.Items.Add(Loc.T("Chat_RedPacket_Mode_Fixed"));
        RpModeBox.SelectedIndex = 0;
    }

    private void RpMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RpPerBox is null || RpTotalBox is null)
        {
            return;
        }
        var fixedMode = RpModeBox.SelectedIndex == 1;
        RpPerBox.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        RpTotalBox.Visibility = fixedMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RpSend_Click(object sender, RoutedEventArgs e)
    {
        var asset = RpAssetBox.SelectedIndex == 1 ? "exp" : "points";
        var mode = RpModeBox.SelectedIndex == 1 ? "fixed" : "random";
        var count = ParseInt(RpCountBox.Text);
        var total = ParseInt(RpTotalBox.Text);
        var per = ParseInt(RpPerBox.Text);
        var remark = (RpRemarkBox.Text ?? string.Empty).Trim();
        if (count <= 0)
        {
            RpSendStatusText.Text = Loc.T("Chat_RedPacket_NeedCount");
            return;
        }
        if (mode == "fixed")
        {
            if (per <= 0)
            {
                RpSendStatusText.Text = Loc.T("Chat_RedPacket_NeedPer");
                return;
            }
            total = per * count;
        }
        else
        {
            if (total <= 0)
            {
                RpSendStatusText.Text = Loc.T("Chat_RedPacket_NeedTotal");
                return;
            }
            per = 0;
        }
        RpSendButton.IsEnabled = false;
        try
        {
            var res = await _chat.SendRedPacketAsync(asset, mode, total, per, count, remark);
            if (!res.Ok)
            {
                RpSendStatusText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                return;
            }
            RpSendOverlay.Visibility = Visibility.Collapsed;
            RpRemarkBox.Text = string.Empty;
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_RedPacket_Sent"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"发红包失败：{ex.Message}");
            RpSendStatusText.Text = Loc.T("Chat_Request_Failed");
        }
        finally
        {
            RpSendButton.IsEnabled = true;
        }
    }

    private async Task OpenRedPacketAsync(long id)
    {
        _rpCurrentId = id;
        RpOverlay.Visibility = Visibility.Visible;
        RpMyAmountText.Text = string.Empty;
        RpGrabButton.Visibility = Visibility.Collapsed;
        _rpGrabs.Clear();
        await LoadRedPacketAsync(id);
    }

    private async Task LoadRedPacketAsync(long id)
    {
        try
        {
            var res = await _chat.GetRedPacketAsync(id);
            if (!res.Ok || res.Packet is null)
            {
                RpInfoText.Text = res.Error ?? Loc.T("Chat_RedPacket_NotFound");
                return;
            }
            RenderRedPacket(res.Packet);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载红包失败：{ex.Message}");
            RpInfoText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private void RenderRedPacket(RedPacketBriefDto p)
    {
        RpTitleText.Text = string.IsNullOrWhiteSpace(p.Remark) ? Loc.T("Chat_RedPacket_Greeting") : p.Remark;
        RpSenderText.Text = Loc.Tf("Chat_RedPacket_From_Format", p.SenderName);
        var asset = string.IsNullOrWhiteSpace(p.AssetName) ? p.Asset : p.AssetName;
        var mode = string.IsNullOrWhiteSpace(p.ModeName) ? p.Mode : p.ModeName;
        RpInfoText.Text = Loc.Tf("Chat_RedPacket_Info_Format", asset, mode, p.Total, p.Count, p.GrabbedCount);
        _rpGrabs.Clear();
        foreach (var g in p.Grabs ?? [])
        {
            _rpGrabs.Add(new RedPacketGrabVm { Name = g.Name, AmountText = "+" + g.Amount });
        }
        if (p.MyAmount is { } mine)
        {
            RpMyAmountText.Text = Loc.Tf("Chat_RedPacket_Mine_Format", mine);
        }
        else
        {
            RpMyAmountText.Text = p.Status is "done" or "expired"
                ? Loc.T("Chat_RedPacket_Status_Done")
                : string.Empty;
        }
        RpGrabButton.Visibility = p.CanGrab ? Visibility.Visible : Visibility.Collapsed;
        RpGrabButton.IsEnabled = p.CanGrab;
    }

    private async void RpGrab_Click(object sender, RoutedEventArgs e)
    {
        if (_rpCurrentId <= 0)
        {
            return;
        }
        RpGrabButton.IsEnabled = false;
        try
        {
            var res = await _chat.GrabRedPacketAsync(_rpCurrentId);
            if (!res.Ok)
            {
                RpMyAmountText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                RpGrabButton.IsEnabled = true;
                return;
            }
            RpMyAmountText.Text = res.Already
                ? Loc.Tf("Chat_RedPacket_Mine_Format", res.Amount)
                : Loc.Tf("Chat_RedPacket_Grabbed_Format", res.Amount);
            if (res.Packet is not null)
            {
                RenderRedPacket(res.Packet);
                var dto = _rawMessages.FirstOrDefault(m => m.RedPacketId == _rpCurrentId);
                if (dto is not null)
                {
                    dto.RedPacket = res.Packet;
                    var vm = _messages.FirstOrDefault(m => !m.IsDivider && m.Id == dto.Id);
                    if (vm is not null)
                    {
                        vm.RedPacketAction = RedPacketActionOf(dto);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"领取红包失败：{ex.Message}");
            RpGrabButton.IsEnabled = true;
        }
    }

    private void CloseRp_Click(object sender, RoutedEventArgs e)
    {
        RpOverlay.Visibility = Visibility.Collapsed;
        _rpCurrentId = 0;
    }

    private void CloseRpSend_Click(object sender, RoutedEventArgs e)
        => RpSendOverlay.Visibility = Visibility.Collapsed;

    // ---------------- 玩法中心：积分 / 抽卡 / 等级 ----------------
    private async void Fun_Click(object sender, RoutedEventArgs e)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }
        FunOverlay.Visibility = Visibility.Visible;
        PointsStatusText.Text = string.Empty;
        GachaStatusText.Text = string.Empty;
        LevelStatusText.Text = string.Empty;
        await Task.WhenAll(LoadPointsAsync(), LoadGachaAsync(), LoadLevelAsync());
    }

    private void CloseFun_Click(object sender, RoutedEventArgs e)
        => FunOverlay.Visibility = Visibility.Collapsed;

    private async Task LoadPointsAsync()
    {
        try
        {
            var me = await _chat.GetPointsMeAsync();
            if (me.Ok)
            {
                PointsBalanceText.Text = Loc.Tf("Chat_Points_Balance_Format", me.Balance);
                PointsStreakText.Text = Loc.Tf("Chat_Points_Streak_Format", me.Streak);
                SigninButton.IsEnabled = !me.SignedToday;
                SigninButton.Content = Loc.T(me.SignedToday ? "Chat_Points_Signin_Done" : "Chat_Points_Signin");
                RenderDays(me.Days);
            }
            await LoadPointsItemsAsync();
            await LoadPointsLogsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载积分失败：{ex.Message}");
            PointsStatusText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private async Task LoadPointsItemsAsync()
    {
        var res = await _chat.GetPointsItemsAsync();
        _pointsItems.Clear();
        foreach (var it in res.Items ?? [])
        {
            _pointsItems.Add(new PointsItemVm
            {
                ItemId = it.Id,
                Name = it.Name,
                Description = it.Description,
                PriceText = Loc.Tf("Chat_Points_Price_Format", it.Price)
            });
        }
    }

    private async Task LoadPointsLogsAsync()
    {
        var res = await _chat.GetPointsLogsAsync();
        _pointsLogs.Clear();
        foreach (var l in res.Logs ?? [])
        {
            _pointsLogs.Add(new PointsLogVm
            {
                Title = l.Title,
                Delta = l.Delta,
                TimeText = FormatTime(l.CreatedAt)
            });
        }
    }

    private void RenderDays(PointsDayDto[]? days)
    {
        PointsDaysPanel.Children.Clear();
        foreach (var d in days ?? [])
        {
            var signed = string.Equals(d.Status, "signed", StringComparison.OrdinalIgnoreCase);
            var background = signed ? "#10B981" : d.Today ? "#F59E0B" : "#9CA3AF";
            var foreground = signed || d.Today ? "#FFFFFF" : "#111827";
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock
            {
                Text = d.Weekday,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushOf(foreground)
            });
            panel.Children.Add(new TextBlock
            {
                Text = signed ? "+" + d.Points : d.Label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushOf(foreground)
            });
            PointsDaysPanel.Children.Add(new Border
            {
                Width = 54,
                Padding = new Thickness(3, 6, 3, 6),
                CornerRadius = new CornerRadius(8),
                Background = BrushOf(background),
                Child = panel
            });
        }
    }

    private async void Signin_Click(object sender, RoutedEventArgs e)
    {
        SigninButton.IsEnabled = false;
        try
        {
            var res = await _chat.SigninAsync();
            if (!res.Ok)
            {
                PointsStatusText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                SigninButton.IsEnabled = true;
                return;
            }
            PointsStatusText.Text = res.Already
                ? Loc.T("Chat_Points_Already")
                : Loc.Tf("Chat_Points_Signed_Format", res.Points);
            if (res.Days is not null)
            {
                RenderDays(res.Days);
            }
            PointsBalanceText.Text = Loc.Tf("Chat_Points_Balance_Format", res.Balance);
            PointsStreakText.Text = Loc.Tf("Chat_Points_Streak_Format", res.Streak);
            SigninButton.Content = Loc.T("Chat_Points_Signin_Done");
            await LoadPointsLogsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"签到失败：{ex.Message}");
            PointsStatusText.Text = Loc.T("Chat_Request_Failed");
        }
        finally
        {
            SigninButton.IsEnabled = false;
        }
    }

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PointsItemVm vm)
        {
            return;
        }
        try
        {
            var res = await _chat.RedeemAsync(vm.ItemId);
            if (!res.Ok)
            {
                PointsStatusText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                return;
            }
            PointsStatusText.Text = Loc.Tf("Chat_Points_Redeemed_Format", vm.Name);
            PointsBalanceText.Text = Loc.Tf("Chat_Points_Balance_Format", res.Balance);
            await LoadPointsItemsAsync();
            await LoadPointsLogsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"兑换失败：{ex.Message}");
            PointsStatusText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        var amount = ParseInt(TransferAmountBox.Text);
        var to = (TransferToBox.Text ?? string.Empty).Trim();
        if (amount <= 0)
        {
            PointsStatusText.Text = Loc.T("Chat_Points_Amount_Invalid");
            return;
        }
        if (to.Length == 0)
        {
            PointsStatusText.Text = Loc.T("Chat_Points_To_Invalid");
            return;
        }
        try
        {
            var res = await _chat.TransferAsync(amount, to, string.Empty);
            if (!res.Ok)
            {
                PointsStatusText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                return;
            }
            PointsStatusText.Text = Loc.Tf("Chat_Points_Transferred_Format", amount, to);
            TransferAmountBox.Text = string.Empty;
            PointsBalanceText.Text = Loc.Tf("Chat_Points_Balance_Format", res.Balance);
            await LoadPointsLogsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"转账失败：{ex.Message}");
            PointsStatusText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private async Task LoadGachaAsync()
    {
        try
        {
            var info = await _chat.GetGachaInfoAsync();
            _gachaPools.Clear();
            foreach (var p in info.Pools ?? [])
            {
                _gachaPools.Add(new GachaPoolVm
                {
                    Id = p.Id,
                    Label = p.Name,
                    Cost1 = p.Cost1,
                    Cost10 = p.Cost10,
                    State = p.State
                });
            }
            GachaPoolBox.ItemsSource = _gachaPools;
            if (_gachaPools.Count == 0)
            {
                GachaInfoText.Text = info.Enabled
                    ? Loc.T("Chat_Gacha_Empty")
                    : Loc.T("Chat_Gacha_Disabled");
                GachaOneButton.IsEnabled = false;
                GachaTenButton.IsEnabled = false;
                return;
            }
            GachaOneButton.IsEnabled = true;
            GachaTenButton.IsEnabled = true;
            GachaPoolBox.SelectedIndex = 0;
            GachaInfoText.Text = Loc.Tf("Chat_Gacha_Balance_Format", info.Balance);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载卡池失败：{ex.Message}");
            GachaStatusText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private void GachaPool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GachaPoolBox.SelectedItem is not GachaPoolVm pool)
        {
            return;
        }
        GachaOneButton.Content = Loc.Tf("Chat_Gacha_One_Cost_Format", pool.Cost1);
        GachaTenButton.Content = Loc.Tf("Chat_Gacha_Ten_Cost_Format", pool.Cost10);
        if (pool.State is not null)
        {
            GachaInfoText.Text = Loc.Tf("Chat_Gacha_Pool_Format",
                pool.State.Pity5, pool.State.ToPity, pool.State.TotalDraws, pool.State.RareTotal);
        }
    }

    private async void GachaOne_Click(object sender, RoutedEventArgs e) => await DrawAsync(1);

    private async void GachaTen_Click(object sender, RoutedEventArgs e) => await DrawAsync(10);

    private async Task DrawAsync(int times)
    {
        if (GachaPoolBox.SelectedItem is not GachaPoolVm pool)
        {
            return;
        }
        GachaOneButton.IsEnabled = false;
        GachaTenButton.IsEnabled = false;
        try
        {
            var res = await _chat.DrawGachaAsync(pool.Id, times);
            if (!res.Ok)
            {
                GachaStatusText.Text = res.Error ?? Loc.T("Chat_Request_Failed");
                return;
            }
            _gachaResults.Clear();
            foreach (var r in res.Results ?? [])
            {
                _gachaResults.Add(new GachaResultVm
                {
                    Name = r.Name,
                    KindText = r.KindName,
                    RarityName = string.IsNullOrWhiteSpace(r.RarityName) ? r.Label : r.RarityName,
                    SubText = string.IsNullOrWhiteSpace(r.Label) ? r.KindName : r.Label,
                    RarityBrush = RarityBrushOf(r.Rarity)
                });
            }
            GachaStatusText.Text = Loc.Tf("Chat_Gacha_Cost_Format", res.Cost, res.Balance);
            if (res.State is not null)
            {
                pool.State = res.State;
                GachaInfoText.Text = Loc.Tf("Chat_Gacha_Pool_Format",
                    res.State.Pity5, res.State.ToPity, res.State.TotalDraws, res.State.RareTotal);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"抽卡失败：{ex.Message}");
            GachaStatusText.Text = Loc.T("Chat_Request_Failed");
        }
        finally
        {
            GachaOneButton.IsEnabled = true;
            GachaTenButton.IsEnabled = true;
        }
    }

    private static Brush RarityBrushOf(string rarity) => BrushOf(rarity switch
    {
        "limited" => "#F59E0B",
        "rare" => "#8B5CF6",
        "premium" => "#3B82F6",
        _ => "#64748B"
    });

    private async Task LoadLevelAsync()
    {
        try
        {
            var info = await _chat.GetLevelMeAsync();
            if (!info.Ok)
            {
                LevelStatusText.Text = info.Error ?? Loc.T("Chat_Request_Failed");
                return;
            }
            LevelBadgeText.Text = Loc.Tf("Chat_Level_Badge_Format", info.Level, info.MaxLevel);
            LevelExpText.Text = info.NextThreshold is { } next
                ? Loc.Tf("Chat_Level_Exp_Format", info.Exp, info.CurThreshold, next)
                : Loc.Tf("Chat_Level_Exp_Max_Format", info.Exp);
            LevelProgress.Value = Math.Clamp(info.Ratio, 0, 1);
            LevelRemainText.Text = info.IsMax
                ? Loc.T("Chat_Level_Max")
                : Loc.Tf("Chat_Level_Remain_Format", info.Remain);
            _levelSources.Clear();
            foreach (var s in info.Sources ?? [])
            {
                _levelSources.Add(new LevelSourceVm
                {
                    Name = s.Name,
                    ExpText = Loc.Tf("Chat_Level_Source_Exp_Format", s.Exp),
                    Desc = LevelSourceDesc(s)
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载等级失败：{ex.Message}");
            LevelStatusText.Text = Loc.T("Chat_Request_Failed");
        }
    }

    private static string LevelSourceDesc(LevelSourceDto s)
    {
        var parts = new List<string>();
        if (s.DailyCap > 0)
        {
            parts.Add(Loc.Tf("Chat_Level_Source_DailyCap_Format", s.DailyCap));
        }
        if (s.DailyCountCap > 0)
        {
            parts.Add(Loc.Tf("Chat_Level_Source_CountCap_Format", s.DailyCountCap));
        }
        if (s.OncePerDay)
        {
            parts.Add(Loc.T("Chat_Level_Source_Once"));
        }
        if (s.StreakDays > 0)
        {
            parts.Add(Loc.Tf("Chat_Level_Source_Streak_Format", s.StreakDays, s.StreakMultiplier));
        }
        return parts.Count == 0
            ? Loc.T(s.Enabled ? "Chat_Level_Source_On" : "Chat_Level_Source_Off")
            : string.Join(" · ", parts);
    }

    private static int ParseInt(string? text)
        => int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : 0;

    private void OnReadStateChanged(string room, int userId, string username, long lastReadId)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (room != _room)
            {
                return;
            }
            _readState[userId] = (username, lastReadId);
            RefreshReadLabels();
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
                UpdateRoomHeader();
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
            ConnText.Text = Loc.T(connected ? "Chat_Connected" : "Chat_Connecting");
            ConnDot.Fill = BrushOf(connected ? "#22C55E" : "#F59E0B");
            ConnBanner.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            if (connected)
            {
                _ = RefreshUnreadAsync();
            }
        });
    }

    // ---------------- 未读 / 已读 ----------------
    private async Task SafeMarkReadAsync(string room)
    {
        try
        {
            await _chat.MarkReadAsync(room);
            _unread.Remove(room);
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
            var unread = result.Unread ?? [];
            _dispatcher.TryEnqueue(() =>
            {
                _unread.Clear();
                foreach (var (key, value) in unread)
                {
                    _unread[key] = value;
                }
                _unread.Remove(_room);
                RebuildUsers();
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 搜索聊天记录 ----------------
    private void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchOverlay.Visibility = Visibility.Visible;
        BuildSearchOptions();
        SearchKwBox.Text = string.Empty;
        SearchTypeBox.SelectedIndex = 0;
        SearchSenderBox.SelectedIndex = 0;
        SearchFromPicker.Date = null;
        SearchToPicker.Date = null;
        SearchStatusText.Text = Loc.T("Chat_Search_Start");
        _searchResults.Clear();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => SearchOverlay.Visibility = Visibility.Collapsed;

    private void BuildSearchOptions()
    {
        if (SearchTypeBox.Items.Count == 0)
        {
            _searchTypes.AddRange(["", "text", "image", "video", "file"]);
            SearchTypeBox.Items.Add(Loc.T("Chat_Type_All"));
            SearchTypeBox.Items.Add(Loc.T("Chat_Type_Text"));
            SearchTypeBox.Items.Add(Loc.T("Chat_Type_Image"));
            SearchTypeBox.Items.Add(Loc.T("Chat_Type_Video"));
            SearchTypeBox.Items.Add(Loc.T("Chat_Type_File"));
            SearchTypeBox.SelectedIndex = 0;
        }

        if (SearchSenderBox.Items.Count == 0)
        {
            SearchSenderBox.Items.Add(Loc.T("Chat_Sender_All"));
            foreach (var u in _rawUsers.Select(DisplayNameOf).Where(n => n.Length > 0).Distinct())
            {
                SearchSenderBox.Items.Add(u);
            }
            SearchSenderBox.SelectedIndex = 0;
        }
    }

    private async void SearchKw_Changed(object sender, TextChangedEventArgs e) => await RunSearchAsync(delay: 300);

    private async void SearchType_Changed(object sender, SelectionChangedEventArgs e) => await RunSearchAsync(delay: 0);

    private async void SearchDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        => await RunSearchAsync(delay: 0);

    private async Task RunSearchAsync(int delay)
    {
        if (SearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cts.Token);
            }

            var keyword = (SearchKwBox.Text ?? string.Empty).Trim();
            var typeIndex = Math.Clamp(SearchTypeBox.SelectedIndex, 0, _searchTypes.Count - 1);
            var type = _searchTypes[typeIndex];
            var senderName = SearchSenderBox.SelectedIndex > 0
                ? SearchSenderBox.SelectedItem as string
                : null;

            if (keyword.Length == 0 && type.Length == 0 && senderName is null
                && SearchFromPicker.Date is null && SearchToPicker.Date is null)
            {
                _searchResults.Clear();
                SearchStatusText.Text = Loc.T("Chat_Search_Start");
                return;
            }

            SearchStatusText.Text = Loc.T("Chat_Searching");
            // 与网页端一致：起止都按本地时区的当天 00:00 / 次日 00:00 前 1ms
            var from = SearchFromPicker.Date is { } f
                ? new DateTimeOffset(f.Date).ToUnixTimeMilliseconds()
                : 0;
            var to = SearchToPicker.Date is { } t
                ? new DateTimeOffset(t.Date).AddDays(1).ToUnixTimeMilliseconds() - 1
                : 0;

            var result = await _chat.SearchAsync(keyword, type, senderName, _room, from, to, 50, 0, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            if (!result.Ok)
            {
                SearchStatusText.Text = result.Error ?? Loc.T("Chat_Search_Failed");
                return;
            }

            var list = result.Messages ?? [];
            _searchResults.Clear();
            foreach (var m in list)
            {
                _searchResults.Add(new SearchResultVm
                {
                    Name = m.FromName,
                    RoomName = string.IsNullOrWhiteSpace(m.RoomName) ? string.Empty : m.RoomName!,
                    TimeText = FormatTime(m.CreatedAt),
                    Preview = PreviewOf(m),
                    Message = m
                });
            }
            SearchStatusText.Text = list.Length == 0
                ? Loc.T("Chat_Search_None")
                : Loc.Tf("Chat_Search_Count", list.Length, result.HasMore ? "+" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // 新一搜索已取代本次
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = Loc.T("Chat_Search_Failed");
            AppLog.Warn($"搜索聊天记录失败：{ex.Message}");
        }
    }

    private static string PreviewOf(ChatMessageDto m) => m.EffectiveKind switch
    {
        "image" => "[图片]",
        "video" => "[视频] " + (m.FileName ?? string.Empty),
        "file" => "[文件] " + (m.FileName ?? string.Empty),
        "sticker" => "[表情]",
        _ => m.Content
    };

    private async void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultVm vm || vm.Message is null)
        {
            return;
        }
        var message = vm.Message;
        var room = string.IsNullOrWhiteSpace(message.Room) ? "group" : message.Room;

        if (room == _room)
        {
            CloseSearch_Click(sender, new RoutedEventArgs());
            if (!FocusMessage(message.Id))
            {
                await LoadHistoryAsync(_room, message.Id);
            }
            return;
        }
        CloseSearch_Click(sender, new RoutedEventArgs());
        await OpenRoomAsync(room, message.Id);
    }

    // ---------------- 个人资料 ----------------
    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_myName))
        {
            return;
        }
        _profileLoading = true;
        ProfileNameText.Text = _myName;
        ProfileUsernameText.Text = "@" + _myUsername;
        ProfileLevelText.Text = LevelTextOf(_myLevel);
        ProfileExpText.Text = Loc.Tf("Chat_Profile_Exp", _myExp);
        ProfileAvatarText.Text = _myName[..1].ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(_myAvatar))
        {
            ProfileAvatarImage.Source = new BitmapImage(new Uri(Absolute(_myAvatar)));
            ProfileAvatarImage.Visibility = Visibility.Visible;
        }
        else
        {
            ProfileAvatarImage.Visibility = Visibility.Collapsed;
        }
        ProfileNickBox.Text = _myName;
        ProfileOldPwdBox.Password = string.Empty;
        ProfileNewPwdBox.Password = string.Empty;

        var settings = AppState.SettingsService.Load();
        NotifyToggle.IsOn = settings.ChatNotifyEnabled;
        var summary = string.Equals(settings.ChatNotifyMode, "summary", StringComparison.OrdinalIgnoreCase);
        NotifySummaryRadio.IsChecked = summary;
        NotifyDetailRadio.IsChecked = !summary;

        ProfileOverlay.Visibility = Visibility.Visible;
        _profileLoading = false;
    }

    private void CloseProfile_Click(object sender, RoutedEventArgs e) => CloseProfile();

    private void CloseProfile() => ProfileOverlay.Visibility = Visibility.Collapsed;

    private async void SaveNickname_Click(object sender, RoutedEventArgs e)
    {
        var nickname = (ProfileNickBox.Text ?? string.Empty).Trim();
        if (nickname.Length == 0)
        {
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_NickEmpty"), InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var resp = await _chat.UpdateNicknameAsync(nickname);
            if (!resp.Ok)
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Save_Failed"), InfoBarSeverity.Warning);
                return;
            }
            _myName = nickname;
            ProfileNameText.Text = nickname;
            ProfileAvatarText.Text = nickname[..1].ToUpperInvariant();
            UpdateProfileButton();
            _rawUsers = await _chat.GetUsersAsync();
            RebuildUsers();
            RebuildMessages();
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_NickSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("修改昵称失败。", ex);
        }
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var oldPassword = ProfileOldPwdBox.Password;
        var newPassword = ProfileNewPwdBox.Password;
        if (string.IsNullOrEmpty(oldPassword) || string.IsNullOrEmpty(newPassword))
        {
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_PwdEmpty"), InfoBarSeverity.Warning);
            return;
        }
        if (newPassword.Length < 6)
        {
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_PwdShort"), InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var resp = await _chat.ChangePasswordAsync(oldPassword, newPassword);
            if (!resp.Ok)
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Save_Failed"), InfoBarSeverity.Warning);
                return;
            }
            ProfileOldPwdBox.Password = string.Empty;
            ProfileNewPwdBox.Password = string.Empty;
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_PwdSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("修改密码失败。", ex);
        }
    }

    private async void UploadAvatar_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickAsync([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"]);
        if (file is null)
        {
            return;
        }
        try
        {
            if (new FileInfo(file.Path).Length > 2L * 1024 * 1024)
            {
                MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_AvatarTooLarge"), InfoBarSeverity.Warning);
                return;
            }
            var resp = await _chat.UploadAvatarAsync(file.Path);
            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Avatar))
            {
                MainWindow.Instance?.ShowStatus(resp.Error ?? Loc.T("Chat_Upload_Failed"), InfoBarSeverity.Warning);
                return;
            }
            _myAvatar = resp.Avatar!;
            ProfileAvatarImage.Source = new BitmapImage(new Uri(Absolute(_myAvatar)));
            ProfileAvatarImage.Visibility = Visibility.Visible;
            UpdateProfileButton();
            _rawUsers = await _chat.GetUsersAsync();
            RebuildUsers();
            RebuildMessages();
            MainWindow.Instance?.ShowStatus(Loc.T("Chat_Profile_AvatarSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("上传头像失败。", ex);
        }
    }

    private void NotifyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_profileLoading)
        {
            return;
        }
        var settings = AppState.SettingsService.Load();
        settings.ChatNotifyEnabled = NotifyToggle.IsOn;
        AppState.SettingsService.Save(settings);
    }

    private void NotifyMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_profileLoading)
        {
            return;
        }
        var settings = AppState.SettingsService.Load();
        settings.ChatNotifyMode = NotifySummaryRadio.IsChecked == true ? "summary" : "detail";
        AppState.SettingsService.Save(settings);
    }

    // ---------------- 图片灯箱 ----------------
    private void ShowImageViewer(string url, string name)
    {
        ViewerImage.Source = SafeBitmap(url);
        ViewerTitle.Text = name;
        ImageViewer.Visibility = Visibility.Visible;
        ViewerScroll.ChangeView(0, 0, 1, disableAnimation: true);
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e)
    {
        ImageViewer.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
    }

    private void ViewerEsc_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ImageViewer.Visibility == Visibility.Visible)
        {
            CloseViewer_Click(this, new RoutedEventArgs());
            args.Handled = true;
        }
        else if (RpSendOverlay.Visibility == Visibility.Visible)
        {
            RpSendOverlay.Visibility = Visibility.Collapsed;
            args.Handled = true;
        }
        else if (RpOverlay.Visibility == Visibility.Visible)
        {
            CloseRp_Click(this, new RoutedEventArgs());
            args.Handled = true;
        }
        else if (FunOverlay.Visibility == Visibility.Visible)
        {
            FunOverlay.Visibility = Visibility.Collapsed;
            args.Handled = true;
        }
        else if (ProfileOverlay.Visibility == Visibility.Visible)
        {
            CloseProfile();
            args.Handled = true;
        }
        else if (SearchOverlay.Visibility == Visibility.Visible)
        {
            SearchOverlay.Visibility = Visibility.Collapsed;
            args.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ViewerScroll.ChangeView(null, null, (float)Math.Min(6, ViewerScroll.ZoomFactor * 1.25), false);

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ViewerScroll.ChangeView(null, null, (float)Math.Max(0.5, ViewerScroll.ZoomFactor / 1.25), false);

    private async void ViewerSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (ImageViewer.Visibility != Visibility.Visible)
        {
            return;
        }
        await SaveFileAsync(LastViewerUrl, ViewerTitle.Text);
    }

    private string LastViewerUrl => (ViewerImage.Source as BitmapImage)?.UriSource?.AbsoluteUri ?? string.Empty;

    // ---------------- 今天吃什么 ----------------
    private async void Eat_Click(object sender, RoutedEventArgs e)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }
        var url = SessionState.OnlineBaseUrl.TrimEnd('/')
                  + "/api/autologin?jwt=" + Uri.EscapeDataString(SessionState.Token!)
                  + "&redirect=/eat.html";
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开「今天吃什么」失败：{ex.Message}");
            MainWindow.Instance?.ShowStatus(Loc.T("AppState_Url_OpenFailed"), InfoBarSeverity.Warning);
        }
    }
}

/// <summary>消息列表项视图模型（日期分隔 + 消息两种形态共用）。</summary>
public sealed class ChatMessageVm : INotifyPropertyChanged
{
    private string _readText = string.Empty;
    private Brush _flashBackground = null!;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>模板内文本绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    public long Id { get; set; }
    public bool IsDivider { get; set; }
    public bool IsMine { get; set; }
    public string Kind { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public string ReEditContent { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileLabel { get; set; } = string.Empty;
    public string FileGlyph { get; set; } = string.Empty;
    public ImageSource? Image { get; set; }
    public double ImageMaxSize { get; set; } = 360;

    // 布局
    public Visibility DividerVisibility { get; set; } = Visibility.Collapsed;
    public Visibility MessageVisibility { get; set; } = Visibility.Visible;
    public string DividerText { get; set; } = string.Empty;
    public HorizontalAlignment RowAlign { get; set; }
    public Brush BubbleBrush { get; set; } = null!;
    public CornerRadius BubbleCorner { get; set; } = new(14);
    public Brush ContentForeground { get; set; } = null!;

    // 内容
    public Visibility BubbleVisibility { get; set; } = Visibility.Visible;
    public Visibility TextVisibility { get; set; } = Visibility.Visible;
    public Visibility ImageVisibility { get; set; } = Visibility.Collapsed;
    public Visibility FileVisibility { get; set; } = Visibility.Collapsed;

    // 撤回
    public string RecallText { get; set; } = string.Empty;
    public Visibility RecallVisibility { get; set; } = Visibility.Collapsed;
    public Visibility EditVisibility { get; set; } = Visibility.Collapsed;
    public string AdminNoteText { get; set; } = string.Empty;
    public Visibility AdminNoteVisibility { get; set; } = Visibility.Collapsed;
    public Visibility RecallButtonVisibility { get; set; } = Visibility.Collapsed;

    // 发送人 / 时间 / 已读
    public string SenderText { get; set; } = string.Empty;
    public Visibility SenderVisibility { get; set; } = Visibility.Collapsed;
    public string SenderLevelText { get; set; } = string.Empty;
    public Brush SenderLevelBrush { get; set; } = null!;
    public Visibility SenderLevelVisibility { get; set; } = Visibility.Collapsed;
    public string TimeText { get; set; } = string.Empty;

    public string ReadText
    {
        get => _readText;
        set
        {
            if (_readText == value)
            {
                return;
            }
            _readText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadVisibility)));
        }
    }

    public Visibility ReadVisibility => _readText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Brush FlashBackground
    {
        get => _flashBackground;
        set
        {
            _flashBackground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlashBackground)));
        }
    }

    // 头像
    public string AvatarText { get; set; } = string.Empty;
    public ImageSource? AvatarImage { get; set; }
    public Brush AvatarBrush { get; set; } = null!;
    public Visibility AvatarLeftVisibility { get; set; } = Visibility.Collapsed;
    public Visibility AvatarRightVisibility { get; set; } = Visibility.Collapsed;

    // 红包卡片
    public long RedPacketId { get; set; }
    public Visibility RedPacketVisibility { get; set; } = Visibility.Collapsed;
    public string RedPacketTitle { get; set; } = string.Empty;
    public string RedPacketSub { get; set; } = string.Empty;

    private string _redPacketAction = string.Empty;

    public string RedPacketAction
    {
        get => _redPacketAction;
        set
        {
            if (_redPacketAction == value)
            {
                return;
            }
            _redPacketAction = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RedPacketAction)));
        }
    }

    public static ChatMessageVm Divider(string label) => new()
    {
        IsDivider = true,
        DividerText = label,
        DividerVisibility = Visibility.Visible,
        MessageVisibility = Visibility.Collapsed,
        FlashBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
    };
}

/// <summary>积分商城商品项。</summary>
public sealed class PointsItemVm
{
    internal LocalizedStrings Strings => Loc.Strings;

    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PriceText { get; set; } = string.Empty;
}

/// <summary>积分明细项。</summary>
public sealed class PointsLogVm
{
    public string Title { get; set; } = string.Empty;
    public int Delta { get; set; }
    public string TimeText { get; set; } = string.Empty;

    public string DeltaText => Delta >= 0 ? "+" + Delta : Delta.ToString();

    public Brush DeltaBrush => new SolidColorBrush(Delta >= 0
        ? Windows.UI.Color.FromArgb(0xFF, 0x10, 0xB9, 0x81)
        : Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
}

/// <summary>抽卡结果项。</summary>
public sealed class GachaResultVm
{
    public string Name { get; set; } = string.Empty;
    public string KindText { get; set; } = string.Empty;
    public string RarityName { get; set; } = string.Empty;
    public string SubText { get; set; } = string.Empty;
    public Brush RarityBrush { get; set; } = null!;
}

/// <summary>等级经验来源说明项。</summary>
public sealed class LevelSourceVm
{
    public string Name { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public string ExpText { get; set; } = string.Empty;
}

/// <summary>红包领取记录项。</summary>
public sealed class RedPacketGrabVm
{
    public string Name { get; set; } = string.Empty;
    public string AmountText { get; set; } = string.Empty;
}

/// <summary>抽卡卡池下拉项。</summary>
public sealed class GachaPoolVm
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Cost1 { get; set; }
    public int Cost10 { get; set; }

    internal GachaStateDto? State { get; set; }
}

/// <summary>表情包列表项视图模型。</summary>
public sealed class StickerVm
{
    internal StickerVm(long id, ImageSource? image, int sendCount, bool canDelete)
    {
        Id = id;
        Image = image;
        SendCount = sendCount;
        CanDelete = canDelete;
    }

    public long Id { get; }

    public ImageSource? Image { get; }

    public int SendCount { get; }

    public bool CanDelete { get; }

    public Visibility DeleteVisibility => CanDelete ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>左侧联系人/分组条目（分组标题与联系人行共用一套模板，靠可见性切换）。</summary>
public sealed class SidebarItemVm
{
    public string Title { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SubText { get; set; } = string.Empty;
    public string TailText { get; set; } = string.Empty;
    public string UnreadText { get; set; } = string.Empty;
    public Visibility UnreadVisibility { get; set; } = Visibility.Collapsed;
    public Visibility OnlineDotVisibility { get; set; } = Visibility.Collapsed;
    public Visibility AiVisibility { get; set; } = Visibility.Collapsed;
    public string LevelText { get; set; } = string.Empty;
    public Brush LevelBrush { get; set; } = null!;
    public Visibility LevelVisibility { get; set; } = Visibility.Collapsed;
    public Visibility HeaderVisibility { get; set; } = Visibility.Collapsed;
    public Visibility RowVisibility { get; set; } = Visibility.Visible;
    public Brush RowBackground { get; set; } = null!;
    public string AvatarText { get; set; } = string.Empty;
    public ImageSource? AvatarImage { get; set; }
    public Brush AvatarBrush { get; set; } = null!;
    public string Room { get; set; } = string.Empty;
    public int PeerId { get; set; }
    public bool IsHeader { get; set; }

    public static SidebarItemVm Header(string title) => new()
    {
        Title = title,
        HeaderVisibility = Visibility.Visible,
        RowVisibility = Visibility.Collapsed
    };

    /// <summary>空提示行：借用分组标题样式，点击无动作。</summary>
    public static SidebarItemVm Empty(string text) => new()
    {
        Title = text,
        HeaderVisibility = Visibility.Visible,
        RowVisibility = Visibility.Collapsed,
        IsHeader = true
    };
}

/// <summary>搜索结果项。</summary>
public sealed class SearchResultVm
{
    public string Name { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string TimeText { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;

    /// <summary>原始消息（仅页面内部使用，不参与 XAML 绑定）。</summary>
    internal ChatMessageDto? Message { get; set; }
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
