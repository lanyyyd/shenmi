using System.ComponentModel;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Models;

/// <summary>下载任务状态。Queued/Probing/Downloading 为活动态，其余为终态或暂停。</summary>
public enum DownloadStatus
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// 百宝箱·下载任务：既是列表绑定的数据模型（x:Bind OneWay），也是引擎更新进度的载体。
/// 引擎只通过 SetTotal/SetStatus/UpdateProgress 等 internal 方法改状态，避免 UI 侧误写。
/// </summary>
public sealed partial class DownloadTaskItem : INotifyPropertyChanged
{
    private long _receivedBytes;
    private long _totalBytes;
    private double _speedBytesPerSecond;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _errorMessage;
    private int _threads;
    private int _activeConnections;

    public DownloadTaskItem(string url, string fileName, string directory, int threads)
    {
        Url = url;
        FileName = fileName;
        Directory = directory;
        _threads = threads;
    }

    public string Url { get; }

    public string FileName { get; private set; }

    public string Directory { get; private set; }

    /// <summary>本次任务实际使用的并发数（小文件会自动降级，见 DownloadEngine.ResolveSegmentCount）。</summary>
    public int Threads => _threads;

    /// <summary>实际建立的分片数（= 并发连接数上限）。</summary>
    public int ActiveConnections => _activeConnections;

    public string TargetPath => Path.Combine(Directory, FileName);

    public long TotalBytes => _totalBytes;

    public long ReceivedBytes => _receivedBytes;

    public DownloadStatus Status => _status;

    public string? ErrorMessage => _errorMessage;

    public double ProgressPercent => _totalBytes > 0
        ? Math.Clamp(_receivedBytes * 100.0 / _totalBytes, 0, 100)
        : 0;

    public string SizeText => DownloadFormat.FormatSizePair(_receivedBytes, _totalBytes);

    public string SpeedText => _status == DownloadStatus.Downloading
        ? DownloadFormat.FormatSpeed(_speedBytesPerSecond)
        : string.Empty;

    public string RemainingText
    {
        get
        {
            if (_status != DownloadStatus.Downloading || _totalBytes <= 0 || _speedBytesPerSecond < 1)
            {
                return string.Empty;
            }

            var remaining = Math.Max(0, _totalBytes - _receivedBytes);
            var seconds = remaining / _speedBytesPerSecond;
            return Loc.Tf("Dl_Remaining_Format", DownloadFormat.FormatDuration(seconds));
        }
    }

    public string StatusText => _status switch
    {
        DownloadStatus.Queued => Loc.T("Dl_Status_Queued"),
        DownloadStatus.Probing => Loc.T("Dl_Status_Probing"),
        DownloadStatus.Downloading => _activeConnections > 1
            ? Loc.Tf("Dl_Status_DownloadingThreads_Format", _activeConnections)
            : Loc.T("Dl_Status_Downloading"),
        DownloadStatus.Paused => Loc.T("Dl_Status_Paused"),
        DownloadStatus.Completed => Loc.T("Dl_Status_Completed"),
        DownloadStatus.Failed => Loc.T("Dl_Status_Failed"),
        DownloadStatus.Canceled => Loc.T("Dl_Status_Canceled"),
        _ => string.Empty
    };

    public bool CanPause => _status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading;

    public bool CanResume => _status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;

    public bool CanCancel => _status is not (DownloadStatus.Completed or DownloadStatus.Canceled);

    public bool IsCompleted => _status == DownloadStatus.Completed;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetTotal(long totalBytes)
    {
        _totalBytes = totalBytes;
        RefreshDerived();
    }

    internal void SetPlan(int threads, int connections)
    {
        _threads = threads;
        _activeConnections = connections;
        RefreshDerived();
    }

    internal void SetStatus(DownloadStatus status, string? error = null)
    {
        _status = status;
        _errorMessage = error;
        if (status != DownloadStatus.Downloading)
        {
            _speedBytesPerSecond = 0;
        }

        RefreshDerived();
    }

    internal void SetFileName(string fileName, string directory)
    {
        FileName = fileName;
        Directory = directory;
        RefreshDerived();
    }

    internal void UpdateProgress(long receivedBytes, double speedBytesPerSecond)
    {
        _receivedBytes = receivedBytes;
        _speedBytesPerSecond = speedBytesPerSecond;
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(ReceivedBytes)));
        handler(this, new PropertyChangedEventArgs(nameof(TotalBytes)));
        handler(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
        handler(this, new PropertyChangedEventArgs(nameof(SizeText)));
        handler(this, new PropertyChangedEventArgs(nameof(SpeedText)));
        handler(this, new PropertyChangedEventArgs(nameof(RemainingText)));
        handler(this, new PropertyChangedEventArgs(nameof(Status)));
        handler(this, new PropertyChangedEventArgs(nameof(StatusText)));
        handler(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
        handler(this, new PropertyChangedEventArgs(nameof(Threads)));
        handler(this, new PropertyChangedEventArgs(nameof(ActiveConnections)));
        handler(this, new PropertyChangedEventArgs(nameof(CanPause)));
        handler(this, new PropertyChangedEventArgs(nameof(CanResume)));
        handler(this, new PropertyChangedEventArgs(nameof(CanCancel)));
        handler(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
        handler(this, new PropertyChangedEventArgs(nameof(FileName)));
        handler(this, new PropertyChangedEventArgs(nameof(TargetPath)));
    }
}

/// <summary>断点续传状态文件（&lt;目标文件&gt;.steameya-dl）的序列化模型。</summary>
internal sealed class DownloadState
{
    public string Url { get; set; } = "";

    public string FinalUrl { get; set; } = "";

    public long TotalBytes { get; set; }

    public string? ETag { get; set; }

    /// <summary>每个分片已完成的字节数；数组长度即分片数。</summary>
    public long[] Done { get; set; } = [];

    public int SegmentCount { get; set; }
}

// 反射式 JSON 已全局禁用（csproj：JsonSerializerIsReflectionEnabledByDefault=false），
// 续传状态必须走 source generator，否则 AOT 产物运行时抛异常。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = false)]
[JsonSerializable(typeof(DownloadState))]
internal sealed partial class DownloadStateJsonContext : JsonSerializerContext;

/// <summary>下载相关的人类可读格式化（与 UI 语言无关的数字格式化）。</summary>
internal static class DownloadFormat
{
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.0} {units[unit]}";
    }

    public static string FormatSizePair(long received, long total) =>
        total > 0 ? $"{FormatSize(received)} / {FormatSize(total)}" : FormatSize(received);

    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : FormatSize((long)bytesPerSecond) + "/s";

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
