using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 资源库：直接调用 t.teatt.top 的公开 JSON 接口（api.php），用原生卡片渲染，
/// 下载按钮把直链丢给百宝箱的分片下载引擎，不经过浏览器也不嵌 WebView。
///
/// 数据策略见 <see cref="LibraryService"/>：内存 5 分钟 → 磁盘缓存 → 报错。
/// 页面只做筛选、展示与入队，不做网络重试逻辑。
/// </summary>
public sealed partial class LibraryPage : Page, INotifyPropertyChanged
{
    private const string AllCategory = "";

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    private List<LibraryResourceItem> _all = [];
    private List<string> _categories = [];
    private string _query = string.Empty;
    private string _category = AllCategory;
    private DateTimeOffset? _syncedAt;
    private bool _fromCache;
    private bool _loading;
    private bool _loadedOnce;

    public LibraryPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    public ObservableCollection<LibraryResourceItem> VisibleItems { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------- 生命周期 ----------------

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _ = LoadAsync(forceRefresh: false);
            return;
        }

        // 从别的页面切回来：只同步下载状态，不重新拉接口（内存缓存 5 分钟）。
        SyncDownloadStates();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面是 NavigationCacheMode=Required 的常驻实例，这里不做清理。
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            foreach (var item in _all)
            {
                item.RefreshLocalizedText();
            }

            BuildCategoryPills();
            UpdateHeaderText();
            UpdateEmptyText();
        });
    }

    // ---------------- 加载 ----------------

    private async Task LoadAsync(bool forceRefresh)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false;

        try
        {
            var snapshot = await LibraryService.LoadAsync(forceRefresh);

            _all = [.. snapshot.Items];
            _categories = [.. snapshot.Categories];
            _syncedAt = snapshot.SyncedAt;
            _fromCache = snapshot.FromCache;

            BuildCategoryPills();
            ApplyFilter();
            UpdateHeaderText();
            UpdateNotice(snapshot);
            SyncDownloadStates();
        }
        catch (Exception ex)
        {
            AppLog.Error("加载资源库失败。", ex);
            _all = [];
            ApplyFilter();
            UpdateHeaderText();
            NoticeBar.IsOpen = false;
        }
        finally
        {
            _loading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => _ = LoadAsync(forceRefresh: true);

    private void OpenDownloadsClick(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateToToolbox();

    private void OpenSiteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(LibraryService.SiteUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开资源站失败：{ex.Message}");
        }
    }

    // ---------------- 筛选 ----------------

    private void SearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<LibraryResourceItem> query = _all;

        if (!string.IsNullOrEmpty(_category))
        {
            query = query.Where(item => string.Equals(item.Category, _category, StringComparison.Ordinal));
        }

        if (_query.Length > 0)
        {
            // 空格分词，要求全部命中，够用且比正则轻。
            var terms = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(item => terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = query.ToList();

        VisibleItems.Clear();
        foreach (var item in filtered)
        {
            VisibleItems.Add(item);
        }

        // 顺序要紧：先决定显不显示，再按结果填文案（UpdateEmptyText 会据可见性决定要不要写）。
        EmptyPanel.Visibility = VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyText();
    }

    private void UpdateEmptyText()
    {
        if (EmptyText is null)
        {
            return;
        }

        if (_all.Count == 0)
        {
            EmptyText.Text = Loc.T("Lib_Empty_NoData");
            EmptyRetryButton.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Text = Loc.T("Lib_Empty_NoMatch");
        EmptyRetryButton.Visibility = Visibility.Collapsed;
    }

    private void BuildCategoryPills()
    {
        CategoryPanel.Children.Clear();
        AddPill(Loc.T("Lib_Category_All"), AllCategory);

        foreach (var category in _categories)
        {
            AddPill(category, category);
        }
    }

    private void AddPill(string text, string value)
    {
        var selected = string.Equals(_category, value, StringComparison.Ordinal);

        var button = new Button
        {
            Content = text,
            Tag = value,
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 6),
            CornerRadius = new CornerRadius(14),
            Style = (Style)Resources[selected ? "LibraryPillSelectedStyle" : "LibraryPillStyle"]
        };

        button.Click += (_, _) =>
        {
            if (string.Equals(_category, value, StringComparison.Ordinal))
            {
                return;
            }

            _category = value;
            BuildCategoryPills();
            ApplyFilter();
        };

        CategoryPanel.Children.Add(button);
    }

    // ---------------- 状态文本 ----------------

    private void UpdateHeaderText()
    {
        if (SyncText is null)
        {
            return;
        }

        if (_syncedAt is null)
        {
            SyncText.Text = Loc.T("Lib_Sync_Never");
            return;
        }

        var time = _syncedAt.Value.ToLocalTime().ToString("MM-dd HH:mm");
        var text = Loc.Tf("Lib_Sync_Format", _all.Count, time);

        if (_fromCache)
        {
            text += " · " + Loc.T("Lib_Sync_FromCache");
        }

        SyncText.Text = text;
    }

    private void UpdateNotice(LibrarySnapshot snapshot)
    {
        if (snapshot.FromCache && !string.IsNullOrEmpty(snapshot.Error))
        {
            var time = snapshot.SyncedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Message = Loc.Tf("Lib_Notice_Cached_Format", time);
            NoticeBar.IsOpen = true;
            return;
        }

        if (!string.IsNullOrEmpty(snapshot.Error))
        {
            NoticeBar.Severity = InfoBarSeverity.Error;
            NoticeBar.Message = Loc.Tf("Lib_Notice_Failed_Format", snapshot.Error);
            NoticeBar.IsOpen = true;
            return;
        }

        if (SessionState.Offline)
        {
            NoticeBar.Severity = InfoBarSeverity.Informational;
            NoticeBar.Message = Loc.T("Lib_Notice_Offline");
            NoticeBar.IsOpen = true;
            return;
        }

        NoticeBar.IsOpen = false;
    }

    // ---------------- 下载 ----------------

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibraryResourceItem item)
        {
            return;
        }

        if (item.State == LibraryDownloadState.InQueue)
        {
            AppState.ShowStatus(
                Loc.Tf("Lib_Status_AlreadyQueued_Format", item.Title), InfoBarSeverity.Informational);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            AppState.ShowStatus(Loc.T("Lib_Error_NoUrl"), InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var settings = AppState.SettingsService.Load();
            var directory = DownloadPaths.ResolveDirectory(settings);
            var task = DownloadManager.Instance.Enqueue(item.Url, directory, settings.DownloadThreadCount);

            item.SetState(LibraryDownloadState.InQueue);
            AppState.ShowStatus(
                Loc.Tf("Lib_Status_Added_Format", task.FileName), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("从资源库发起下载失败。", ex);
            AppState.ShowStatus(Loc.T("Lib_Error_Download"), InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 用下载队列里的 URL 反查卡片状态，避免同一资源被重复入队下多份。
    /// 只把这几种状态算作「进行中」。
    /// </summary>
    private void SyncDownloadStates()
    {
        HashSet<string> active;
        try
        {
            active = DownloadManager.Instance.Tasks
                .Where(task => task.Status is DownloadStatus.Queued or DownloadStatus.Probing
                               or DownloadStatus.Downloading or DownloadStatus.Paused)
                .Select(task => task.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"同步下载状态失败：{ex.Message}");
            return;
        }

        foreach (var item in _all)
        {
            item.SetState(active.Contains(item.Url) ? LibraryDownloadState.InQueue : LibraryDownloadState.Idle);
        }
    }
}
