using Newtonsoft.Json;
using Cruncharr.Core.Models;
using Cruncharr.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IQueuePersistenceService : IDisposable
{
    // Both return whether the queue is durably on disk; failures are logged, not thrown.
    bool SaveQueue(List<QueueItem> queue);
    bool ScheduleSave(List<QueueItem> queue);
    List<QueueItem>? LoadQueue();
    void DeleteQueue();
}

public class QueuePersistenceService : IQueuePersistenceService, IDisposable
{
    private readonly string _initialQueueFilePath;
    private string _queueFilePath => _config?.Queue.QueueFilePath ?? _initialQueueFilePath;
    private readonly object _syncLock = new();
    private Timer? _saveTimer;
    private readonly ILogger<QueuePersistenceService>? _logger;
    private readonly CruncharrConfig? _config;
    // Set when the existing queue file could not be read or set aside; saves are refused while set.
    private string? _loadError;

    // Queue persistence is always on: a downloader that loses its queue on every container
    // restart (backup jobs, updates) is worse than a small queue.json in /config. This
    // guarantees downloads/transcodes resume after a restart regardless of the (legacy)
    // PersistQueue toggle, which some users had saved as false before this was the default.
    private bool PersistEnabled => true;

    public QueuePersistenceService(string queueFilePath, ILogger<QueuePersistenceService>? logger = null, CruncharrConfig? config = null)
    {
        _initialQueueFilePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
        _logger = logger;
        _config = config;
        var dir = Path.GetDirectoryName(_queueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            // Never let an unwritable queue location abort startup; SaveQueue reports the
            // failure on the first save instead and downloads still run.
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Queue directory {Dir} could not be created; the queue will not persist until the path is fixed", dir);
            }
        }
    }

    public bool SaveQueue(List<QueueItem> queue)
    {
        lock (_syncLock)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return PersistQueueCore(queue);
        }
    }

    public bool ScheduleSave(List<QueueItem> queue)
    {
        // Save immediately instead of using timer (timer may be trimmed in release builds)
        lock (_syncLock)
        {
            return PersistQueueCore(queue);
        }
    }

    public List<QueueItem>? LoadQueue()
    {
        if (!PersistEnabled)
            return null;

        if (!File.Exists(_queueFilePath))
            return null;

        string json;
        try
        {
            json = File.ReadAllText(_queueFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadError = $"Could not read the queue file {_queueFilePath}: {ex.Message} " +
                         "The queue will not be saved over it until the file can be read and Cruncharr is restarted.";
            _logger?.LogError(ex, "{Error}", _loadError);
            return null;
        }
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
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
            // Keep the damaged file aside so the next save cannot replace the only copy.
            var damaged = $"{_queueFilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_queueFilePath, damaged);
                _logger?.LogError(ex, "Queue file {Path} could not be read and was kept as {Damaged}; starting with an empty queue", _queueFilePath, damaged);
            }
            catch (Exception moveEx)
            {
                _loadError = $"Could not load the queue file {_queueFilePath}: {ex.Message} " +
                             "The queue will not be saved over it until the file is fixed and Cruncharr is restarted.";
                _logger?.LogError(moveEx, "{Error}", _loadError);
            }
            return null;
        }
    }

    public void DeleteQueue()
    {
        lock (_syncLock)
        {
            if (_loadError != null) return;
            DeleteQueueCore();
        }
    }

    private void DeleteQueueCore()
    {
        if (File.Exists(_queueFilePath))
        {
            File.Delete(_queueFilePath);
        }
    }

    private bool PersistQueueCore(List<QueueItem> queue)
    {
        // PersistQueue disabled: do not write a snapshot to disk.
        if (!PersistEnabled)
            return true;

        // The saved queue could not be loaded, so this in-memory queue is incomplete: writing or
        // deleting the file would lose the saved items. Report every save as not durable.
        if (_loadError != null)
        {
            _logger?.LogError("Queue not saved: {Error}", _loadError);
            return false;
        }

        if (queue.Count == 0)
        {
            return TryDeleteQueueCore();
        }

        var snapshot = queue
            .Where(item => !item.DownloadProgress.IsFinished)
            .Select(CloneForPersistence)
            .Where(item => item != null)
            .ToList();

        if (snapshot.Count == 0)
        {
            return TryDeleteQueueCore();
        }

        var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

        try
        {
            var directory = Path.GetDirectoryName(_queueFilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            // Atomic write (temp + rename) so a crash mid-write can't corrupt the queue file.
            var tmp = _queueFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _queueFilePath, overwrite: true);
            _logger?.LogDebug("Queue persisted: {Count} items to {Path}", snapshot.Count, _queueFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write queue file to {Path}", _queueFilePath);
            return false;
        }
    }

    private bool TryDeleteQueueCore()
    {
        try
        {
            DeleteQueueCore();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete queue file {Path}", _queueFilePath);
            return false;
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
        else if (item.DownloadProgress.State is DownloadState.Downloading or DownloadState.Processing)
        {
            // Only work that was interrupted mid-flight is restarted. Paused/Cancelled are
            // intentional user states and must survive a container restart unchanged.
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
