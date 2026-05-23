using System.Collections.Concurrent;
using System.Threading;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IQueueService{
    void AddToQueue(EpisodeInfo episode);
    void RemoveFromQueue(string queueItemId);
    List<QueueItem> GetQueue();
    Task ProcessQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void ScheduleRetry(string queueItemId, TimeSpan delay, string statusText);
    void BlockAutoDownloadUntil(TimeSpan delay);
    void RetryAllFailed();
    void ClearQueue();
    void RetryItem(string queueItemId);
    void PauseItem(string queueItemId);
    void ResumeItem(string queueItemId);
    int ActiveDownloads { get; }
    bool HasActiveDownloads { get; }
    event EventHandler? QueueStateChanged;
}

public class QueueService : IQueueService, IDisposable{
    private readonly ConcurrentDictionary<string, QueueItem> _queue = new();
    private readonly ConcurrentDictionary<string, QueueItem> _activeDownloads = new();
    private readonly IDownloadService _downloadService;
    private readonly IQueuePersistenceService? _persistenceService;
    private readonly ILogger<QueueService>? _logger;
    private SemaphoreSlim _downloadSemaphore;
    private ProcessingSlotManager? _processingSlots;
    
    private int _pumpScheduled;
    private int _pumpDirty;
    private DateTimeOffset? _autoDownloadBlockedUntilUtc;
    private readonly object _autoDownloadBlockLock = new();
    private readonly Timer? _autoUnblockTimer;

    public int ActiveDownloads{
        get{
            return _activeDownloads.Count;
        }
    }

    public bool HasActiveDownloads => ActiveDownloads > 0;

    public event EventHandler? QueueStateChanged;

    public QueueService(IDownloadService downloadService, ILogger<QueueService>? logger = null, IQueuePersistenceService? persistenceService = null){
        _downloadService = downloadService;
        _logger = logger;
        _persistenceService = persistenceService;
        _downloadSemaphore = new SemaphoreSlim(2, int.MaxValue);
        
        if (_persistenceService != null){
            var savedQueue = _persistenceService.LoadQueue();
            if (savedQueue != null){
                foreach (var item in savedQueue){
                    _queue[item.Id] = item;
                }
                _logger?.LogInformation("Restored {Count} items from persisted queue", savedQueue.Count);
            }
        }
    }

    public void AddToQueue(EpisodeInfo episode){
        var item = new QueueItem{
            Episode = episode,
            DownloadProgress = new DownloadProgress{ State = DownloadState.Queued }
        };
        
        if (_queue.TryAdd(item.Id, item)){
            _logger?.LogInformation("Added to queue: {EpisodeId} - {Title}", episode.Id, episode.Title);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
        }
    }
    
    public void RemoveFromQueue(string queueItemId){
        if (_queue.TryRemove(queueItemId, out _)){
            _logger?.LogInformation("Removed from queue: {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
        }
    }
    
    public List<QueueItem> GetQueue(){
        return _queue.Values.ToList();
    }

    public void RetryAllFailed(){
        foreach (var item in _queue.Values.Where(i => i.DownloadProgress.IsError || i.DownloadProgress.IsWaitingForRetry)){
            item.DownloadProgress.RetryAttemptCount = 0;
            item.DownloadProgress.State = DownloadState.Queued;
            item.DownloadProgress.RetryAtUtc = null;
            item.DownloadProgress.Doing = "Retrying...";
            _logger?.LogInformation("Retrying item {QueueItemId}", item.Id);
        }
        OnQueueStateChanged();
        ScheduleSave();
        RequestPump();
    }

    public void ClearQueue(){
        _queue.Clear();
        _logger?.LogInformation("Queue cleared");
        OnQueueStateChanged();
        ScheduleSave();
    }

    public void RetryItem(string queueItemId){
        if (_queue.TryGetValue(queueItemId, out var item)){
            item.DownloadProgress.RetryAttemptCount = 0;
            item.DownloadProgress.State = DownloadState.Queued;
            item.DownloadProgress.RetryAtUtc = null;
            item.DownloadProgress.Doing = "Retrying...";
            _logger?.LogInformation("Retrying item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
        }
    }

    public void PauseItem(string queueItemId){
        if (_queue.TryGetValue(queueItemId, out var item)){
            item.DownloadProgress.State = DownloadState.Paused;
            item.DownloadProgress.Doing = "Paused";
            _logger?.LogInformation("Paused item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
        }
    }

    public void ResumeItem(string queueItemId){
        if (_queue.TryGetValue(queueItemId, out var item)){
            item.DownloadProgress.State = DownloadState.Queued;
            item.DownloadProgress.Doing = "Queued";
            _logger?.LogInformation("Resumed item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
        }
    }

    public void ScheduleRetry(string queueItemId, TimeSpan delay, string statusText){
        if (_queue.TryGetValue(queueItemId, out var item)){
            item.DownloadProgress.ScheduleRetry(delay, statusText);
            _logger?.LogInformation("Scheduled retry for {QueueItemId} in {Delay}s: {Status}", queueItemId, delay.TotalSeconds, statusText);
            OnQueueStateChanged();
            ScheduleSave();
            
            _ = Task.Run(async () =>{
                try{
                    await Task.Delay(delay, CancellationToken.None);
                    item.DownloadProgress.RetryAtUtc = null;
                    OnQueueStateChanged();
                    RequestPump();
                } catch (OperationCanceledException){
                    // ignored
                }
            });
        }
    }

    public void BlockAutoDownloadUntil(TimeSpan delay){
        var unblockAt = DateTimeOffset.UtcNow.Add(delay);

        lock (_autoDownloadBlockLock){
            if (!_autoDownloadBlockedUntilUtc.HasValue || unblockAt > _autoDownloadBlockedUntilUtc.Value){
                _autoDownloadBlockedUntilUtc = unblockAt;
            } else{
                unblockAt = _autoDownloadBlockedUntilUtc.Value;
            }
        }

        _logger?.LogInformation("Auto-download blocked until {UnblockAt}", unblockAt);

        _ = Task.Run(async () =>{
            try{
                var remaining = unblockAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero){
                    await Task.Delay(remaining, CancellationToken.None);
                }

                lock (_autoDownloadBlockLock){
                    if (_autoDownloadBlockedUntilUtc.HasValue && _autoDownloadBlockedUntilUtc.Value <= DateTimeOffset.UtcNow){
                        _autoDownloadBlockedUntilUtc = null;
                    }
                }

                OnQueueStateChanged();
                RequestPump();
            } catch (OperationCanceledException){
                // ignored
            }
        });
    }
    
    public async Task ProcessQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
        _downloadSemaphore = new SemaphoreSlim(config.Download.SimultaneousDownloads, int.MaxValue);
        _processingSlots = new ProcessingSlotManager(config.Queue.SimultaneousProcessingJobs);

        _logger?.LogInformation("Queue processor started with {Downloads} concurrent downloads, {Processing} processing jobs", 
            config.Download.SimultaneousDownloads, config.Queue.SimultaneousProcessingJobs);

        // Start the pump loop
        _ = Task.Run(() => PumpLoopAsync(config, progress, cancellationToken), cancellationToken);

        // Keep running until cancellation
        try{
            await Task.Delay(Timeout.Infinite, cancellationToken);
        } catch (OperationCanceledException){
            _logger?.LogInformation("Queue processor stopped");
        }
    }

    private async Task PumpLoopAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken){
        while (!cancellationToken.IsCancellationRequested){
            if (config.Queue.AutoDownload){
                await PumpQueueAsync(config, progress, cancellationToken);
            }
            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task PumpQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken){
        lock (_autoDownloadBlockLock){
            if (_autoDownloadBlockedUntilUtc.HasValue && !HasPendingRetryItems()){
                _autoDownloadBlockedUntilUtc = null;
            }

            if (_autoDownloadBlockedUntilUtc.HasValue && _autoDownloadBlockedUntilUtc.Value > DateTimeOffset.UtcNow){
                return;
            }

            if (_autoDownloadBlockedUntilUtc.HasValue){
                _autoDownloadBlockedUntilUtc = null;
            }
        }

        var toStart = new List<QueueItem>();
        
        foreach (var item in _queue.Values.OrderBy(q => q.AddedAt)){
            if (_activeDownloads.Count >= config.Download.SimultaneousDownloads)
                break;

            if (item.DownloadProgress.IsError)
                continue;

            if (item.DownloadProgress.IsWaitingForRetry)
                continue;

            if (item.DownloadProgress.IsDone)
                continue;

            if (item.DownloadProgress.State is DownloadState.Downloading or DownloadState.Processing)
                continue;

            if (_activeDownloads.ContainsKey(item.Id))
                continue;

            if (_downloadSemaphore.CurrentCount == 0)
                break;

            toStart.Add(item);
        }

        foreach (var item in toStart){
            _ = Task.Run(async () =>{
                await _downloadSemaphore.WaitAsync(cancellationToken);
                try{
                    _activeDownloads[item.Id] = item;
                    item.DownloadProgress.State = DownloadState.Downloading;
                    item.DownloadProgress.Doing = "Starting download...";
                    OnQueueStateChanged();
                    ScheduleSave();

                    _logger?.LogInformation("Starting download: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);

                    try{
                        var result = await _downloadService.DownloadEpisodeAsync(item.Episode.Id, config, progress, cancellationToken);
                        
                        if (!result.Success){
                            throw new Exception(result.ErrorMessage ?? "Download failed");
                        }
                        
                        item.DownloadProgress.State = DownloadState.Done;
                        item.DownloadProgress.Percent = 100;
                        item.DownloadProgress.Doing = "Complete";
                        _logger?.LogInformation("Download complete: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);
                    } catch (OperationCanceledException){
                        item.DownloadProgress.State = DownloadState.Cancelled;
                        item.DownloadProgress.Doing = "Cancelled";
                        _logger?.LogInformation("Download cancelled: {EpisodeId}", item.Episode.Id);
                    } catch (Exception ex){
                        _logger?.LogError(ex, "Download failed: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);
                        
                        if (item.DownloadProgress.RetryAttemptCount < config.Download.RetryAttempts){
                            var delay = TimeSpan.FromSeconds(config.Download.RetryDelaySeconds * Math.Pow(3, item.DownloadProgress.RetryAttemptCount));
                            ScheduleRetry(item.Id, delay, $"Error: {ex.Message}");
                        } else{
                            item.DownloadProgress.State = DownloadState.Error;
                            item.DownloadProgress.Doing = $"Error: {ex.Message}";
                        }
                    }
                } finally{
                    _activeDownloads.TryRemove(item.Id, out _);
                    _downloadSemaphore.Release();
                    OnQueueStateChanged();
                    ScheduleSave();
                    
                    if (config.Queue.AutoDownload){
                        RequestPump();
                    }
                }
            }, cancellationToken);
        }
    }

    private bool HasPendingRetryItems(){
        return _queue.Values.Any(item => item.DownloadProgress.IsWaitingForRetry);
    }

    private void RequestPump(){
        Interlocked.Exchange(ref _pumpDirty, 1);

        if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>{
            try{
                while (Interlocked.Exchange(ref _pumpDirty, 0) == 1){
                    await Task.Delay(100);
                }
            } finally{
                Interlocked.Exchange(ref _pumpScheduled, 0);

                if (Volatile.Read(ref _pumpDirty) == 1 &&
                    Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0){
                    _ = Task.Run(async () =>{
                        await Task.Delay(100);
                        RequestPump();
                    });
                }
            }
        });
    }

    private void ScheduleSave(){
        if (_persistenceService != null){
            var queue = GetQueue();
            _persistenceService.ScheduleSave(queue);
        }
    }

    private void OnQueueStateChanged(){
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose(){
        _downloadSemaphore?.Dispose();
        _processingSlots?.Dispose();
        _autoUnblockTimer?.Dispose();
        _persistenceService?.Dispose();
    }
}
