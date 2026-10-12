using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 原生聊天室页面：Group/私聊、文本/图片/文件/视频、撤回、在线状态与「上次登录时间」、
/// 以及局域网互传面板。用原生 WinUI3 控件取代原先的 WebView2 内嵌方案，
/// 以修复部分 Win10 上点开聊天室卡死的问题。
/// </summary>
public sealed partial class ChatPage : Page, INotifyPropertyChanged
{
    private static readonly Brush MyBubble = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private static readonly Brush OtherBubble = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
    private static readonly Brush OnAccentBrush = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
    private static readonly Brush PrimaryTextBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private readonly ChatService _chat = new();
    // 同时 using 了 Microsoft.UI.Dispatching 与 Windows.System，两者都有 DispatcherQueue，必须写全名消歧义。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<ChatMessageVm> _messages = [];
    private readonly ObservableCollection<ChatUserVm> _users = [];
    private readonly ObservableCollection<LanPeerVm> _peers = [];

    private readonly List<ChatMessageDto> _rawMessages = [];
    private ChatUserDto[] _rawUsers = [];
    private Dictionary<string, int> _unread = [];
    private int _myId;
    private string _room = "group";
    private bool _initialized;

    public ChatPage()
    {
        InitializeComponent();

        MessagesList.ItemsSource = _messages;
        UsersList.ItemsSource = _users;
        LanPeersList.ItemsSource = _peers;

        _chat.MessageReceived += OnMessageReceived;
        _chat.MessageRecalled += OnMessageRecalled;
        _chat.UsersChanged += OnUsersChanged;
        _chat.ConnectionChanged += OnConnectionChanged;

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

        ApplyStaticText();

        try
        {
            var me = await _chat.GetMeAsync();
            _myId = me?.Id ?? 0;
            _rawUsers = await _chat.GetUsersAsync();
            RebuildUsers();
            await OpenRoomAsync("group");
            _chat.Start();
            await RefreshUnreadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("聊天室初始化失败。", ex);
        }

        _ = LoadLanAsync();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        await _chat.DisposeAsync();
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
        AddPeerText.Text = Loc.T("Chat_Lan_Add");
        RoomTitle.Text = _room == "group" ? Loc.T("Chat_Group") : RoomTitle.Text;
        SetConnectionText(_chat.IsConnected);
    }

    // ---------------- 会话切换 ----------------
    private async Task OpenRoomAsync(string room)
    {
        _room = room;
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

    private async void InputBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            e.Handled = true;
            await SendTextAsync();
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
        await _chat.SendChatAsync("text", text, to: TargetId());
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

    // ---------------- 撤回 / 附件 ----------------
    private async void Recall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageVm vm)
        {
            await _chat.RecallAsync(vm.Id);
        }
    }

    private async void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageVm vm && !string.IsNullOrEmpty(vm.FileUrl))
        {
            await Launcher.LaunchUriAsync(new Uri(vm.FileUrl));
        }
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
        foreach (var u in _rawUsers.OrderByDescending(u => u.Online).ThenBy(u => u.DisplayName))
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
                : $"{dto.FromName} · {FormatTime(dto.CreatedAt)}",
            RowAlign = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = isMine ? MyBubble : OtherBubble,
            ContentForeground = isMine ? OnAccentBrush : PrimaryTextBrush,
            RecallLabel = Loc.T("Chat_Recall"),
            RecalledLabel = Loc.T("Chat_Recalled")
        };
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

    /// <summary>滚动到最新一条消息。</summary>
    private void ScrollToBottom()
    {
        if (_messages.Count > 0)
        {
            MessagesList.ScrollIntoView(_messages[^1]);
        }
    }

    // ---------------- 局域网互传 ----------------
    private async Task LoadLanAsync()
    {
        var info = await _chat.GetLanInfoAsync();
        if (!info.Enabled)
        {
            // 在线模式下本机网关默认没起：这里补拉一次以启用局域网发现，让在线/离线都能互传。
            await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname);
            info = await _chat.GetLanInfoAsync();
        }

        _dispatcher.TryEnqueue(() =>
        {
            LanSelfText.Text = info.Enabled
                ? Loc.Tf("Chat_Lan_Self", info.Name, info.Port ?? 0)
                : Loc.T("Chat_Lan_Off");
        });

        await RefreshPeersAsync();
    }

    private async Task RefreshPeersAsync()
    {
        var peers = await _chat.GetLanPeersAsync();
        _dispatcher.TryEnqueue(() =>
        {
            _peers.Clear();
            foreach (var p in peers.Peers ?? [])
            {
                _peers.Add(new LanPeerVm(p));
            }
            LanEmptyText.Text = _peers.Count == 0 ? Loc.T("Chat_Lan_None") : string.Empty;
        });
    }

    private async void RefreshLan_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPeersAsync();
    }

    private async void AddPeer_Click(object sender, RoutedEventArgs e)
    {
        var ipBox = new TextBox { Header = Loc.T("Chat_Lan_Ip"), PlaceholderText = "192.168.1.10" };
        var portBox = new TextBox { Header = Loc.T("Chat_Lan_Port"), Text = "8898" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Chat_Lan_Add"),
            Content = new StackPanel { Spacing = 8, Children = { ipBox, portBox } },
            PrimaryButtonText = Loc.T("Chat_Lan_Add"),
            CloseButtonText = Loc.T("Chat_Cancel")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (int.TryParse(portBox.Text, out var port) && !string.IsNullOrWhiteSpace(ipBox.Text))
        {
            await _chat.AddLanPeerAsync(ipBox.Text.Trim(), port, string.Empty);
            await RefreshPeersAsync();
        }
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

    public Visibility TextVisibility =>
        Recalled || Kind == "text" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageVisibility =>
        !Recalled && Image is not null ? Visibility.Visible : Visibility.Collapsed;

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

/// <summary>局域网对端设备视图模型。</summary>
public sealed class LanPeerVm
{
    private readonly LanPeerDto _peer;

    // LanPeerDto 是 internal，构造函数只能是 internal，否则 CS0051（参数类型可见性低于方法）。
    internal LanPeerVm(LanPeerDto peer)
    {
        _peer = peer;
    }

    public string Name => string.IsNullOrWhiteSpace(_peer.Name) ? _peer.Ip : _peer.Name;

    public string Endpoint => $"{_peer.Ip}:{_peer.Port}";
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
