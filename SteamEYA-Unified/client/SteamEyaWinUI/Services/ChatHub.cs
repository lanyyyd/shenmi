using Microsoft.UI.Xaml;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 聊天室常驻服务：登录成功后即建立并保持 WebSocket 连接，用户切到其它页面（登录/云游戏/设置…）
/// 也能继续收消息并弹系统通知 + 闪烁任务栏。
///
/// 已读语义：本服务只收消息、不提交已读；只有用户真正停在聊天室页并查看对应房间时，
/// 才由 <c>ChatPage</c> 调用 /api/read 提交已读。
/// </summary>
internal static class ChatHub
{
    private static readonly ChatService Instance = new();
    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private static bool _wired;
    private static int _myId;

    public static ChatService Chat => Instance;

    /// <summary>聊天室页是否打开（由 ChatPage 维护，用于抑制“正在看”时的通知）。</summary>
    public static bool IsChatPageOpen { get; set; }

    /// <summary>聊天室页当前查看的房间（由 ChatPage 维护）。</summary>
    public static string CurrentRoom { get; set; } = "group";

    /// <summary>待打开的房间：点击系统通知后若聊天室页尚未打开，ChatPage 加载时据此进入对应房间。</summary>
    public static string? PendingRoom { get; set; }

    /// <summary>请求打开某房间（点击通知时触发）；聊天室页已打开则立即切房，否则等下次加载。</summary>
    public static event Action<string>? RoomOpenRequested;

    public static void RequestOpenRoom(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return;
        }
        PendingRoom = room;
        RoomOpenRequested?.Invoke(room);
    }

    /// <summary>由 MainWindow 创建时注入 UI 线程派发器（通知需要回到 UI 线程）。</summary>
    public static void AttachUi(Microsoft.UI.Dispatching.DispatcherQueue ui) => _ui = ui;

    /// <summary>当前登录用户 id（供判断“是不是自己发的”）。</summary>
    public static void SetMyId(int id) => _myId = id;

    /// <summary>
    /// 建立常驻连接（幂等）。登录成功后调用。
    /// 离线/局域网模式同样适用：此时 BaseUrl 指向本机网关，用它签发的 JWT 连本机 /ws。
    /// </summary>
    public static void EnsureStarted()
    {
        if (string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }

        Wire();
        if (_ui is { } ui)
        {
            ui.TryEnqueue(NotificationService.Initialize);
        }
        else
        {
            NotificationService.Initialize();
        }

        Instance.Start();
        _ = LoadMyIdAsync();
    }

    /// <summary>退出登录 / 切换模式时停掉连接。</summary>
    public static void Shutdown()
    {
        _myId = 0;
        _ = Instance.StopAsync();
    }

    /// <summary>发送「保队友」指令：服务端会广播给所有在线客户端（含自己），收到后立即退出本软件。</summary>
    public static Task<bool> SendPanicAsync() => Instance.SendPanicAsync();

    private static void Wire()
    {
        if (_wired)
        {
            return;
        }
        _wired = true;
        Instance.MessageReceived += OnMessageReceived;
        Instance.PanicReceived += OnPanicReceived;
    }

    private static void OnPanicReceived()
    {
        if (_ui is { } ui)
        {
            ui.TryEnqueue(ForceExit);
        }
        else
        {
            ForceExit();
        }
    }

    /// <summary>「保队友」：立即结束本软件。先分离极域并结束内置助手，Application.Exit 不保证触发 Closed 清理。</summary>
    private static void ForceExit()
    {
        try
        {
            JiYuService.Stop();
        }
        catch
        {
            // 退出路径不因清理失败而卡住
        }

        var app = Application.Current;
        if (app is not null)
        {
            app.Exit();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    private static async Task LoadMyIdAsync()
    {
        if (_myId != 0)
        {
            return;
        }
        try
        {
            var me = await Instance.GetMeAsync();
            _myId = me?.Id ?? 0;
        }
        catch
        {
            // 取不到身份不影响收消息，只是可能对自己发的不做抑制。
        }
    }

    private static void OnMessageReceived(ChatMessageDto dto)
    {
        if (dto.Recalled || (_myId != 0 && dto.FromId == _myId))
        {
            return;
        }

        // 正在聊天室页、窗口在前台、且正是当前房间 —— 用户看得到，不必打扰。
        if (IsChatPageOpen && MainWindow.IsActive && dto.Room == CurrentRoom)
        {
            return;
        }

        if (_ui is { } ui)
        {
            ui.TryEnqueue(() => Notify(dto));
        }
        else
        {
            Notify(dto);
        }
    }

    private static int _pendingCount;

    /// <summary>用户回到聊天室页 / 正在查看时清零「你有 N 条新消息」计数。</summary>
    public static void ResetPendingNotifications() => _pendingCount = 0;

    private static void Notify(ChatMessageDto dto)
    {
        // 个人中心里的通知偏好在每次弹窗前读取，改动即时生效。
        var settings = AppState.SettingsService.Load();
        if (!settings.ChatNotifyEnabled)
        {
            return;
        }

        _pendingCount++;
        if (string.Equals(settings.ChatNotifyMode, "summary", StringComparison.OrdinalIgnoreCase))
        {
            var summary = Loc.Tf("Chat_Notify_Summary", _pendingCount);
            NotificationService.NotifyChat(Loc.T("Chat_Notify_Title"), summary, dto.Room);
            return;
        }

        var preview = dto.EffectiveKind switch
        {
            "image" => "[图片]",
            "video" => "[视频]",
            "file" => "[文件] " + (dto.FileName ?? string.Empty),
            "sticker" => "[表情]",
            _ => dto.Content
        };
        if (preview.Length > 80)
        {
            preview = preview[..80] + "…";
        }

        var title = Loc.Tf(dto.Room == "group" ? "Chat_Notify_Group" : "Chat_Notify_Private", dto.FromName);
        AppLog.Debug($"收到聊天消息：room={dto.Room} from={dto.FromName} kind={dto.EffectiveKind} len={preview.Length}");
        NotificationService.NotifyChat(title, preview, dto.Room);   // 带房间参数，点击通知可直接跳到该房间
    }
}
