using Newtonsoft.Json;
using Cruncharr.Core.Models;
using Cruncharr.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IQueuePersistenceService : IDisposable
{
    void SaveQueue(List<QueueItem> queue);
    void ScheduleSave(List<QueueItem> queue);
    List<QueueItem>? LoadQueue();
    void DeleteQueue();
}

public class QueuePersistenceService : IQueuePersistenceService, IDisposable
{
    private readonly string _queueFilePath;
    private readonly object _syncLock = new();
    private Timer? _saveTimer;
    private readonly ILogger<QueuePersistenceService>? _logger;
    private readonly CruncharrConfig? _config;

    // When false (PersistQueue disabled), load/save are no-ops (upstream QueuePersistenceManager).
    private bool PersistEnabled => _config?.Queue.PersistQueue ?? true;

    public QueuePersistenceService(string queueFilePath, ILogger<QueuePersistenceService>? logger = null, CruncharrConfig? config = null)
    {
        _queueFilePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
        _logger = logger;
        _config = config;
        var dir = Path.GetDirectoryName(_queueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void SaveQueue(List<QueueItem> queue)
    {
        lock (_syncLock)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        PersistQueue(queue);
    }

    public void ScheduleSave(List<QueueItem> queue)
    {
        // Save immediately instead of using timer (timer may be trimmed in release builds)
        PersistQueue(queue);
    }

    public List<QueueItem>? LoadQueue()
    {
        if (!PersistEnabled)
            return null;

        if (!File.Exists(_queueFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(_queueFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var queue = JsonConvert.DeserializeObject<List<QueueItem>>(json);

            if (queue != null)
            {
                foreach (var item in queue)
                {
                    PrepareRestoredItem(item);
                }
            }

            return queue;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load queue from {Path}", _queueFilePath);
            return null;
        }
    }

    public void DeleteQueue()
    {
        if (File.Exists(_queueFilePath))
        {
            File.Delete(_queueFilePath);
        }
    }

    private void PersistQueue(List<QueueItem> queue)
    {
        // PersistQueue disabled: do not write a snapshot to disk.
        if (!PersistEnabled)
            return;

        if (queue.Count == 0)
        {
            DeleteQueue();
            return;
        }

        var snapshot = queue
            .Where(item => !item.DownloadProgress.IsFinished)
            .Select(CloneForPersistence)
            .Where(item => item != null)
            .ToList();

        if (snapshot.Count == 0)
        {
            DeleteQueue();
            return;
        }

        var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

        try
        {
            // Atomic write (temp + rename) so a crash mid-write can't corrupt the queue file.
            var tmp = _queueFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _queueFilePath, overwrite: true);
            _logger?.LogDebug("Queue persisted: {Count} items to {Path}", snapshot.Count, _queueFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write queue file to {Path}", _queueFilePath);
        }
    }

    private static void PrepareRestoredItem(QueueItem item)
    {
        item.DownloadProgress ??= new DownloadProgress();

        if (item.DownloadProgress.RetryAtUtc.HasValue)
        {
            if (item.DownloadProgress.RetryAtUtc.Value <= DateTimeOffset.UtcNow)
            {
                item.DownloadProgress.ResetForRetry();
            }
            else
            {
                item.DownloadProgress.State = DownloadState.Queued;
                item.DownloadProgress.ResumeState = DownloadState.Downloading;
            }
        }
        else if (!item.DownloadProgress.IsFinished)
        {
            item.DownloadProgress.ResetForRetry();
        }
    }

    private static QueueItem? CloneForPersistence(QueueItem item)
    {
        try
        {
            var json = JsonConvert.SerializeObject(item);
            return JsonConvert.DeserializeObject<QueueItem>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }
}
