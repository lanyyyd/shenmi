using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 百宝箱：目前只有「多线程下载」，其余工具后续以卡片形式追加。
/// 页面只负责交互与展示，下载逻辑在 DownloadManager/DownloadEngine 中。
/// </summary>
public sealed partial class ToolboxPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<DownloadTaskItem> _tasks = [];
    private readonly DispatcherQueueTimer _timer;

    private CancellationTokenSource? _probeCts;
    private RemoteFileInfo? _probe;
    private bool _syncing;

    public ToolboxPage()
    {
        InitializeComponent();

        TaskList.ItemsSource = _tasks;

        // 其它入口（资源库等）直接调 DownloadManager.Enqueue，本页要跟着补进列表。
        DownloadManager.Instance.TaskEnqueued += OnTaskEnqueued;

        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();

        _syncing = true;
        ThreadBox.Value = Math.Clamp(AppState.SettingsService.Load().DownloadThreadCount, 1, 256);
        _syncing = false;

        UpdateDirText();
        UpdateEmptyHint();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 页面首次打开时把管理器里已有的任务（可能是进页面前从资源库发起的）一次性补全。
        foreach (var task in DownloadManager.Instance.Tasks)
        {
            AddTaskIfMissing(task);
        }

        _timer.Start();
        Tick();
    }

    private void OnTaskEnqueued(DownloadTaskItem task)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            AddTaskIfMissing(task);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => AddTaskIfMissing(task));
        }
    }

    /// <summary>去重后加入列表：页内入队时事件已经加过一次，这里必须跳过。</summary>
    private void AddTaskIfMissing(DownloadTaskItem task)
    {
        if (_tasks.Contains(task))
        {
            return;
        }

        _tasks.Add(task);
        UpdateEmptyHint();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            UpdateDirText();
            ApplyProbeUi();
            UpdateEmptyHint();
        });
    }

    /// <summary>UI 节拍：把后台引擎进度写进绑定模型，并刷新空状态提示。</summary>
    private void Tick()
    {
        try
        {
            DownloadManager.Instance.SyncUi();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新下载进度失败：{ex.Message}");
        }

        UpdateEmptyHint();
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateDirText() =>
        DownloadDirText.Text = DownloadPaths.ResolveDirectory(AppState.SettingsService.Load());

    private int ResolveRequestedThreads()
    {
        if (_syncing)
        {
            return AppState.SettingsService.Load().DownloadThreadCount;
        }

        var value = ThreadBox.Value;
        return double.IsNaN(value)
            ? AppState.SettingsService.Load().DownloadThreadCount
            : Math.Clamp((int)value, 1, 256);
    }

    // ---------- 链接输入与探测 ----------

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                ShowStatus(Loc.T("Toolbox_Paste_Empty"), InfoBarSeverity.Warning);
                return;
            }

            var text = (await content.GetTextAsync()).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus(Loc.T("Toolbox_Paste_Empty"), InfoBarSeverity.Warning);
                return;
            }

            UrlBox.Text = text;
            UrlBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取剪贴板失败：{ex.Message}");
            ShowStatus(Loc.T("Toolbox_Paste_Failed"), InfoBarSeverity.Error);
        }
    }

    private async void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 每敲一个字符都发请求会打爆目标站点：500ms 防抖，并把上一轮探测取消掉。
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        var token = cts.Token;

        var text = UrlBox.Text.Trim();
        if (text.Length < 8)
        {
            _probe = null;
            ProbePanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            await Task.Delay(500, token);
            var info = await RemoteFileProbe.ProbeAsync(text, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _probe = info;
            ApplyProbeUi();
        }
        catch (OperationCanceledException)
        {
            // 用户继续输入或清空，忽略。
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _probe = null;
                ProbePanel.Visibility = Visibility.Collapsed;
                AppLog.Warn($"探测链接失败：{ex.Message}");
            }
        }
    }

    /// <summary>把探测结果显示出来，并按当前线程数算出「实际会用几条连接」（小文件自动降级）。</summary>
    private void ApplyProbeUi()
    {
        if (_probe is null)
        {
            ProbePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ProbeNameText.Text = _probe.FileName;

        var threads = ResolveRequestedThreads();
        ProbeMetaText.Text = _probe.SupportsRange
            ? Loc.Tf(
                "Toolbox_Probe_Meta_Format",
                DownloadFormat.FormatSize(_probe.TotalBytes),
                threads,
                DownloadEngine.ResolveSegmentCount(_probe.TotalBytes, _probe.SupportsRange, threads))
            : Loc.Tf(
                "Toolbox_Probe_Meta_NoRange_Format",
                DownloadFormat.FormatSize(_probe.TotalBytes));

        ProbePanel.Visibility = Visibility.Visible;
    }

    private void ThreadBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        ApplyProbeUi();
    }

    // ---------- 开始下载 ----------

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowStatus(Loc.T("Toolbox_Error_InvalidUrl"), InfoBarSeverity.Warning);
            return;
        }

        var settings = AppState.SettingsService.Load();
        var directory = DownloadPaths.ResolveDirectory(settings);

        if (!DownloadPaths.TryEnsureWritable(directory, out var error))
        {
            ShowStatus(Loc.Tf("Toolbox_Error_DirNotWritable_Format", error ?? string.Empty), InfoBarSeverity.Error);
            return;
        }

        var task = DownloadManager.Instance.Enqueue(url, directory, ResolveRequestedThreads());
        AddTaskIfMissing(task);

        UrlBox.Text = string.Empty;
        _probe = null;
        ProbePanel.Visibility = Visibility.Collapsed;
        UpdateEmptyHint();

        ShowStatus(Loc.Tf("Toolbox_Status_Added_Format", task.FileName), InfoBarSeverity.Informational);
    }

    // ---------- 任务操作 ----------

    private static DownloadTaskItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DownloadTaskItem;

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            DownloadManager.Instance.Pause(item);
        }
    }

    private void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            DownloadManager.Instance.Resume(item);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            DownloadManager.Instance.Cancel(item);
        }
    }

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Queued)
        {
            DownloadManager.Instance.Cancel(item);
        }

        _tasks.Remove(item);
        DownloadManager.Instance.Forget(item);
        UpdateEmptyHint();
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        try
        {
            var target = item.TargetPath;
            var argument = File.Exists(target)
                ? $"/select,\"{target}\""
                : $"\"{item.Directory}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Toolbox_Error_OpenFolder_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---------- 目录 ----------

    private async void ChangeDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add("*");

            // 无打包 WinUI：选择器必须显式绑定窗口句柄，否则弹不出来。
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var settings = AppState.SettingsService.Load();
            settings.DownloadDirectory = folder.Path;
            AppState.SettingsService.Save(settings);

            UpdateDirText();
            ShowStatus(Loc.Tf("Toolbox_Status_DirChanged_Format", folder.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Toolbox_Error_OpenFolder_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    private void OpenDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = DownloadPaths.ResolveDirectory(AppState.SettingsService.Load());
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Toolbox_Error_OpenFolder_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    private static void ShowStatus(string message, InfoBarSeverity severity) =>
        AppState.ShowStatus(message, severity);
}
