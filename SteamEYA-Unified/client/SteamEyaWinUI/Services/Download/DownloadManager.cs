using System.Diagnostics;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 下载任务队列：串行/限量执行、暂停恢复取消、以及把后台引擎的快照同步到 UI 模型。
///
/// 线程模型：引擎跑在后台任务里，**从不触碰 UI 对象**；本类的 <see cref="SyncUi"/> 必须由 UI 线程
/// 按固定节拍调用（页面用 DispatcherQueueTimer），在这里写 <see cref="DownloadTaskItem"/> 的属性，
/// 从而保证 PropertyChanged 全部发生在 UI 线程。
/// </summary>
internal sealed class DownloadManager
{
    private sealed class Entry
    {
        public Entry(DownloadTaskItem task, DownloadEngine engine)
        {
            Task = task;
            Engine = engine;
            LastSampleAt = Stopwatch.GetTimestamp();
        }

        public DownloadTaskItem Task { get; }

        public DownloadEngine Engine { get; }

        public long LastSampleBytes { get; set; }

        public long LastSampleAt { get; set; }

        public bool Primed { get; set; }
    }

    private readonly List<Entry> _entries = [];
    private readonly Queue<Entry> _queue = new();
    private readonly object _gate = new();
    private int _running;

    public static DownloadManager Instance { get; } = new();

    public IReadOnlyList<DownloadTaskItem> Tasks
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(entry => entry.Task).ToArray();
            }
        }
    }

    /// <summary>把链接加入队列。返回的任务对象由页面放进自己的 ObservableCollection。</summary>
    public DownloadTaskItem Enqueue(string url, string directory, int threads)
    {
        var placeholder = DownloadPaths.SanitizeFileName(
            TryGuessNameFromUrl(url), ".bin");

        var task = new DownloadTaskItem(url, placeholder, directory, threads);
        var engine = new DownloadEngine(url, directory, threads);
        var entry = new Entry(task, engine);

        lock (_gate)
        {
            _entries.Add(entry);
            _queue.Enqueue(entry);
        }

        Pump();
        return task;
    }

    /// <summary>暂停：引擎收到取消信号后落盘续传状态，任务随后可「继续」。</summary>
    public void Pause(DownloadTaskItem task)
    {
        var entry = Find(task);
        entry?.Engine.Pause();
        task.SetStatus(DownloadStatus.Paused);
    }

    /// <summary>继续/重试：新建引擎（旧引擎的 CTS 已取消），从续传状态接着下。</summary>
    public void Resume(DownloadTaskItem task)
    {
        var old = Find(task);
        if (old is not null && old.Engine.Status is DownloadStatus.Probing or DownloadStatus.Downloading)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var threads = task.Threads > 0 ? task.Threads : settings.DownloadThreadCount;
        var engine = new DownloadEngine(task.Url, task.Directory, threads);
        var entry = new Entry(task, engine);

        lock (_gate)
        {
            _entries.RemoveAll(item => ReferenceEquals(item.Task, task));
            _entries.Add(entry);
            _queue.Enqueue(entry);
        }

        task.SetStatus(DownloadStatus.Queued);
        Pump();
    }

    /// <summary>取消：终止引擎并删除半成品与续传状态。</summary>
    public void Cancel(DownloadTaskItem task)
    {
        var entry = Find(task);
        entry?.Engine.Cancel();
        task.SetStatus(DownloadStatus.Canceled);
    }

    /// <summary>UI 节拍：把引擎快照写进绑定模型（**必须 UI 线程调用**）。</summary>
    public void SyncUi()
    {
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        foreach (var entry in snapshot)
        {
            var engine = entry.Engine;
            var task = entry.Task;

            if (!string.IsNullOrWhiteSpace(engine.FileName) &&
                !string.Equals(task.FileName, engine.FileName, StringComparison.Ordinal))
            {
                task.SetFileName(engine.FileName!, task.Directory);
            }

            if (engine.TotalBytes > 0 && task.TotalBytes != engine.TotalBytes)
            {
                task.SetTotal(engine.TotalBytes);
            }

            if (task.ActiveConnections != engine.Connections)
            {
                task.SetPlan(engine.RequestedThreads, engine.Connections);
            }

            var status = engine.Status;
            if (task.Status != status)
            {
                task.SetStatus(status, engine.Error);
            }

            var now = Stopwatch.GetTimestamp();
            var bytes = engine.ReceivedBytes;

            if (!entry.Primed)
            {
                entry.Primed = true;
                entry.LastSampleBytes = bytes;
                entry.LastSampleAt = now;
            }
            else
            {
                var elapsed = Stopwatch.GetElapsedTime(entry.LastSampleAt, now);
                if (elapsed.TotalMilliseconds >= 200)
                {
                    engine.SampleSpeed(elapsed, entry.LastSampleBytes);
                    entry.LastSampleBytes = bytes;
                    entry.LastSampleAt = now;
                }
            }

            task.UpdateProgress(bytes, engine.Speed);
        }
    }

    /// <summary>任务结束后从队列登记表移除（页面点「移除」时调用）。</summary>
    public void Forget(DownloadTaskItem task)
    {
        lock (_gate)
        {
            _entries.RemoveAll(item => ReferenceEquals(item.Task, task));
        }
    }

    private Entry? Find(DownloadTaskItem task)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(item => ReferenceEquals(item.Task, task));
        }
    }

    private void Pump()
    {
        var maxConcurrent = Math.Clamp(AppState.SettingsService.Load().DownloadConcurrentTasks, 1, 8);
        List<Entry> starting = [];

        lock (_gate)
        {
            while (_running < maxConcurrent && _queue.Count > 0)
            {
                var entry = _queue.Dequeue();
                _running++;
                starting.Add(entry);
            }
        }

        foreach (var entry in starting)
        {
            _ = RunAsync(entry);
        }
    }

    private async Task RunAsync(Entry entry)
    {
        try
        {
            await Task.Run(entry.Engine.RunAsync);
        }
        catch (Exception ex)
        {
            AppLog.Error("下载任务异常终止。", ex);
            entry.Task.SetStatus(DownloadStatus.Failed, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }

            Pump();
        }
    }

    /// <summary>从 URL 末段猜一个占位文件名，探测完成后会被真实文件名替换。</summary>
    private static string TryGuessNameFromUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var segment = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    return Uri.UnescapeDataString(segment);
                }
            }
        }
        catch
        {
            // 猜不出就让 SanitizeFileName 用时间戳兜底。
        }

        return string.Empty;
    }
}
