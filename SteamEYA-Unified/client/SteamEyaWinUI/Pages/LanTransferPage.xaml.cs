using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 局域网互传：上方列出同网段的设备（UDP 广播自动发现 + 手动添加），选中设备后
/// 选择/拖入任意格式文件即开始点对点直传。传输走对端本机网关的原始字节流接口，
/// 单文件切多段并发、文件间再加并发，不限大小、不经公网。
/// </summary>
public sealed partial class LanTransferPage : Page
{
    /// <summary>同时发送的文件数（单文件内部还会再切成多条连接并发）。</summary>
    private const int MaxConcurrentFiles = 3;

    private readonly LanTransferService _service = new();
    private readonly ChatService _chat = ChatHub.Chat;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private readonly ObservableCollection<LanPeerVm> _peers = [];
    private readonly ObservableCollection<LanTransferItem> _outgoing = [];
    private readonly ObservableCollection<LanReceivedVm> _incoming = [];

    private LanPeerVm? _selectedPeer;
    private string _recvDir = string.Empty;
    private string _incomingSignature = string.Empty;

    private CancellationTokenSource _cts = new();
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _inboxTimer;
    private bool _subscribed;

    public LanTransferPage()
    {
        InitializeComponent();

        PeersList.ItemsSource = _peers;
        OutgoingList.ItemsSource = _outgoing;
        IncomingList.ItemsSource = _incoming;

        _progressTimer = _dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(300);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => UpdateProgressUi();

        _inboxTimer = _dispatcher.CreateTimer();
        _inboxTimer.Interval = TimeSpan.FromSeconds(2);
        _inboxTimer.IsRepeating = true;
        _inboxTimer.Tick += (_, _) => _ = RefreshInboxAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTexts();
        if (!_subscribed)
        {
            Loc.LanguageChanged += ApplyTexts;
            _subscribed = true;
        }
        _progressTimer.Start();
        _inboxTimer.Start();
        _ = InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            Loc.LanguageChanged -= ApplyTexts;
            _subscribed = false;
        }
        _progressTimer.Stop();
        _inboxTimer.Stop();
    }

    private async Task InitializeAsync()
    {
        await EnsureGatewayAsync();
        await RefreshPeersAsync();
        await RefreshInboxAsync();
    }

    /// <summary>本机网关负责局域网发现与接收；在线模式默认没起，这里按需拉起。</summary>
    private async Task EnsureGatewayAsync()
    {
        try
        {
            var info = await _chat.GetLanInfoAsync();
            if (!info.Enabled)
            {
                await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"启动本机网关失败：{ex.Message}");
        }
    }

    private void ApplyTexts()
    {
        PageTitle.Text = Loc.T("Lan_Page_Title");
        RefreshText.Text = Loc.T("Lan_Refresh");
        OpenFolderText.Text = Loc.T("Lan_OpenFolder");
        PeersTitle.Text = Loc.T("Lan_Peers_Title");
        AddDeviceText.Text = Loc.T("Lan_AddDevice");
        SelectFilesText.Text = Loc.T("Lan_SelectFiles");
        CancelText.Text = Loc.T("Lan_Cancel");
        OutgoingTitle.Text = Loc.T("Lan_Outgoing_Title");
        IncomingTitle.Text = Loc.T("Lan_Incoming_Title");
        PeersEmptyText.Text = Loc.T("Chat_Lan_None");
        IncomingEmptyText.Text = Loc.T("Lan_Incoming_Empty");
        SelectionText.Text = Loc.T("Lan_Hint");
        if (_selectedPeer is not null)
        {
            SelectionText.Text = Loc.Tf("Lan_Target_Format", _selectedPeer.Name);
        }
    }

    // ---------------- 设备列表 ----------------
    private async Task RefreshPeersAsync()
    {
        try
        {
            var info = await _chat.GetLanInfoAsync();
            SelfText.Text = info.Enabled
                ? Loc.Tf("Chat_Lan_Self", info.Name, info.Port ?? 0)
                : Loc.T("Chat_Lan_Off");

            var result = await _chat.GetLanPeersAsync();
            var peers = result.Peers ?? [];

            var keepIp = _selectedPeer?.Ip;
            var keepPort = _selectedPeer?.Port ?? 0;
            _peers.Clear();
            foreach (var peer in peers)
            {
                var vm = new LanPeerVm(
                    string.IsNullOrWhiteSpace(peer.Name) ? peer.Ip : peer.Name, peer.Ip, peer.Port);
                _peers.Add(vm);
                if (peer.Ip == keepIp && peer.Port == keepPort)
                {
                    PeersList.SelectedItem = vm;
                    _selectedPeer = vm;
                }
            }
            PeersEmptyText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新局域网设备失败：{ex.Message}");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshPeersAsync();

    private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPeer = PeersList.SelectedItem as LanPeerVm;
        ApplyTexts();
    }

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
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

    // ---------------- 发送 ----------------
    private async void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var peer = _selectedPeer;
        if (peer is null)
        {
            ShowInfo(Loc.T("Lan_No_Target"), InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");   // 任意格式
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return;
        }
        StartSend(peer, files);
    }

    private void StartSend(LanPeerVm peer, IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }
        var jobs = new List<(LanTransferItem Item, string Path)>(files.Count);
        foreach (var file in files)
        {
            var item = new LanTransferItem(file.Name, $"{peer.Name} · {peer.Endpoint}", SizeOf(file.Path), file.Path);
            _outgoing.Insert(0, item);
            jobs.Add((item, file.Path));
        }
        _ = RunSendAsync(peer, jobs);
    }

    private async Task RunSendAsync(LanPeerVm peer, List<(LanTransferItem Item, string Path)> jobs)
    {
        var cts = _cts;
        using var gate = new SemaphoreSlim(MaxConcurrentFiles);
        var tasks = new List<Task>(jobs.Count);
        foreach (var (item, path) in jobs)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    Ui(() => item.StatusText = Loc.T("Lan_Status_Sending"));
                    var result = await _service
                        .SendFileAsync(peer.Ip, peer.Port, path, SessionState.Nickname,
                            delta => item.AddSent(delta), cts.Token)
                        .ConfigureAwait(false);
                    if (result.Ok)
                    {
                        Ui(() =>
                        {
                            item.Percent = 100;
                            item.StatusText = Loc.T("Lan_Status_Done");
                        });
                    }
                    else
                    {
                        Ui(() => item.StatusText = Loc.Tf("Lan_Status_Failed", result.Msg ?? string.Empty));
                    }
                }
                catch (Exception ex)
                {
                    Ui(() => item.StatusText = Loc.Tf("Lan_Status_Failed", ex.Message));
                }
                finally
                {
                    Ui(() => item.Finished = true);
                    gate.Release();
                }
            }));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 只取消、不销毁：正在传输中的请求还会用到该 Token，销毁会导致后续注册抛异常。
        _cts.Cancel();
        _cts = new CancellationTokenSource();
    }

    /// <summary>进度条刷新：读累计已发送字节，算百分比与平均速度（UI 线程定时调用）。</summary>
    private void UpdateProgressUi()
    {
        foreach (var item in _outgoing)
        {
            if (item.Finished)
            {
                continue;
            }
            var sent = item.SentBytes;
            item.Percent = item.TotalBytes > 0 ? Math.Min(100.0, sent * 100.0 / item.TotalBytes) : 0;
            var seconds = (DateTime.UtcNow - item.StartedAt).TotalSeconds;
            item.SpeedText = seconds > 0.05 ? LanFormat.Speed(sent / seconds) : string.Empty;
        }
    }

    // ---------------- 接收列表 ----------------
    private async Task RefreshInboxAsync()
    {
        var inbox = await _service.GetInboxAsync();
        if (!string.IsNullOrWhiteSpace(inbox.Dir))
        {
            _recvDir = inbox.Dir;
        }
        var active = inbox.Active ?? [];
        var received = inbox.Received ?? [];

        var signature = string.Join(",", active.Select(a => $"{a.Id}:{a.Written}"))
            + "#" + string.Join(",", received.Select(r => $"{r.Id}:{r.Name}"));
        if (signature == _incomingSignature)
        {
            return;
        }
        _incomingSignature = signature;

        _incoming.Clear();
        foreach (var a in active)
        {
            var percent = a.Size > 0 ? Math.Min(100.0, a.Written * 100.0 / a.Size) : 0;
            _incoming.Add(new LanReceivedVm(
                a.FileName,
                LanFormat.Bytes(a.Size),
                Loc.Tf("Lan_Receiving_Format", LanFormat.Bytes(a.Written), LanFormat.Bytes(a.Size)),
                percent, true));
        }
        foreach (var r in received)
        {
            var detail = string.IsNullOrWhiteSpace(r.From) ? r.Path : Loc.Tf("Lan_From_Format", r.From);
            _incoming.Add(new LanReceivedVm(r.Name, LanFormat.Bytes(r.Size), detail, 100, false));
        }
        IncomingEmptyText.Visibility = _incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_recvDir))
        {
            await RefreshInboxAsync();
        }
        if (string.IsNullOrWhiteSpace(_recvDir))
        {
            ShowInfo(Loc.T("Lan_Folder_NotReady"), InfoBarSeverity.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_recvDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowInfo(Loc.Tf("Lan_Status_Failed", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---------------- 拖入文件 ----------------
    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = Loc.T("Lan_Drop_Caption");
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var peer = _selectedPeer;
        if (peer is null)
        {
            ShowInfo(Loc.T("Lan_No_Target"), InfoBarSeverity.Warning);
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<StorageFile>().ToList();
        if (files.Count == 0)
        {
            ShowInfo(Loc.T("Lan_No_Files"), InfoBarSeverity.Warning);
            return;
        }
        StartSend(peer, files);
    }

    // ---------------- 工具 ----------------
    private void Ui(Action action) => _dispatcher.TryEnqueue(() => action());

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>局域网设备（同网段对端）列表项。</summary>
public sealed class LanPeerVm
{
    public LanPeerVm(string name, string ip, int port)
    {
        Name = name;
        Ip = ip;
        Port = port;
        Endpoint = $"{ip}:{port}";
    }

    public string Name { get; }
    public string Ip { get; }
    public int Port { get; }
    public string Endpoint { get; }
}

/// <summary>发送任务列表项（进度/速度由页面定时器刷新）。</summary>
public sealed class LanTransferItem : INotifyPropertyChanged
{
    private long _sent;
    private double _percent;
    private string _speedText = string.Empty;
    private string _statusText = string.Empty;

    public LanTransferItem(string fileName, string target, long totalBytes, string filePath)
    {
        FileName = fileName;
        Target = target;
        TotalBytes = totalBytes;
        FilePath = filePath;
        SizeText = LanFormat.Bytes(totalBytes);
        StartedAt = DateTime.UtcNow;
    }

    public string FileName { get; }
    public string Target { get; }
    public string SizeText { get; }
    public string FilePath { get; }
    public long TotalBytes { get; }
    public DateTime StartedAt { get; }

    /// <summary>是否已结束（结束后不再刷新进度，保留最终百分比）。</summary>
    public bool Finished { get; set; }

    public long SentBytes => Interlocked.Read(ref _sent);

    /// <summary>在后台线程累加已发送字节（服务层回调）。</summary>
    public void AddSent(long delta) => Interlocked.Add(ref _sent, delta);

    public double Percent
    {
        get => _percent;
        set => SetField(ref _percent, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => SetField(ref _speedText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>接收列表项（进行中的显示进度条，已完成的只显示信息）。</summary>
public sealed class LanReceivedVm
{
    public LanReceivedVm(string name, string sizeText, string detailText, double percent, bool active)
    {
        Name = name;
        SizeText = sizeText;
        DetailText = detailText;
        Percent = percent;
        ProgressVisibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Name { get; }
    public string SizeText { get; }
    public string DetailText { get; }
    public double Percent { get; }
    public Visibility ProgressVisibility { get; }
}

/// <summary>局域网页共用的字节/速度格式化。</summary>
internal static class LanFormat
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        var size = (double)Math.Max(value, 0);
        var unit = 0;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} {ByteUnits[unit]}" : $"{size:0.#} {ByteUnits[unit]}";
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }
        var size = bytesPerSecond;
        var unit = 0;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {ByteUnits[unit]}/s";
    }
}
