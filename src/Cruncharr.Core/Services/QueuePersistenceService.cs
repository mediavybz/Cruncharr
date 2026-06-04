using System.Text.Json;
using Cruncharr.Core.Models;

namespace Cruncharr.Core.Services;

public interface IQueuePersistenceService : IDisposable{
    void SaveQueue(List<QueueItem> queue);
    void ScheduleSave(List<QueueItem> queue);
    List<QueueItem>? LoadQueue();
    void DeleteQueue();
}

public class QueuePersistenceService : IQueuePersistenceService, IDisposable{
    private readonly string _queueFilePath;
    private readonly object _syncLock = new();
    private Timer? _saveTimer;
    private List<QueueItem>? _latestQueue;

    public QueuePersistenceService(string queueFilePath){
        _queueFilePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
        Directory.CreateDirectory(Path.GetDirectoryName(_queueFilePath)!);
    }

    public void SaveQueue(List<QueueItem> queue){
        lock (_syncLock){
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        PersistQueue(queue);
    }

    public void ScheduleSave(List<QueueItem> queue){
        lock (_syncLock){
            _latestQueue = queue;
            if (_saveTimer == null){
                _saveTimer = new Timer(_ => {
                    List<QueueItem>? q;
                    lock (_syncLock){
                        q = _latestQueue;
                    }
                    if (q != null) PersistQueue(q);
                }, null, TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
                return;
            }

            _saveTimer.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
        }
    }

    public List<QueueItem>? LoadQueue(){
        if (!File.Exists(_queueFilePath))
            return null;

        try{
            var json = File.ReadAllText(_queueFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var queue = JsonSerializer.Deserialize<List<QueueItem>>(json, new JsonSerializerOptions{
                PropertyNameCaseInsensitive = true
            });

            if (queue != null){
                foreach (var item in queue){
                    PrepareRestoredItem(item);
                }
            }

            return queue;
        } catch{
            return null;
        }
    }

    public void DeleteQueue(){
        if (File.Exists(_queueFilePath)){
            File.Delete(_queueFilePath);
        }
    }

    private void PersistQueue(List<QueueItem> queue){
        if (queue.Count == 0){
            DeleteQueue();
            return;
        }

        var snapshot = queue
            .Where(item => !item.DownloadProgress.IsFinished)
            .Select(CloneForPersistence)
            .Where(item => item != null)
            .ToList();

        if (snapshot.Count == 0){
            DeleteQueue();
            return;
        }

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions{
            WriteIndented = true
        });

        try{
            File.WriteAllText(_queueFilePath, json);
        } catch{
            // Ignore write failures - queue will be re-persisted on next change
        }
    }

    private static void PrepareRestoredItem(QueueItem item){
        item.DownloadProgress ??= new DownloadProgress();

        if (item.DownloadProgress.RetryAtUtc.HasValue){
            if (item.DownloadProgress.RetryAtUtc.Value <= DateTimeOffset.UtcNow){
                item.DownloadProgress.ResetForRetry();
            } else{
                item.DownloadProgress.State = DownloadState.Queued;
                item.DownloadProgress.ResumeState = DownloadState.Downloading;
            }
        } else if (!item.DownloadProgress.IsFinished){
            item.DownloadProgress.ResetForRetry();
        }
    }

    private static QueueItem? CloneForPersistence(QueueItem item){
        try{
            var json = JsonSerializer.Serialize(item);
            return JsonSerializer.Deserialize<QueueItem>(json);
        } catch{
            return null;
        }
    }

    public void Dispose(){
        lock (_syncLock){
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }
}
