using System.Collections.Concurrent;
using System.Threading;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.DependencyInjection;
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
    void StartItem(string queueItemId);
    int ActiveDownloads { get; }
    bool HasActiveDownloads { get; }
    event EventHandler? QueueStateChanged;
    
    // Replace entire queue
    void ReplaceQueue(List<QueueItem> newQueue);
    
    // Processing slot management (ported from upstream QueueManager)
    Task WaitForProcessingSlotAsync(CancellationToken cancellationToken = default);
    void ReleaseProcessingSlot();
    void SetProcessingLimit(int newLimit);
}

public class QueueService : IQueueService, IDisposable{
    private readonly ConcurrentDictionary<string, QueueItem> _queue = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IQueuePersistenceService? _persistenceService;
    private readonly ILogger<QueueService>? _logger;
    private ProcessingSlotManager? _processingSlots;
    private CruncharrConfig? _config;
    private CancellationToken _cancellationToken;
    
    // Ported from upstream: HashSet with lock for active download tracking
    private readonly object _downloadStartLock = new();
    private readonly HashSet<string> _activeOrStarting = new();
    
    // Ported from upstream: pump scheduling
    private int _pumpScheduled;
    private int _pumpDirty;
    
    // Ported from upstream: auto-download blocking
    private DateTimeOffset? _autoDownloadBlockedUntilUtc;
    private readonly object _autoDownloadBlockLock = new();

    public int ActiveDownloads{
        get{
            lock (_downloadStartLock){
                return _activeOrStarting.Count;
            }
        }
    }

    public bool HasActiveDownloads => ActiveDownloads > 0;

    public event EventHandler? QueueStateChanged;

    public QueueService(IServiceProvider serviceProvider, ILogger<QueueService>? logger = null, IQueuePersistenceService? persistenceService = null){
        _serviceProvider = serviceProvider;
        _logger = logger;
        _persistenceService = persistenceService;
        
        // Restore persisted queue
        if (_persistenceService != null){
            var savedQueue = _persistenceService.LoadQueue();
            if (savedQueue != null){
                foreach (var item in savedQueue){
                    _queue[item.Id] = item;
                }
                _logger?.LogInformation("Restored {Count} items from persisted queue", savedQueue.Count);
                RestoreRetryStateFromQueue();
            }
        }
    }

    // Ported from upstream: Restore retry states when queue is loaded from persistence
    private void RestoreRetryStateFromQueue(){
        var retryItems = _queue.Values.Where(i => i.DownloadProgress.IsWaitingForRetry).ToList();
        if (retryItems.Count == 0) return;

        var earliestRetry = retryItems.Min(i => i.DownloadProgress.RetryAtUtc);
        if (earliestRetry.HasValue){
            lock (_autoDownloadBlockLock){
                _autoDownloadBlockedUntilUtc = earliestRetry.Value;
            }
            _logger?.LogInformation("Restored retry state: {Count} items waiting, blocked until {Time}", retryItems.Count, earliestRetry.Value);
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

    public void ReplaceQueue(List<QueueItem> newQueue){
        _queue.Clear();
        foreach (var item in newQueue){
            if (item != null){
                _queue[item.Id] = item;
            }
        }
        _logger?.LogInformation("Queue replaced with {Count} items", newQueue.Count);
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

    // Ported from upstream QueueManager.TryStartDownload
    public void StartItem(string queueItemId){
        if (_queue.TryGetValue(queueItemId, out var item)){
            if (!TryStartDownload(item)){
                _logger?.LogWarning("Cannot start {QueueItemId} - already active or no slots", queueItemId);
                return;
            }
            
            _ = Task.Run(async () =>{
                try{
                    await RunDownloadAsync(item, _cancellationToken);
                } finally{
                    ReleaseDownloadSlot(item);
                }
            }, _cancellationToken);
        }
    }

    // Ported from upstream QueueManager.TryStartDownload
    private bool TryStartDownload(QueueItem item){
        lock (_downloadStartLock){
            if (_activeOrStarting.Contains(item.Id))
                return false;

            if (item.DownloadProgress.State is DownloadState.Downloading or DownloadState.Processing)
                return false;

            if (item.DownloadProgress.IsDone)
                return false;

            if (item.DownloadProgress.IsError)
                return false;

            if (item.DownloadProgress.IsPaused)
                return false;

            if (_activeOrStarting.Count >= (_config?.Download.SimultaneousDownloads ?? 2))
                return false;

            _activeOrStarting.Add(item.Id);
        }
        
        NotifyDownloadStateChanged();
        OnQueueStateChanged();
        return true;
    }

    // Ported from upstream QueueManager.ReleaseDownloadSlot
    private void ReleaseDownloadSlot(QueueItem item){
        bool removed;

        lock (_downloadStartLock){
            removed = _activeOrStarting.Remove(item.Id);
        }

        if (removed){
            NotifyDownloadStateChanged();
            OnQueueStateChanged();

            if (_config?.Queue.AutoDownload == true){
                RequestPump();
            }
            
            // Check if queue is empty and shutdown is enabled
            CheckShutdownWhenQueueEmpty();
        }
    }
    
    private void CheckShutdownWhenQueueEmpty(){
        if (_config?.Queue.ShutdownWhenQueueEmpty != true)
            return;
        
        bool hasUnfinishedItems = _queue.Values.Any(q => !q.DownloadProgress.IsDone && !q.DownloadProgress.IsError);
        
        lock (_downloadStartLock){
            if (!hasUnfinishedItems && _activeOrStarting.Count == 0){
                _logger?.LogInformation("Queue is empty and ShutdownWhenQueueEmpty is enabled - shutting down");
                _config.Queue.ShutdownWhenQueueEmpty = false;
                
                // Trigger application shutdown
                _ = Task.Run(() =>{
                    try{
                        Environment.Exit(0);
                    } catch (Exception ex){
                        _logger?.LogError(ex, "Failed to shutdown application");
                    }
                });
            }
        }
    }

    public async Task ProcessQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
        _config = config;
        _cancellationToken = cancellationToken;
        _processingSlots = new ProcessingSlotManager(config.Queue.SimultaneousProcessingJobs);

        _logger?.LogInformation("Queue processor started with {Downloads} concurrent downloads, {Processing} processing jobs", 
            config.Download.SimultaneousDownloads, config.Queue.SimultaneousProcessingJobs);

        // Keep running until cancellation
        try{
            await Task.Delay(Timeout.Infinite, cancellationToken);
        } catch (OperationCanceledException){
            _logger?.LogInformation("Queue processor stopped");
        }
    }

    // Ported from upstream QueueManager.RunPump + PumpQueue
    private void RequestPump(){
        if (_config?.Queue.AutoDownload != true) return;
        
        Interlocked.Exchange(ref _pumpDirty, 1);

        if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>{
            try{
                while (Interlocked.Exchange(ref _pumpDirty, 0) == 1){
                    await PumpQueueAsync();
                    await Task.Delay(100, _cancellationToken);
                }
            } finally{
                Interlocked.Exchange(ref _pumpScheduled, 0);

                if (Volatile.Read(ref _pumpDirty) == 1 &&
                    Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0){
                    _ = Task.Run(async () =>{
                        await Task.Delay(100, _cancellationToken);
                        RequestPump();
                    });
                }
            }
        });
    }

    // Ported from upstream QueueManager.PumpQueue
    private async Task PumpQueueAsync(){
        if (_config == null) return;
        if (!_config.Queue.AutoDownload) return;

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
        bool changed = false;
        
        lock (_downloadStartLock){
            int limit = _config.Download.SimultaneousDownloads;
            int freeSlots = Math.Max(0, limit - _activeOrStarting.Count);

            if (freeSlots == 0)
                return;

            foreach (var item in _queue.Values.OrderBy(q => q.AddedAt)){
                if (freeSlots == 0)
                    break;

                if (item.DownloadProgress.IsError)
                    continue;

                if (item.DownloadProgress.IsWaitingForRetry)
                    continue;

                if (item.DownloadProgress.IsDone)
                    continue;

                if (item.DownloadProgress.State is DownloadState.Downloading or DownloadState.Processing)
                    continue;

                if (_activeOrStarting.Contains(item.Id))
                    continue;

                _activeOrStarting.Add(item.Id);
                freeSlots--;
                toStart.Add(item);
                changed = true;
            }
        }
        
        if (changed){
            NotifyDownloadStateChanged();
        }

        foreach (var item in toStart){
            _ = Task.Run(async () =>{
                try{
                    await RunDownloadAsync(item, _cancellationToken);
                } finally{
                    ReleaseDownloadSlot(item);
                }
            }, _cancellationToken);
        }

        OnQueueStateChanged();
    }

    // Extracted download execution logic
    private async Task RunDownloadAsync(QueueItem item, CancellationToken cancellationToken){
        item.DownloadProgress.State = DownloadState.Downloading;
        item.DownloadProgress.Doing = "Starting download...";
        OnQueueStateChanged();
        ScheduleSave();

        _logger?.LogInformation("Starting download: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);

        try{
            var queueProgress = new Progress<DownloadProgress>(p =>{
                item.DownloadProgress.State = p.State;
                item.DownloadProgress.Percent = p.Percent;
                item.DownloadProgress.Doing = p.Doing;
                item.DownloadProgress.DownloadSpeedBytes = p.DownloadSpeedBytes;
                item.DownloadProgress.Time = p.Time;
                OnQueueStateChanged();
            });
            
            var downloadService = _serviceProvider.GetRequiredService<IDownloadService>();
            var result = await downloadService.DownloadEpisodeAsync(item.Episode, _config!, queueProgress, cancellationToken, onDownloadComplete: () =>{
                // Release download slot early so next download can start while this one is still processing (muxing/encoding)
                if (_config?.Download.DownloadAllowEarlyStart == true){
                    ReleaseDownloadSlot(item);
                }
            });
            
            if (!result.Success){
                throw new Exception(result.ErrorMessage ?? "Download failed");
            }
            
            item.DownloadProgress.State = DownloadState.Done;
            item.DownloadProgress.Percent = 100;
            item.DownloadProgress.Doing = "Complete";
            _logger?.LogInformation("Download complete: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);
            
            if (_config?.RemoveFinishedDownload == true){
                _queue.TryRemove(item.Id, out _);
                _logger?.LogInformation("Removed finished download from queue: {EpisodeId}", item.Episode.Id);
            }
        } catch (DownloadException dex){
            _logger?.LogError("Download failed: {ErrorType} - {Message}", dex.ErrorType, dex.Message);
            
            bool isAuthError = dex.ErrorType == DownloadErrorType.NotAuthenticated ||
                              dex.ErrorType == DownloadErrorType.SubscriptionExpired ||
                              dex.ErrorType == DownloadErrorType.PremiumContent ||
                              dex.ErrorType == DownloadErrorType.MaturityRating;
            
            if (!isAuthError && item.DownloadProgress.RetryAttemptCount < (_config?.Download.RetryAttempts ?? 5)){
                var delay = TimeSpan.FromSeconds(Math.Max(1, (_config?.Download.RetryDelaySeconds ?? 5) * Math.Pow(3, item.DownloadProgress.RetryAttemptCount)));
                ScheduleRetry(item.Id, delay, $"Error: {dex.Message}");
            } else{
                item.DownloadProgress.State = DownloadState.Error;
                item.DownloadProgress.Doing = $"Error: {dex.Message}";
            }
        } catch (OperationCanceledException){
            item.DownloadProgress.State = DownloadState.Cancelled;
            item.DownloadProgress.Doing = "Cancelled";
            _logger?.LogInformation("Download cancelled: {EpisodeId}", item.Episode.Id);
        } catch (Exception ex){
            _logger?.LogError(ex, "Download failed: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);
            
            if (item.DownloadProgress.RetryAttemptCount < (_config?.Download.RetryAttempts ?? 5)){
                var delay = TimeSpan.FromSeconds(Math.Max(1, (_config?.Download.RetryDelaySeconds ?? 5) * Math.Pow(3, item.DownloadProgress.RetryAttemptCount)));
                ScheduleRetry(item.Id, delay, $"Error: {ex.Message}");
            } else{
                item.DownloadProgress.State = DownloadState.Error;
                item.DownloadProgress.Doing = $"Error: {ex.Message}";
            }
        }
        
        OnQueueStateChanged();
        ScheduleSave();
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

    private bool HasPendingRetryItems(){
        return _queue.Values.Any(item => item.DownloadProgress.IsWaitingForRetry);
    }

    private void NotifyDownloadStateChanged(){
        OnQueueStateChanged();
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

    public async Task WaitForProcessingSlotAsync(CancellationToken cancellationToken = default){
        if (_processingSlots != null){
            await _processingSlots.WaitAsync(cancellationToken);
        }
    }

    public void ReleaseProcessingSlot(){
        _processingSlots?.Release();
    }

    public void SetProcessingLimit(int newLimit){
        _processingSlots?.SetLimit(newLimit);
    }

    public void Dispose(){
        _processingSlots?.Dispose();
    }
}
