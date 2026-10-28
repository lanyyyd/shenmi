using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 单个下载任务的执行引擎（后台线程运行，**不直接改 UI 模型**）。
/// UI 侧的进度由 <see cref="DownloadManager.SyncUi"/> 按固定节拍读取本类的快照后写入绑定模型，
/// 避免 64 个分片线程各自触发 PropertyChanged 造成 UI 线程雪崩。
///
/// 分片策略（小文件自动降级）见 <see cref="ResolveSegmentCount"/>。
/// </summary>
internal sealed class DownloadEngine
{
    /// <summary>每片最小字节数：低于它对小文件开多线程只会更慢（建连开销 &gt; 并行收益）。</summary>
    private const long MinSegmentBytes = 2 * 1024 * 1024;

    private const int MaxThreads = 256;

    private const int MaxAttemptsPerSegment = 3;

    private const int BufferSize = 80 * 1024;

    private const int IdleTimeoutSeconds = 60;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateGate = new();
    private readonly int _exactSegments;

    private long _received;
    private long _total;
    private double _speed;
    private int _connections;
    private int _status = (int)DownloadStatus.Queued;
    private string? _error;
    private string? _fileName;
    private string? _targetPath;
    private volatile bool _pauseRequested;
    private volatile bool _cancelRequested;
    private long[]? _segmentDone;

    public DownloadEngine(string url, string directory, int threads, int exactSegments = 0)
    {
        Url = url;
        TargetDirectory = directory;
        RequestedThreads = Math.Clamp(threads, 1, MaxThreads);
        _exactSegments = Math.Clamp(exactSegments, 0, MaxThreads);
    }

    public string Url { get; }

    public string TargetDirectory { get; }

    public int RequestedThreads { get; }

    public long ReceivedBytes => Interlocked.Read(ref _received);

    public long TotalBytes => Interlocked.Read(ref _total);

    public double Speed => _speed;

    public int Connections => _connections;

    public DownloadStatus Status => (DownloadStatus)_status;

    public string? Error => _error;

    public string? FileName => _fileName;

    public string? TargetPath => _targetPath;

    /// <summary>
    /// 计算实际分片数：从「线程数」和「文件大小 ÷ 每片 2MiB」中取小，再向下取 2 的幂。<br/>
    /// 例：64 线程下 20MB → 10 片 → **8**；4MB → 2；3MB → 1（单流）；128MB 及以上 → 64。
    /// 取 2 的幂是为了让各片长度尽量相等，且 4/8/16 这类档位更贴合站点常见的连接限制。
    /// </summary>
    internal static int ResolveSegmentCount(long totalBytes, bool supportsRange, int requestedThreads)
    {
        if (!supportsRange || totalBytes <= 0)
        {
            return 1;
        }

        var threads = Math.Clamp(requestedThreads, 1, MaxThreads);
        if (threads <= 1)
        {
            return 1;
        }

        var bySize = totalBytes / MinSegmentBytes;
        if (bySize < 2)
        {
            return 1;
        }

        var count = 1;
        while (count * 2 <= bySize && count * 2 <= threads)
        {
            count *= 2;
        }

        return count;
    }

    public void Pause()
    {
        _pauseRequested = true;
        SafeCancel();
    }

    public void Cancel()
    {
        _cancelRequested = true;
        SafeCancel();
    }

    private void SafeCancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务已结束，取消是 no-op。
        }
    }

    public async Task RunAsync()
    {
        try
        {
            SetStatus(DownloadStatus.Probing);

            var info = await RemoteFileProbe.ProbeAsync(Url, _cts.Token);
            _fileName = info.FileName;
            Interlocked.Exchange(ref _total, info.TotalBytes);

            PrepareTarget(info);
            var segments = _exactSegments > 0 && info.SupportsRange && info.TotalBytes > 0
                ? (int)Math.Min(_exactSegments, info.TotalBytes)
                : ResolveSegmentCount(info.TotalBytes, info.SupportsRange, RequestedThreads);

            if (info.TotalBytes > 0 && !HasEnoughDiskSpace(info.TotalBytes))
            {
                throw new IOException("DiskFull");
            }

            var state = TryLoadState(info, segments);
            var done = state?.Done ?? new long[segments];
            if (done.Length != segments)
            {
                done = new long[segments];
            }

            lock (_stateGate)
            {
                _segmentDone = done;
            }

            var alreadyDone = done.Sum();
            Interlocked.Exchange(ref _received, alreadyDone);

            var partPath = DownloadPaths.PartPathFor(_targetPath!);
            System.IO.Directory.CreateDirectory(TargetDirectory);

            using var handle = File.OpenHandle(
                partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);

            if (info.TotalBytes > 0)
            {
                RandomAccess.SetLength(handle, info.TotalBytes);
            }

            SetStatus(DownloadStatus.Downloading);
            SetConnections(segments);

            await DownloadSegmentsAsync(handle, info, done, segments);

            // 全部片完成：校验字节数，避免服务器提前断流却当作成功。
            var received = Interlocked.Read(ref _received);
            if (info.TotalBytes > 0 && received < info.TotalBytes)
            {
                throw new IOException($"Incomplete:{received}/{info.TotalBytes}");
            }

            handle.Close();
            File.Move(partPath, _targetPath!, overwrite: true);
            DeleteStateFile();

            SetStatus(DownloadStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            if (_pauseRequested)
            {
                SaveStateSafe();
                SetStatus(DownloadStatus.Paused);
            }
            else
            {
                CleanupPartialFiles();
                SetStatus(DownloadStatus.Canceled);
            }
        }
        catch (Exception ex)
        {
            _error = DescribeError(ex);
            SaveStateSafe();
            SetStatus(DownloadStatus.Failed);
            AppLog.Warn($"下载失败：{Url} — {ex.Message}");
        }
        finally
        {
            SetConnections(0);
            _cts.Dispose();
        }
    }

    /// <summary>确定最终文件路径：已有续传状态则沿用同名目标，否则做同名避让。</summary>
    private void PrepareTarget(RemoteFileInfo info)
    {
        var fileName = DownloadPaths.SanitizeFileName(info.FileName);
        var direct = Path.Combine(TargetDirectory, fileName);

        if (File.Exists(DownloadPaths.StatePathFor(direct)))
        {
            _targetPath = direct;
            _fileName = fileName;
            return;
        }

        _targetPath = DownloadPaths.ResolveUniquePath(TargetDirectory, fileName);
        _fileName = Path.GetFileName(_targetPath);
    }

    private async Task DownloadSegmentsAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        RemoteFileInfo info,
        long[] done,
        int segments)
    {
        if (segments <= 1)
        {
            await DownloadSingleStreamAsync(handle, info, done);
            return;
        }

        using var throttle = new SemaphoreSlim(segments);
        var tasks = new Task[segments];
        var per = info.TotalBytes / segments;

        for (var i = 0; i < segments; i++)
        {
            var index = i;
            var start = index * per;
            var end = index == segments - 1 ? info.TotalBytes - 1 : (index + 1) * per - 1;

            tasks[index] = Task.Run(async () =>
            {
                await throttle.WaitAsync(_cts.Token);
                try
                {
                    await DownloadSegmentAsync(handle, info, index, start, end, done);
                }
                finally
                {
                    throttle.Release();
                }
            }, _cts.Token);
        }

        await Task.WhenAll(tasks);
    }

    private async Task DownloadSegmentAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        RemoteFileInfo info,
        int index,
        long start,
        long end,
        long[] done)
    {
        var segmentLength = end - start + 1;

        for (var attempt = 1; ; attempt++)
        {
            _cts.Token.ThrowIfCancellationRequested();

            // 每次重试都从「该片已完成位置」续起，避免整片重下。
            var offset = Interlocked.Read(ref done[index]);
            if (offset >= segmentLength)
            {
                return;
            }

            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, info.FinalUrl);
                request.Headers.Range = new RangeHeaderValue(start + offset, end);

                using var response = await RemoteFileProbe.Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, idle.Token);

                if (response.StatusCode == HttpStatusCode.OK && segmentLength > 0)
                {
                    // 服务端忽略了 Range：多片并发会把同一份完整文件写进同一句柄，必须整体降级。
                    throw new NotSupportedException("RangeIgnored");
                }

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
                var buffer = new byte[BufferSize];

                while (true)
                {
                    var read = await stream.ReadAsync(buffer, idle.Token);
                    if (read <= 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(
                        handle, buffer.AsMemory(0, read), start + offset, idle.Token);

                    offset += read;
                    Interlocked.Exchange(ref done[index], offset);
                    Interlocked.Add(ref _received, read);

                    // 每读一块都续一次空闲超时：只约束「卡住」而不是总耗时。
                    idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                }

                if (offset < segmentLength)
                {
                    throw new IOException($"SegmentIncomplete:{offset}/{segmentLength}");
                }

                return;
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttemptsPerSegment && ex is not NotSupportedException)
            {
                AppLog.Warn($"分片 {index} 第 {attempt} 次失败，准备重试：{ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(600 * Math.Pow(2, attempt - 1)), _cts.Token);
            }
        }
    }

    /// <summary>服务器不支持 Range 时的单流下载（不可分片，也不支持暂停后续传）。</summary>
    private async Task DownloadSingleStreamAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        RemoteFileInfo info,
        long[] done)
    {
        var offset = Interlocked.Read(ref done[0]);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, info.FinalUrl);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await RemoteFileProbe.Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // 服务端忽略了 Range：本次从文件头开始写，旧偏移作废。
            offset = 0;
            Interlocked.Exchange(ref done[0], 0);
            Interlocked.Exchange(ref _received, 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
        var buffer = new byte[BufferSize];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, idle.Token);
            if (read <= 0)
            {
                break;
            }

            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, idle.Token);
            offset += read;
            Interlocked.Exchange(ref done[0], offset);
            Interlocked.Add(ref _received, read);
            idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
        }
    }

    private DownloadState? TryLoadState(RemoteFileInfo info, int segments)
    {
        try
        {
            var path = DownloadPaths.StatePathFor(_targetPath!);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize(json, DownloadStateJsonContext.Default.DownloadState);
            if (state is null ||
                state.SegmentCount != segments ||
                state.TotalBytes != info.TotalBytes ||
                !string.Equals(state.Url, Url, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // ETag 变了说明远端内容已替换，旧分片偏移无意义。
            if (!string.IsNullOrWhiteSpace(info.ETag) &&
                !string.Equals(info.ETag, state.ETag, StringComparison.Ordinal))
            {
                AppLog.Warn($"远端文件已变化（ETag 不同），丢弃旧进度重下：{Url}");
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取下载续传状态失败：{ex.Message}");
            return null;
        }
    }

    private void SaveStateSafe()
    {
        try
        {
            if (_targetPath is null)
            {
                return;
            }

            long[]? done;
            lock (_stateGate)
            {
                done = _segmentDone;
            }

            if (done is null)
            {
                return;
            }

            var state = new DownloadState
            {
                Url = Url,
                FinalUrl = Url,
                TotalBytes = Interlocked.Read(ref _total),
                SegmentCount = done.Length,
                Done = done.ToArray()
            };

            var json = JsonSerializer.Serialize(state, DownloadStateJsonContext.Default.DownloadState);
            File.WriteAllText(DownloadPaths.StatePathFor(_targetPath), json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存下载续传状态失败：{ex.Message}");
        }
    }

    private void DeleteStateFile()
    {
        try
        {
            if (_targetPath is not null)
            {
                File.Delete(DownloadPaths.StatePathFor(_targetPath));
            }
        }
        catch
        {
            // 残留状态文件不影响成品可用性。
        }
    }

    private void CleanupPartialFiles()
    {
        try
        {
            if (_targetPath is null)
            {
                return;
            }

            File.Delete(DownloadPaths.PartPathFor(_targetPath));
            File.Delete(DownloadPaths.StatePathFor(_targetPath));
        }
        catch
        {
            // 删除失败留给下次同名避让处理。
        }
    }

    private bool HasEnoughDiskSpace(long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(TargetDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace > requiredBytes + (64L * 1024 * 1024);
        }
        catch
        {
            return true;
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        IOException io when io.Message == "DiskFull" => "DiskFull",
        IOException io when io.Message.StartsWith("Incomplete:", StringComparison.Ordinal) => io.Message,
        NotSupportedException => "RangeNotSupported",
        HttpRequestException http => http.Message,
        _ => ex.Message
    };

    private void SetStatus(DownloadStatus status) => Interlocked.Exchange(ref _status, (int)status);

    private void SetConnections(int value) => Interlocked.Exchange(ref _connections, value);

    /// <summary>供 UI 节拍调用：刷新速度采样（滑动窗口式瞬时值）。</summary>
    internal void SampleSpeed(TimeSpan elapsed, long previousBytes)
    {
        if (elapsed.TotalSeconds <= 0)
        {
            return;
        }

        var delta = Interlocked.Read(ref _received) - previousBytes;
        _speed = delta > 0 ? delta / elapsed.TotalSeconds : 0;
    }
}
