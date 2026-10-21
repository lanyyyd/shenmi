using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 协助申请记录：内存里维护最近 50 条 / 24 小时，并落盘到
/// %AppData%\SteamEYA\assist\requests.json，重启后申请列表仍在。
///
/// 重启后残留的 Pending 一律按 Expired 处理——TCP 会话已经不存在，点"同意"也没意义。
/// </summary>
internal sealed class AssistRequestStore
{
    private const int MaxRecords = 50;
    private static readonly TimeSpan Retain = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly List<AssistRequestRecord> _items = [];

    public AssistRequestStore()
    {
        Load();
    }

    /// <summary>列表内容或状态变化（可能在后台线程触发，UI 需自行封送）。</summary>
    public event Action? Changed;

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamEYA", "assist");

    private static string FilePath => Path.Combine(Directory, "requests.json");

    public IReadOnlyList<AssistRequestRecord> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public AssistRequestRecord? Find(string requestId)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(r => r.RequestId == requestId);
        }
    }

    public AssistRequestRecord Add(string requestId, string uid, string nick, string ip, bool inbound)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new AssistRequestRecord
        {
            RequestId = requestId,
            FromUid = uid,
            FromNick = nick,
            FromIp = ip,
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = now.AddMinutes(5).ToUnixTimeMilliseconds(),
            Status = AssistRequestStatus.Pending,
            Inbound = inbound,
        };

        lock (_gate)
        {
            _items.Insert(0, record);
            Trim();
        }

        Save();
        Changed?.Invoke();
        return record;
    }

    public void UpdateStatus(string requestId, AssistRequestStatus status)
    {
        var changed = false;
        lock (_gate)
        {
            var record = _items.FirstOrDefault(r => r.RequestId == requestId);
            if (record is not null && record.Status != status)
            {
                record.Status = status;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>把所有超过 5 分钟仍为 Pending 的记录标成 Expired；返回本次过期的记录。</summary>
    public List<AssistRequestRecord> ExpireOverdue()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = new List<AssistRequestRecord>();

        lock (_gate)
        {
            foreach (var record in _items)
            {
                if (record.Status == AssistRequestStatus.Pending && record.ExpiresAt <= now)
                {
                    record.Status = AssistRequestStatus.Expired;
                    expired.Add(record);
                }
            }
        }

        if (expired.Count > 0)
        {
            Save();
            Changed?.Invoke();
        }
        return expired;
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(Retain).ToUnixTimeMilliseconds();
        _items.RemoveAll(r => r.CreatedAt < cutoff);
        if (_items.Count > MaxRecords)
        {
            _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var json = File.ReadAllBytes(FilePath);
            var file = JsonSerializer.Deserialize(json, AssistJsonContext.Default.AssistRequestFile);
            if (file?.Items is null)
            {
                return;
            }

            lock (_gate)
            {
                _items.AddRange(file.Items);
                // 进程重启后，未处理的申请一律失效。
                foreach (var record in _items.Where(r => r.Status == AssistRequestStatus.Pending))
                {
                    record.Status = AssistRequestStatus.Expired;
                }
                Trim();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取协助申请记录失败：{ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            AssistRequestFile file;
            lock (_gate)
            {
                file = new AssistRequestFile { Items = [.. _items] };
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(file, AssistJsonContext.Default.AssistRequestFile);
            File.WriteAllBytes(FilePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存协助申请记录失败：{ex.Message}");
        }
    }
}
