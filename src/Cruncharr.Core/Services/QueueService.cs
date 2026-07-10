using System.Collections.Concurrent;
using System.Threading;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IQueueService
{
    void AddToQueue(EpisodeInfo episode);
    bool RemoveFromQueue(string queueItemId);
    List<QueueItem> GetQueue();
    Task ProcessQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void ScheduleRetry(string queueItemId, TimeSpan delay, string statusText);
    void BlockAutoDownloadUntil(TimeSpan delay);
    void RetryAllFailed();
    void ClearQueue();
    bool RetryItem(string queueItemId);
    bool PauseItem(string queueItemId);
    bool ResumeItem(string queueItemId);
    bool StartItem(string queueItemId);
    int ActiveDownloads { get; }
    bool HasActiveDownloads { get; }
    bool ShutdownRequested { get; }
    event EventHandler? QueueStateChanged;

    // Replace entire queue
    void ReplaceQueue(List<QueueItem> newQueue);

    // Processing slot management (ported from upstream QueueManager)
    Task WaitForProcessingSlotAsync(CancellationToken cancellationToken = default);
    void ReleaseProcessingSlot();
    void SetProcessingLimit(int newLimit);

    // Transcode (encode-step) slot management — separate limit from processing jobs.
    Task WaitForTranscodeSlotAsync(CancellationToken cancellationToken = default);
    void ReleaseTranscodeSlot();
    void SetTranscodeLimit(int newLimit);

    // Init-completion gate (ported from upstream c123093)
    void SetInitialized(bool initialized);
    bool IsGloballyPaused { get; }
    void PauseGlobally();
    void ResumeGlobally();
}

public class QueueService : IQueueService, IDisposable
{
    private readonly ConcurrentDictionary<string, QueueItem> _queue = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IQueuePersistenceService? _persistenceService;
    private readonly ILogger<QueueService>? _logger;
    private ProcessingSlotManager? _processingSlots;
    private ProcessingSlotManager? _transcodeSlots;
    private CruncharrConfig? _config;
    private CancellationToken _cancellationToken;

    // Ported from upstream: HashSet with lock for active download tracking
    private readonly object _downloadStartLock = new();
    private readonly HashSet<string> _activeOrStarting = new();

    // Per-item cancellation. A remove or pause of an ACTIVE item cancels its in-flight
    // download/mux/encode instead of letting it run to completion in the background
    // (previously the trash/pause buttons only changed queue state; ffmpeg kept going).
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _itemCts = new();
    // Items whose active work was cancelled by PauseItem (as opposed to removed):
    // the cancellation handler restores Paused instead of Cancelled so Resume works.
    private readonly ConcurrentDictionary<string, byte> _pauseRequested = new();

    private void CancelActiveItem(string queueItemId)
    {
        if (_itemCts.TryGetValue(queueItemId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    // Ported from upstream: pump scheduling
    private int _pumpScheduled;
    private int _pumpDirty;

    // Ported from upstream: auto-download blocking
    private DateTimeOffset? _autoDownloadBlockedUntilUtc;
    private readonly object _autoDownloadBlockLock = new();

    // [PT] Ported from upstream c123093: init-completion gate
    private volatile bool _isInitialized;

    // Shutdown flag replaces Environment.Exit for Docker compatibility
    private volatile bool _shutdownRequested;

    // Global pause flag
    private volatile bool _isGloballyPaused;

    public bool ShutdownRequested => _shutdownRequested;
    public bool IsGloballyPaused => _isGloballyPaused;

    public int ActiveDownloads
    {
        get
        {
            lock (_downloadStartLock)
            {
                return _activeOrStarting.Count;
            }
        }
    }

    public bool HasActiveDownloads => ActiveDownloads > 0;

    public event EventHandler? QueueStateChanged;

    private readonly INotificationService? _notificationService;

    public QueueService(IServiceProvider serviceProvider, ILogger<QueueService>? logger = null, IQueuePersistenceService? persistenceService = null, INotificationService? notificationService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _persistenceService = persistenceService;
        _notificationService = notificationService;

        // Restore persisted queue
        if (_persistenceService != null)
        {
            var savedQueue = _persistenceService.LoadQueue();
            if (savedQueue != null)
            {
                foreach (var item in savedQueue)
                {
                    // An item saved mid-flight has no running task after a restart. Left as
                    // Downloading/Processing the pump would skip it forever (stuck). Requeue it
                    // so it restarts. Paused/Done/Error/retry states are intentional — keep them.
                    if (item.DownloadProgress.State is DownloadState.Downloading or DownloadState.Processing)
                    {
                        item.DownloadProgress.State = DownloadState.Queued;
                        item.DownloadProgress.Percent = 0;
                        item.DownloadProgress.Doing = "Queued";
                    }
                    _queue[item.Id] = item;
                }
                _logger?.LogInformation("Restored {Count} items from persisted queue", savedQueue.Count);
                RestoreRetryStateFromQueue();
            }
        }
    }

    // Ported from upstream: Restore retry states when queue is loaded from persistence
    private void RestoreRetryStateFromQueue()
    {
        var retryItems = _queue.Values.Where(i => i.DownloadProgress.IsWaitingForRetry).ToList();
        if (retryItems.Count == 0) return;

        var retryTimes = retryItems.Select(i => i.DownloadProgress.RetryAtUtc).Where(t => t.HasValue).ToList();
        if (retryTimes.Count == 0) return;

        var latestRetry = retryTimes.Max();
        if (latestRetry.HasValue)
        {
            lock (_autoDownloadBlockLock)
            {
                _autoDownloadBlockedUntilUtc = latestRetry.Value;
            }
            _logger?.LogInformation("Restored retry state: {Count} items waiting, blocked until {Time}", retryItems.Count, latestRetry.Value);
        }
    }

    public void AddToQueue(EpisodeInfo episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var item = new QueueItem
        {
            Episode = episode,
            DownloadProgress = new DownloadProgress { State = DownloadState.Queued }
        };

        if (_queue.TryAdd(item.Id, item))
        {
            _logger?.LogInformation("Added to queue: {EpisodeId} - {Title}", episode.Id, episode.Title);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
        }
    }

    public bool RemoveFromQueue(string queueItemId)
    {
        if (_queue.TryRemove(queueItemId, out _))
        {
            // Cancel any in-flight download/mux/encode for this item.
            CancelActiveItem(queueItemId);
            _logger?.LogInformation("Removed from queue: {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            return true;
        }
        return false;
    }

    public List<QueueItem> GetQueue()
    {
        return _queue.Values.ToList();
    }

    public void RetryAllFailed()
    {
        foreach (var item in _queue.Values.Where(i => i.DownloadProgress.IsError || i.DownloadProgress.IsWaitingForRetry))
        {
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

    public void ClearQueue()
    {
        _queue.Clear();
        // Clearing the queue also cancels every in-flight download/mux/encode.
        foreach (var id in _itemCts.Keys.ToList())
        {
            CancelActiveItem(id);
        }
        _logger?.LogInformation("Queue cleared");
        OnQueueStateChanged();
        ScheduleSave();
    }

    public void ReplaceQueue(List<QueueItem> newQueue)
    {
        // Replacing the queue also replaces its active work. This is the REST equivalent of
        // removing the old desktop queue items, which cancels each item's token.
        foreach (var id in _itemCts.Keys.ToList())
        {
            CancelActiveItem(id);
        }

        if (newQueue == null)
        {
            _logger?.LogWarning("ReplaceQueue called with null list, clearing queue");
            _queue.Clear();
            OnQueueStateChanged();
            ScheduleSave();
            return;
        }
        _queue.Clear();
        foreach (var item in newQueue)
        {
            if (item != null)
            {
                _queue[item.Id] = item;
            }
        }
        _logger?.LogInformation("Queue replaced with {Count} items", newQueue.Count);
        OnQueueStateChanged();
        ScheduleSave();
        RequestPump();
    }

    public bool RetryItem(string queueItemId)
    {
        if (_queue.TryGetValue(queueItemId, out var item))
        {
            item.DownloadProgress.RetryAttemptCount = 0;
            item.DownloadProgress.State = DownloadState.Queued;
            item.DownloadProgress.RetryAtUtc = null;
            item.DownloadProgress.Doing = "Retrying...";
            _logger?.LogInformation("Retrying item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
            return true;
        }
        return false;
    }

    public bool PauseItem(string queueItemId)
    {
        if (_queue.TryGetValue(queueItemId, out var item))
        {
            // Actively downloading/processing: cancel the in-flight work so pause actually
            // frees the machine (Resume restarts the episode from the beginning). The
            // cancellation handler in RunDownloadAsync sets the final Paused state.
            if (_itemCts.ContainsKey(queueItemId))
            {
                _pauseRequested[queueItemId] = 1;
                item.DownloadProgress.Doing = "Pausing...";
                CancelActiveItem(queueItemId);
            }
            else
            {
                item.DownloadProgress.State = DownloadState.Paused;
                item.DownloadProgress.Doing = "Paused";
            }
            _logger?.LogInformation("Paused item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            return true;
        }
        return false;
    }

    public bool ResumeItem(string queueItemId)
    {
        if (_queue.TryGetValue(queueItemId, out var item))
        {
            // Clear any pending pause request so a still-winding-down cancel from a rapid
            // pause->resume doesn't re-park this item as Paused after we requeue it.
            _pauseRequested.TryRemove(queueItemId, out _);
            item.DownloadProgress.State = DownloadState.Queued;
            item.DownloadProgress.Doing = "Queued";
            _logger?.LogInformation("Resumed item {QueueItemId}", queueItemId);
            OnQueueStateChanged();
            ScheduleSave();
            RequestPump();
            return true;
        }
        return false;
    }

    // Ported from upstream QueueManager.TryStartDownload
    public bool StartItem(string queueItemId)
    {
        if (_queue.TryGetValue(queueItemId, out var item))
        {
            if (!TryStartDownload(item))
            {
                _logger?.LogWarning("Cannot start {QueueItemId} - already active or no slots", queueItemId);
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunDownloadAsync(item, _cancellationToken, applyAutoStartDelay: false);
                }
                finally
                {
                    ReleaseDownloadSlot(item);
                }
            }, _cancellationToken);
            return true;
        }
        return false;
    }

    // Ported from upstream QueueManager.TryStartDownload
    private bool TryStartDownload(QueueItem item)
    {
        lock (_downloadStartLock)
        {
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
    private void ReleaseDownloadSlot(QueueItem item)
    {
        bool removed;

        lock (_downloadStartLock)
        {
            removed = _activeOrStarting.Remove(item.Id);
        }

        if (removed)
        {
            NotifyDownloadStateChanged();
            OnQueueStateChanged();

            if (_config?.Queue.AutoDownload == true)
            {
                RequestPump();
            }

            // Check if queue is empty and shutdown is enabled
            CheckShutdownWhenQueueEmpty();
        }
    }

    private void CheckShutdownWhenQueueEmpty()
    {
        if (_config?.Queue.ShutdownWhenQueueEmpty != true)
            return;

        bool shouldShutdown = false;

        lock (_downloadStartLock)
        {
            bool hasUnfinishedItems = _queue.Values.Any(q => !q.DownloadProgress.IsDone && !q.DownloadProgress.IsError);
            if (!hasUnfinishedItems && _activeOrStarting.Count == 0)
            {
                _logger?.LogInformation("Queue is empty and ShutdownWhenQueueEmpty is enabled - shutting down");
                _config.Queue.ShutdownWhenQueueEmpty = false;
                shouldShutdown = true;
            }
        }

        if (shouldShutdown)
        {
            // Execute on complete outside the lock to avoid deadlock
            if (_notificationService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notificationService.NotifyQueueCompleteAsync(_config);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to notify queue complete");
                    }
                });
            }
            _logger?.LogInformation("Queue empty, requesting shutdown...");
            _shutdownRequested = true;
        }
    }

    public async Task ProcessQueueAsync(CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _config = config;
        _cancellationToken = cancellationToken;
        _processingSlots?.Dispose();
        _processingSlots = new ProcessingSlotManager(config.Queue.SimultaneousProcessingJobs);
        _transcodeSlots?.Dispose();
        _transcodeSlots = new ProcessingSlotManager(Math.Max(1, config.Queue.MaxSimultaneousTranscodes));

        _logger?.LogInformation("Queue processor started with {Downloads} concurrent downloads, {Processing} processing jobs, {Transcodes} transcodes",
            config.Download.SimultaneousDownloads, config.Queue.SimultaneousProcessingJobs, config.Queue.MaxSimultaneousTranscodes);

        ScheduleRestoredRetryWakes();
        // ProcessQueueAsync may begin before or after SetInitialized. Requesting here as well as
        // in SetInitialized makes either startup order deterministic; PumpQueueAsync keeps the
        // initialization gate used by the desktop source.
        RequestPump();

        // Keep running until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Queue processor stopped");
        }
    }

    // Ported from upstream QueueManager.RunPump + PumpQueue
    private readonly object _pumpLock = new();

    private void RequestPump()
    {
        if (_config?.Queue.AutoDownload != true) return;

        Interlocked.Exchange(ref _pumpDirty, 1);

        lock (_pumpLock)
        {
            if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) != 0)
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pumpDirty, 0) == 1)
                {
                    await PumpQueueAsync();
                    await Task.Delay(100, _cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown, don't log as error
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in queue pump");
            }
            finally
            {
                lock (_pumpLock)
                {
                    Interlocked.Exchange(ref _pumpScheduled, 0);

                    if (Volatile.Read(ref _pumpDirty) == 1)
                    {
                        // Dirty was set during pump - reschedule synchronously
                        Interlocked.Exchange(ref _pumpScheduled, 1);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(100, _cancellationToken);
                                await PumpQueueAsync();
                                Interlocked.Exchange(ref _pumpScheduled, 0);
                                RequestPump(); // Re-check for more work
                            }
                            catch (OperationCanceledException)
                            {
                                // Normal shutdown, don't log as error
                                Interlocked.Exchange(ref _pumpScheduled, 0);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Unhandled exception in queue pump reschedule");
                                Interlocked.Exchange(ref _pumpScheduled, 0);
                            }
                        });
                    }
                }
            }
        });
    }

    // Which download states the auto-download pump may (re)start. Excludes terminal/in-flight
    // states AND — critically — Paused/Cancelled, which only an explicit Resume (-> Queued)
    // may requeue. Without the Paused exclusion the pump restarted a download the instant it
    // was paused, so Pause appeared to do nothing. (internal for the guard test.)
    internal static bool IsAutoStartEligibleState(DownloadProgress p)
    {
        if (p.IsError) return false;
        if (p.IsWaitingForRetry) return false;
        if (p.IsDone) return false;
        if (p.State is DownloadState.Paused or DownloadState.Cancelled) return false;
        if (p.State is DownloadState.Downloading or DownloadState.Processing) return false;
        return true;
    }

    // Ported from upstream QueueManager.PumpQueue
#pragma warning disable CS1998
    private async Task PumpQueueAsync()
    {
#pragma warning restore CS1998
        if (!_isInitialized) return;
        if (_config == null) return;
        if (!_config.Queue.AutoDownload) return;
        if (_isGloballyPaused) return;

        lock (_autoDownloadBlockLock)
        {
            if (_autoDownloadBlockedUntilUtc.HasValue && !HasPendingRetryItems())
            {
                _autoDownloadBlockedUntilUtc = null;
            }

            if (_autoDownloadBlockedUntilUtc.HasValue && _autoDownloadBlockedUntilUtc.Value > DateTimeOffset.UtcNow)
            {
                return;
            }

            if (_autoDownloadBlockedUntilUtc.HasValue)
            {
                _autoDownloadBlockedUntilUtc = null;
            }
        }

        var toStart = new List<QueueItem>();
        bool changed = false;

        lock (_downloadStartLock)
        {
            int limit = _config.Download.SimultaneousDownloads;
            int freeSlots = Math.Max(0, limit - _activeOrStarting.Count);

            if (freeSlots == 0)
                return;

            foreach (var item in _queue.Values.OrderBy(q => q.AddedAt))
            {
                if (freeSlots == 0)
                    break;

                if (!IsAutoStartEligibleState(item.DownloadProgress))
                    continue;

                if (_activeOrStarting.Contains(item.Id))
                    continue;

                _activeOrStarting.Add(item.Id);
                freeSlots--;
                toStart.Add(item);
                changed = true;
            }
        }

        if (changed)
        {
            NotifyDownloadStateChanged();
        }

        foreach (var item in toStart)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunDownloadAsync(item, _cancellationToken, applyAutoStartDelay: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Download task failed for {EpisodeId}", item.Episode?.Id ?? item.Id);
                    item.DownloadProgress.State = DownloadState.Error;
                    item.DownloadProgress.Doing = $"Error: {ex.Message}";
                }
                finally
                {
                    ReleaseDownloadSlot(item);
                }
            }, _cancellationToken);
        }

        OnQueueStateChanged();
    }

    // Extracted download execution logic
    private async Task RunDownloadAsync(QueueItem item, CancellationToken outerCancellationToken, bool applyAutoStartDelay)
    {
        // Per-item cancellation (linked to app shutdown) so removing/pausing this item
        // cancels ITS delay/download/mux/encode without touching the others. The desktop source
        // renews its item CTS before entering any download delay for the same reason.
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
        _itemCts[item.Id] = itemCts;
        var cancellationToken = itemCts.Token;

        try
        {
            // Reject a stale item captured before Pause/Remove/Replace won its race with the task.
            if (!IsCurrentQueueItem(item) || item.DownloadProgress.State is DownloadState.Paused or DownloadState.Cancelled)
            {
                return;
            }

            if (applyAutoStartDelay)
            {
                // Apply cooldown delay between downloads (upstream #445).
                if (_config?.Download.CooldownDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Download.CooldownDelaySeconds), cancellationToken);
                }
                // [PT] Upstream: episode-based download delay (when dub-based delay is disabled).
                if (_config?.Download.DownloadDelaySeconds > 0 && !_config.Download.DownloadDelayUseDubBased)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Download.DownloadDelaySeconds), cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentQueueItem(item) || item.DownloadProgress.State is DownloadState.Paused or DownloadState.Cancelled)
            {
                return;
            }

            item.DownloadProgress.State = DownloadState.Downloading;
            item.DownloadProgress.Doing = "Starting download...";
            OnQueueStateChanged();
            ScheduleSave();

            _logger?.LogInformation("Starting download: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);

            var queueProgress = new Progress<DownloadProgress>(p =>
            {
                item.DownloadProgress.State = p.State;
                item.DownloadProgress.Percent = p.Percent;
                item.DownloadProgress.Doing = p.Doing;
                item.DownloadProgress.DownloadSpeedBytes = p.DownloadSpeedBytes;
                item.DownloadProgress.Time = p.Time;
                OnQueueStateChanged();
            });

            var downloadService = _serviceProvider.GetRequiredService<IDownloadService>();
            var result = await downloadService.DownloadEpisodeAsync(item.Episode, _config!, queueProgress, cancellationToken, onDownloadComplete: () =>
            {
                // Release download slot early so next download can start while this one is still processing (muxing/encoding)
                if (_config?.Download.DownloadAllowEarlyStart == true)
                {
                    ReleaseDownloadSlot(item);
                }
            });

            if (!result.Success)
            {
                throw new Exception(result.ErrorMessage ?? "Download failed");
            }

            item.DownloadProgress.State = DownloadState.Done;
            item.DownloadProgress.Percent = 100;
            item.DownloadProgress.Doing = "Complete";
            _logger?.LogInformation("Download complete: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);

            // Send webhook notification
            if (_notificationService != null && _config != null)
            {
                _ = Task.Run(async () => await _notificationService.NotifyCompleteAsync(result, _config));
            }

            if (_config?.RemoveFinishedDownload == true)
            {
                _queue.TryRemove(item.Id, out _);
                _logger?.LogInformation("Removed finished download from queue: {EpisodeId}", item.Episode.Id);
            }
        }
        catch (DownloadException dex)
        {
            _logger?.LogError("Download failed: {ErrorType} - {Message}", dex.ErrorType, dex.Message);

            // Send webhook error notification
            if (_notificationService != null && _config != null)
            {
                var errorResult = new DownloadResult { Success = false, ErrorMessage = dex.Message };
                _ = Task.Run(async () => await _notificationService.NotifyErrorAsync(errorResult, _config));
            }

            bool isAuthError = dex.ErrorType == DownloadErrorType.NotAuthenticated ||
                              dex.ErrorType == DownloadErrorType.SubscriptionExpired ||
                              dex.ErrorType == DownloadErrorType.PremiumContent ||
                              dex.ErrorType == DownloadErrorType.MaturityRating;

            if (!isAuthError && item.DownloadProgress.RetryAttemptCount < (_config?.Download.RetryAttempts ?? 5))
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, (_config?.Download.RetryDelaySeconds ?? 5) * Math.Pow(3, item.DownloadProgress.RetryAttemptCount)));
                ScheduleRetry(item.Id, delay, $"Error: {dex.Message}");
            }
            else
            {
                item.DownloadProgress.State = DownloadState.Error;
                item.DownloadProgress.Doing = $"Error: {dex.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            bool wasPause = _pauseRequested.TryRemove(item.Id, out _);
            // If Resume/Retry re-queued this item while cancellation was still in flight, honor
            // that intent instead of clobbering it back to Paused/Cancelled.
            if (wasPause)
            {
                // Cancelled BY PauseItem: park it as Paused so Resume can restart it.
                item.DownloadProgress.State = DownloadState.Paused;
                item.DownloadProgress.Doing = "Paused";
                item.DownloadProgress.Percent = 0;
                _logger?.LogInformation("Download paused (in-flight work cancelled): {EpisodeId}", item.Episode.Id);
            }
            else if (item.DownloadProgress.State == DownloadState.Queued)
            {
                _logger?.LogInformation("Download cancelled but item re-queued; leaving it queued: {EpisodeId}", item.Episode.Id);
            }
            else
            {
                item.DownloadProgress.State = DownloadState.Cancelled;
                item.DownloadProgress.Doing = "Cancelled";
                _logger?.LogInformation("Download cancelled: {EpisodeId}", item.Episode.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Download failed: {EpisodeId} - {Title}", item.Episode.Id, item.Episode.Title);

            if (item.DownloadProgress.RetryAttemptCount < (_config?.Download.RetryAttempts ?? 5))
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, (_config?.Download.RetryDelaySeconds ?? 5) * Math.Pow(3, item.DownloadProgress.RetryAttemptCount)));
                ScheduleRetry(item.Id, delay, $"Error: {ex.Message}");
            }
            else
            {
                item.DownloadProgress.State = DownloadState.Error;
                item.DownloadProgress.Doing = $"Error: {ex.Message}";
            }
        }
        finally
        {
            _itemCts.TryRemove(item.Id, out _);
            _pauseRequested.TryRemove(item.Id, out _);
        }

        OnQueueStateChanged();
        ScheduleSave();
    }

    private bool IsCurrentQueueItem(QueueItem item)
    {
        return _queue.TryGetValue(item.Id, out var current) && ReferenceEquals(current, item);
    }

    // Ported from upstream QueueManager.RestoreRetryStateFromQueue/ScheduleRetryWake. Queue
    // persistence is loaded in the constructor, before the host cancellation token exists, so
    // delayed wakes are attached after ProcessQueueAsync installs that token.
    private void ScheduleRestoredRetryWakes()
    {
        foreach (var item in _queue.Values.Where(i => i.DownloadProgress.IsWaitingForRetry).ToList())
        {
            var retryAtUtc = item.DownloadProgress.RetryAtUtc;
            if (!retryAtUtc.HasValue) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    var remaining = retryAtUtc.Value - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, _cancellationToken);
                    }

                    if (!IsCurrentQueueItem(item)) return;
                    item.DownloadProgress.RetryAtUtc = null;
                    OnQueueStateChanged();
                    RequestPump();
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            }, _cancellationToken);
        }
    }

    public void ScheduleRetry(string queueItemId, TimeSpan delay, string statusText)
    {
        if (_queue.TryGetValue(queueItemId, out var item))
        {
            item.DownloadProgress.ScheduleRetry(delay, statusText);
            _logger?.LogInformation("Scheduled retry for {QueueItemId} in {Delay}s: {Status}", queueItemId, delay.TotalSeconds, statusText);
            OnQueueStateChanged();
            ScheduleSave();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _cancellationToken);
                    item.DownloadProgress.RetryAtUtc = null;
                    OnQueueStateChanged();
                    RequestPump();
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            });
        }
    }

    public void BlockAutoDownloadUntil(TimeSpan delay)
    {
        var unblockAt = DateTimeOffset.UtcNow.Add(delay);

        lock (_autoDownloadBlockLock)
        {
            if (!_autoDownloadBlockedUntilUtc.HasValue || unblockAt > _autoDownloadBlockedUntilUtc.Value)
            {
                _autoDownloadBlockedUntilUtc = unblockAt;
            }
            else
            {
                unblockAt = _autoDownloadBlockedUntilUtc.Value;
            }
        }

        _logger?.LogInformation("Auto-download blocked until {UnblockAt}", unblockAt);

        _ = Task.Run(async () =>
        {
            try
            {
                var remaining = unblockAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _cancellationToken);
                }

                lock (_autoDownloadBlockLock)
                {
                    if (_autoDownloadBlockedUntilUtc.HasValue && _autoDownloadBlockedUntilUtc.Value <= DateTimeOffset.UtcNow)
                    {
                        _autoDownloadBlockedUntilUtc = null;
                    }
                }

                OnQueueStateChanged();
                RequestPump();
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        });
    }

    private bool HasPendingRetryItems()
    {
        return _queue.Values.Any(item => item.DownloadProgress.IsWaitingForRetry);
    }

    private void NotifyDownloadStateChanged()
    {
        OnQueueStateChanged();
    }

    private void ScheduleSave()
    {
        if (_persistenceService != null)
        {
            var queue = GetQueue();
            _persistenceService.ScheduleSave(queue);
        }
    }

    private void OnQueueStateChanged()
    {
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task WaitForProcessingSlotAsync(CancellationToken cancellationToken = default)
    {
        if (_processingSlots != null)
        {
            await _processingSlots.WaitAsync(cancellationToken);
        }
    }

    public void ReleaseProcessingSlot()
    {
        _processingSlots?.Release();
    }

    public void SetProcessingLimit(int newLimit)
    {
        _processingSlots?.SetLimit(newLimit);
    }

    public async Task WaitForTranscodeSlotAsync(CancellationToken cancellationToken = default)
    {
        if (_transcodeSlots != null)
        {
            await _transcodeSlots.WaitAsync(cancellationToken);
        }
    }

    public void ReleaseTranscodeSlot()
    {
        _transcodeSlots?.Release();
    }

    public void SetTranscodeLimit(int newLimit)
    {
        _transcodeSlots?.SetLimit(Math.Max(1, newLimit));
    }

    // [PT] Ported from upstream c123093: init-completion gate
    public void SetInitialized(bool initialized)
    {
        _isInitialized = initialized;
        _logger?.LogInformation("QueueService initialized: {Initialized}", initialized);
        if (initialized)
        {
            RequestPump();
        }
    }

    public void PauseGlobally()
    {
        _isGloballyPaused = true;
        _logger?.LogInformation("Queue globally paused");
    }

    public void ResumeGlobally()
    {
        _isGloballyPaused = false;
        _logger?.LogInformation("Queue globally resumed");
        RequestPump();
    }

    public void Dispose()
    {
        _processingSlots?.Dispose();
        _transcodeSlots?.Dispose();
    }
}
