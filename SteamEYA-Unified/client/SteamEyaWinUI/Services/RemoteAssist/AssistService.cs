using Microsoft.UI.Dispatching;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services.RemoteAssist;
using SteamEyaWinUI.Views;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 远程协助的门面：把"被控端监听 / 申请记录 / 弹窗"串起来，页面只跟它打交道。
///
/// 登录成功后调用 <see cref="EnsureStarted"/> 即进入可被邀请状态（与局域网广播同时上线）；
/// 主窗口关闭时调用 <see cref="Stop"/> 释放监听与端口。
/// </summary>
internal static class AssistService
{
    private static DispatcherQueue? _ui;
    private static bool _wired;

    /// <summary>被控端服务（监听 + 申请列表）。</summary>
    public static AssistHost Host { get; } = new();

    /// <summary>主控端当前会话（一个进程同时只允许一个）。</summary>
    public static AssistSession? ControllerSession { get; set; }

    /// <summary>任意状态变化（申请列表、会话、监听状态），页面据此刷新。</summary>
    public static event Action? Changed;

    public static bool Running => Host.IsRunning;

    /// <summary>注入 UI 派发器并接好事件（MainWindow 构造时调用一次）。</summary>
    public static void AttachUi(DispatcherQueue ui)
    {
        _ui = ui;
        if (_wired)
        {
            return;
        }
        _wired = true;

        Host.InviteReceived += record => Post(() =>
        {
            AssistInviteToast.ShowInvite(record);
            NotifyChanged();
        });

        Host.SessionStarted += session => Post(() =>
        {
            AssistInviteToast.ShowSessionBar(session);
            NotifyChanged();
        });

        Host.SessionEnded += reason => Post(() =>
        {
            AssistInviteToast.CloseSessionBar();
            NotifyChanged();
        });

        Host.Faulted += message => Post(() =>
        {
            AppLog.Warn($"远程协助服务异常：{message}");
            MainWindow.Instance?.ShowStatus(message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        });

        Host.Store.Changed += () => Post(NotifyChanged);
    }

    /// <summary>登录后调用：启动监听（幂等）。昵称与 installId 沿用现有账号体系，不另建身份。</summary>
    public static void EnsureStarted()
    {
        if (Host.IsRunning)
        {
            return;
        }

        var nick = string.IsNullOrWhiteSpace(SessionState.Nickname) ? Environment.MachineName : SessionState.Nickname;
        Host.Start(nick, SessionState.InstallId);
    }

    public static void Stop()
    {
        AssistInviteToast.CloseAll();
        Host.Stop();
        ControllerSession?.Dispose();
        ControllerSession = null;
    }

    public static void NotifyChanged() => Changed?.Invoke();

    private static void Post(Action action)
    {
        if (_ui is { } ui)
        {
            ui.TryEnqueue(() => action());
        }
    }
}
