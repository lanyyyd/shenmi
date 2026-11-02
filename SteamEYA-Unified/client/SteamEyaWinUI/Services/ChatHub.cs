using Microsoft.UI.Xaml;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    /// <summary>模式切换后重启常驻连接：先等旧连接真正关闭，再按新的 BaseUrl / Token / 地址重连。</summary>
    public static async Task RestartAsync()
    {
        _myId = 0;
        await Instance.StopAsync();
        if (string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }
        Instance.Start();
        _ = LoadMyIdAsync();
    }

    /// <summary>发送「保队友」指令：在线走服务器广播，离线走局域网转发。收到后立即退出本软件。</summary>
    public static async Task<bool> SendPanicAsync()
    {
        // 在线模式且已连上服务器：走服务器广播；离线（含在线断网）走本机网关，由它广播并转发同网段。
        if (!SessionState.Offline && Instance.IsConnected)
        {
            return await Instance.SendPanicAsync();
        }

        var sent = await SendLanControlAsync("panic", null);
        if (sent && !Instance.IsConnected)
        {
            // 未连本机网关的 WS 时收不到自己的广播，让发起者也执行一次。
            OnPanicReceived();
        }
        return sent;
    }

    /// <summary>下发全员在线/离线切换指令：在线走服务器广播，离线走局域网转发。</summary>
    public static async Task<bool> SendModeCommandAsync(string mode)
    {
        // 在线模式且已连上服务器：走服务器广播；离线（含在线断网）走本机网关，由它广播并转发同网段。
        if (!SessionState.Offline && Instance.IsConnected)
        {
            return await Instance.SendModeAsync(mode);
        }

        var sent = await SendLanControlAsync("mode", mode);
        if (sent && !Instance.IsConnected)
        {
            _ = NetworkModeService.HandleRemoteModeAsync(mode);
        }
        return sent;
    }

    private static void Wire()
    {
        if (_wired)
        {
            return;
        }
        _wired = true;
        Instance.MessageReceived += OnMessageReceived;
        Instance.PanicReceived += OnPanicReceived;
        Instance.ModeReceived += OnModeReceived;
    }

    private static void OnModeReceived(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        if (_ui is { } ui)
        {
            ui.TryEnqueue(() => _ = NetworkModeService.HandleRemoteModeAsync(mode));
        }
        else
        {
            _ = NetworkModeService.HandleRemoteModeAsync(mode);
        }
    }

    private static void OnPanicReceived()
    {
        if (_ui is { } ui)
        {
            ui.TryEnqueue(ExecutePanic);
        }
        else
        {
            ExecutePanic();
        }
    }

    /// <summary>避免在线广播与本地兜底同时触发两次退出流程。</summary>
    private static bool _panicExecuting;

    /// <summary>
    /// 「保队友」：先把本机极域广播窗口拉成全屏并激活（每个收到指令的客户端各自执行），
    /// 短暂等待窗口生效后，结束内置极域助手并退出本软件。
    /// </summary>
    private static async void ExecutePanic()
    {
        if (_panicExecuting)
        {
            return;
        }
        _panicExecuting = true;

        try
        {
            if (!AppEdition.IsNoJiYu)
            {
                // 放后台线程：注入侧可能卡住，不能阻塞退出流程。
                await Task.Run(() => JiYuService.TriggerBossKey());
            }
        }
        catch
        {
            // 未注入/无可用极域时仍然退出
        }

        await Task.Delay(300);
        ForceExit();
    }

    /// <summary>「保队友」：立即结束本软件。先结束内置助手（不分离注入，避免撤销刚刚的全屏）。</summary>
    private static void ForceExit()
    {
        try
        {
            if (!AppEdition.IsNoJiYu)
            {
                JiYuService.Stop(detach: false);
            }
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

    /// <summary>在线连接不可用时，把指令交给本机网关，由它广播给本客户端并转发给同网段对端。</summary>
    private static async Task<bool> SendLanControlAsync(string action, string? mode)
    {
        try
        {
            if (!GatewayLauncher.IsRunning &&
                !await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname))
            {
                return false;
            }

            var wanted = string.Equals(mode, "online", StringComparison.OrdinalIgnoreCase) ? "online" : "offline";
            // controlId 供各实例去重，visited 用于阻断环形转发。
            var controlId = Guid.NewGuid().ToString("N");
            var json = action == "panic"
                ? $"{{\"action\":\"panic\",\"controlId\":\"{controlId}\",\"visited\":[]}}"
                : $"{{\"action\":\"mode\",\"mode\":\"{wanted}\",\"controlId\":\"{controlId}\",\"visited\":[]}}";

            using var req = new HttpRequestMessage(
                HttpMethod.Post, SessionState.LocalGatewayUrl.TrimEnd('/') + "/api/lan/control");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await LanHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"局域网指令下发失败：{ex.Message}");
            return false;
        }
    }

    private static readonly HttpClient LanHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

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
