using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using SteamEyaWinUI.Services.RemoteAssist;
using Windows.Graphics;
using WinRT.Interop;

namespace SteamEyaWinUI.Views;

/// <summary>
/// 右下角协助弹窗（自绘，不用系统 toast）。
///
/// 同一个窗口两种形态：
///  · 邀请形态：显示"某某请求远程协助"，带 5 分钟倒计时与「同意 / 拒绝」；
///  · 会话形态：被控中常驻的"正在被某某协助"条，只保留「断开」——这是安全底线，不能让人不知情。
///
/// 关键点：加 WS_EX_NOACTIVATE，弹出时不抢焦点（不会打断对方打字），但鼠标点击照常有效。
/// </summary>
public sealed partial class AssistInviteToast : Window
{
    private const int WidthDip = 380;
    private const int InviteHeightDip = 178;
    private const int SessionHeightDip = 132;
    private const int MarginDip = 20;

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;

    private enum ToastMode
    {
        Invite,
        Session,
    }

    private static AssistInviteToast? _instance;
    private static readonly Queue<AssistRequestRecord> Queue = new();
    private static AssistSession? _session;

    private readonly DispatcherQueueTimer _timer;
    private ToastMode _mode = ToastMode.Invite;
    private AssistRequestRecord? _current;
    private bool _closing;

    public AssistInviteToast()
    {
        InitializeComponent();

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, false);
        presenter?.IsResizable = false;
        presenter?.IsMaximizable = false;
        presenter?.IsMinimizable = false;
        presenter?.IsAlwaysOnTop = true;

        ApplyNoActivate();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();

        Closed += (_, _) =>
        {
            _timer.Stop();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        };
    }

    /// <summary>弹出一条新的协助邀请（可在后台线程调用，内部已封送到 UI 线程）。</summary>
    internal static void ShowInvite(AssistRequestRecord record)
    {
        if (MainWindow.Instance?.DispatcherQueue is not { } ui)
        {
            return;
        }

        ui.TryEnqueue(() =>
        {
            Queue.Enqueue(record);
            EnsureInstance()?.ShowNextIfIdle();
        });
    }

    /// <summary>被控会话建立：切换为常驻的"正在被协助"条。</summary>
    internal static void ShowSessionBar(AssistSession session)
    {
        if (MainWindow.Instance?.DispatcherQueue is not { } ui)
        {
            return;
        }

        ui.TryEnqueue(() =>
        {
            _session = session;
            var instance = EnsureInstance();
            if (instance is null)
            {
                return;
            }
            instance.EnterSessionMode();
        });
    }

    internal static void CloseSessionBar()
    {
        _session = null;
        var instance = _instance;
        if (instance is null)
        {
            return;
        }
        if (instance._mode == ToastMode.Session)
        {
            instance.CloseSafely();
        }
    }

    internal static void CloseAll()
    {
        Queue.Clear();
        _session = null;
        _instance?.CloseSafely();
        _instance = null;
    }

    private static AssistInviteToast? EnsureInstance()
    {
        if (_instance is not null)
        {
            return _instance;
        }

        try
        {
            _instance = new AssistInviteToast();
            return _instance;
        }
        catch (Exception ex)
        {
            AppLog.Error("创建协助弹窗失败。", ex);
            return null;
        }
    }

    private void ShowNextIfIdle()
    {
        if (_mode == ToastMode.Session || _current is not null || _closing)
        {
            return;
        }

        if (Queue.Count == 0)
        {
            CloseSafely();
            return;
        }

        _current = Queue.Dequeue();
        _mode = ToastMode.Invite;
        _closing = false;

        TitleText.Text = Loc.T("Assist_Invite_Title");
        BodyText.Text = Loc.Tf("Assist_Invite_Body", _current.FromNick, _current.FromIp);
        AcceptBtn.Content = Loc.T("Assist_Accept");
        RejectBtn.Content = Loc.T("Assist_Reject");
        AcceptBtn.Visibility = Visibility.Visible;
        RejectBtn.Visibility = Visibility.Visible;
        AcceptBtn.IsEnabled = true;
        RejectBtn.IsEnabled = true;
        CountdownBar.Visibility = Visibility.Visible;

        UpdatePosition(InviteHeightDip);
        _timer.Start();
        Activate();
    }

    private void EnterSessionMode()
    {
        _mode = ToastMode.Session;
        _current = null;
        Queue.Clear();

        TitleText.Text = Loc.Tf("Assist_Active_Title", _session?.PeerNick ?? Loc.T("Assist_Someone"));
        BodyText.Text = Loc.T("Assist_Active_Body");
        AcceptBtn.Visibility = Visibility.Collapsed;
        RejectBtn.Visibility = Visibility.Visible;
        RejectBtn.Content = Loc.T("Assist_Disconnect");
        RejectBtn.IsEnabled = true;
        CountdownBar.Visibility = Visibility.Collapsed;

        UpdatePosition(SessionHeightDip);
        _timer.Stop();
        Activate();
    }

    private void OnTick()
    {
        if (_mode != ToastMode.Invite || _current is null)
        {
            return;
        }

        var remainingMs = _current.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
        {
            // 到点即消失；状态由 AssistHost 的超时扫描统一改成"失效"。
            _current = null;
            ShowNextIfIdle();
            return;
        }

        var totalMs = Math.Max(_current.ExpiresAt - _current.CreatedAt, 1);
        CountdownBar.Value = Math.Clamp(remainingMs * 100.0 / totalMs, 0, 100);

        var seconds = (int)(remainingMs / 1000);
        BodyText.Text = Loc.Tf("Assist_Invite_Body_Countdown",
            _current.FromNick, _current.FromIp, $"{seconds / 60}:{seconds % 60:00}");
    }

    private async void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (_mode == ToastMode.Session)
        {
            return;
        }

        var current = _current;
        if (current is null)
        {
            return;
        }

        AcceptBtn.IsEnabled = false;
        RejectBtn.IsEnabled = false;

        var ok = await AssistService.Host.ApproveAsync(current.RequestId);
        if (!ok)
        {
            // 同意失败（对端已断开/本机无法采集），直接把这条收起，避免卡在弹窗上。
            _current = null;
            ShowNextIfIdle();
        }
        // 成功时会话建立 → ShowSessionBar 会把窗口切到会话形态
    }

    private async void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if (_mode == ToastMode.Session)
        {
            _session?.Close("host_user_disconnect");
            CloseSafely();
            return;
        }

        var current = _current;
        if (current is null)
        {
            return;
        }

        AcceptBtn.IsEnabled = false;
        RejectBtn.IsEnabled = false;
        await AssistService.Host.RejectAsync(current.RequestId);
        _current = null;
        ShowNextIfIdle();
    }

    private void CloseSafely()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        _timer.Stop();
        try
        {
            Close();
        }
        catch
        {
            // 已经关闭
        }
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    /// <summary>贴到当前显示器工作区右下角（MoveAndResize 吃物理像素，必须乘 DPI 缩放）。</summary>
    private void UpdatePosition(int heightDip)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var scale = GetDpiForWindow(hwnd) / 96.0;

            var width = (int)(WidthDip * scale);
            var height = (int)(heightDip * scale);
            var margin = (int)(MarginDip * scale);

            AppWindow.MoveAndResize(new RectInt32(
                work.X + work.Width - width - margin,
                work.Y + work.Height - height - margin,
                width,
                height));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"定位协助弹窗失败：{ex.Message}");
        }
    }

    private void ApplyNoActivate()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)(style | WsExNoActivate));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置弹窗不抢焦点失败：{ex.Message}");
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
