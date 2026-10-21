using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using SteamEyaWinUI.Services.RemoteAssist;
using SteamEyaWinUI.Views;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 远程协助页：上半是"当前局域网在线的昵称与 IP"（与局域网互传共用同一份 UDP 广播发现结果），
/// 双击即发起；下半是"协助申请"列表，每行右侧固定一个按钮——待处理是「同意」，其余一律「失效」（灰）。
/// </summary>
public sealed partial class RemoteAssistPage : Page
{
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);

    private readonly ChatService _chat = ChatHub.Chat;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private readonly ObservableCollection<AssistPeerVm> _peers = [];
    private readonly ObservableCollection<AssistRequestVm> _requests = [];
    private readonly Dictionary<string, AssistRequestVm> _requestMap = new(StringComparer.Ordinal);

    private readonly DispatcherQueueTimer _tickTimer;
    private int _tickCounter;
    private bool _subscribed;
    private bool _busy;
    private CancellationTokenSource? _connectCts;
    private RemoteAssistWindow? _remoteWindow;

    public RemoteAssistPage()
    {
        InitializeComponent();

        PeersList.ItemsSource = _peers;
        RequestsList.ItemsSource = _requests;

        _tickTimer = _dispatcher.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.IsRepeating = true;
        _tickTimer.Tick += OnTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTexts();
        if (!_subscribed)
        {
            Loc.LanguageChanged += ApplyTexts;
            AssistService.Changed += OnAssistChanged;
            _subscribed = true;
        }

        AssistService.EnsureStarted();
        _tickTimer.Start();
        _ = RefreshPeersAsync();
        RefreshRequests();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            Loc.LanguageChanged -= ApplyTexts;
            AssistService.Changed -= OnAssistChanged;
            _subscribed = false;
        }

        _tickTimer.Stop();
        _connectCts?.Cancel();
    }

    private void OnAssistChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshRequests();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshRequests);
        }
    }

    private void ApplyTexts()
    {
        PageTitle.Text = Loc.T("Nav_RemoteAssist");
        RefreshText.Text = Loc.T("Lan_Refresh");
        PeersTitle.Text = Loc.T("Assist_Peers_Title");
        RequestsTitle.Text = Loc.T("Assist_Requests_Title");
        PeersEmptyText.Text = Loc.T("Assist_Peers_Empty");
        RequestsEmptyText.Text = Loc.T("Assist_Requests_Empty");

        var port = AssistService.Running ? AssistService.Host.Port : 0;
        SelfText.Text = port > 0
            ? Loc.Tf("Assist_Self_Format", SessionState.Nickname, port)
            : Loc.T("Assist_Self_Off");

        if (_remoteWindow is not null)
        {
            return;
        }

        if (_busy)
        {
            ShowInfo(Loc.T("Assist_Waiting"), InfoBarSeverity.Informational);
        }
    }

    // ---------------- 在线设备 ----------------
    private async Task RefreshPeersAsync()
    {
        try
        {
            var result = await _chat.GetLanPeersAsync();
            var peers = result.Peers ?? [];

            var keep = (PeersList.SelectedItem as AssistPeerVm)?.Ip;
            _peers.Clear();
            foreach (var peer in peers)
            {
                var name = string.IsNullOrWhiteSpace(peer.Name) ? peer.Ip : peer.Name;
                var port = peer.AssistPort > 0 ? peer.AssistPort : 8900;
                var hint = peer.AssistEnabled ? Loc.T("Assist_DoubleClick") : Loc.T("Assist_NotEnabled");
                var vm = new AssistPeerVm(name, peer.Ip, port, hint);
                _peers.Add(vm);
                if (peer.Ip == keep)
                {
                    PeersList.SelectedItem = vm;
                }
            }

            PeersEmptyText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新协助设备失败：{ex.Message}");
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshPeersAsync();
        RefreshRequests();
    }

    private void OnTick(object? sender, object e)
    {
        RefreshRequests();
        _tickCounter++;
        if (_tickCounter % 3 == 0)
        {
            _ = RefreshPeersAsync();
        }
    }

    private async void OnPeersDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is not AssistPeerVm peer)
        {
            return;
        }

        await ConnectAsync(peer);
    }

    private async Task ConnectAsync(AssistPeerVm peer)
    {
        if (_busy)
        {
            ShowInfo(Loc.T("Assist_BusySelf"), InfoBarSeverity.Warning);
            return;
        }

        if (AssistService.ControllerSession is not null)
        {
            ShowInfo(Loc.T("Assist_AlreadyInSession"), InfoBarSeverity.Warning);
            return;
        }

        _busy = true;
        _connectCts = new CancellationTokenSource();
        ShowInfo(Loc.Tf("Assist_Connecting_Format", peer.Name), InfoBarSeverity.Informational);

        var client = new AssistClient(SessionState.InstallId, SessionState.Nickname);
        client.StateChanged += (state, message) => _dispatcher.TryEnqueue(() =>
        {
            switch (state)
            {
                case AssistConnectState.WaitingApproval:
                    ShowInfo(Loc.Tf("Assist_Waiting_Format", peer.Name), InfoBarSeverity.Informational);
                    break;
                case AssistConnectState.Rejected:
                    ShowInfo(Loc.T("Assist_Rejected"), InfoBarSeverity.Warning);
                    break;
                case AssistConnectState.Timeout:
                    ShowInfo(Loc.T("Assist_Timeout"), InfoBarSeverity.Warning);
                    break;
                case AssistConnectState.Busy:
                    ShowInfo(Loc.T("Assist_PeerBusy"), InfoBarSeverity.Warning);
                    break;
            }
        });

        try
        {
            var result = await client.ConnectAsync(peer.Ip, peer.Port, _connectCts.Token);
            if (result.State != AssistConnectState.Accepted || result.Session is null)
            {
                ShowInfo(result.Message ?? Loc.T("Assist_ConnectFailed"), InfoBarSeverity.Warning);
                return;
            }

            AssistService.ControllerSession = result.Session;
            _remoteWindow = new RemoteAssistWindow(result.Session);
            _remoteWindow.Closed += (_, _) =>
            {
                _remoteWindow = null;
                AssistService.ControllerSession = null;
                ShowInfo(Loc.T("Assist_Ended"), InfoBarSeverity.Informational);
            };
            _remoteWindow.Activate();
            PageInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"发起远程协助失败：{ex.Message}");
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    // ---------------- 申请列表 ----------------
    private void RefreshRequests()
    {
        var snapshot = AssistService.Host.Store.Snapshot();
        var ids = new List<string>(snapshot.Count);
        foreach (var record in snapshot)
        {
            ids.Add(record.RequestId);
        }

        var sameSet = ids.Count == _requests.Count
            && ids.Zip(_requests, (id, vm) => id == vm.RequestId).All(x => x);

        if (!sameSet)
        {
            _requests.Clear();
            _requestMap.Clear();
            foreach (var id in ids)
            {
                var vm = new AssistRequestVm(id);
                _requestMap[id] = vm;
                _requests.Add(vm);
            }
        }

        foreach (var record in snapshot)
        {
            if (_requestMap.TryGetValue(record.RequestId, out var vm))
            {
                ApplyRecord(vm, record);
            }
        }

        RequestsEmptyText.Visibility = _requests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRecord(AssistRequestVm vm, AssistRequestRecord record)
    {
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAt).ToLocalTime();
        var title = record.Inbound
            ? Loc.Tf("Assist_Request_Title", record.FromNick, createdAt.ToString("HH:mm"))
            : Loc.Tf("Assist_Request_Title_Out", record.FromNick, createdAt.ToString("HH:mm"));

        string detail;
        string status;
        var canApprove = record.Status == AssistRequestStatus.Pending;

        if (canApprove)
        {
            var remaining = TimeSpan.FromMilliseconds(record.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }
            detail = Loc.Tf("Assist_Remaining_Format", $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}");
            status = Loc.T("Assist_Status_Pending");
        }
        else
        {
            detail = record.FromIp;
            status = record.Status switch
            {
                AssistRequestStatus.Accepted => Loc.T("Assist_Status_Accepted"),
                AssistRequestStatus.Rejected => Loc.T("Assist_Status_Rejected"),
                AssistRequestStatus.Cancelled => Loc.T("Assist_Status_Cancelled"),
                AssistRequestStatus.Failed => Loc.T("Assist_Status_Failed"),
                _ => Loc.T("Assist_Status_Expired"),
            };
        }

        // 按钮只有两种形态：可点的「同意」，或灰的「失效」。
        var button = canApprove ? Loc.T("Assist_Accept") : Loc.T("Assist_Expired_Button");
        vm.Apply(title, detail, status, button, canApprove);
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AssistRequestVm vm || !vm.CanApprove)
        {
            return;
        }

        var ok = await AssistService.Host.ApproveAsync(vm.RequestId);
        if (!ok)
        {
            ShowInfo(Loc.T("Assist_ApproveFailed"), InfoBarSeverity.Warning);
        }
        RefreshRequests();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }
}
