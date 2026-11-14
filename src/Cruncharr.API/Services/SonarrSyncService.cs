using Cruncharr.Core.Models;
using Cruncharr.Core.Services;

namespace Cruncharr.API.Services;

public sealed class SonarrSyncService(ISonarrAcquisitionService requests, IQueueService queue, ILogger<SonarrSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await requests.HoldQueuedEpisodesAsync(QueuedEpisodeIds(queue.GetQueue()), stoppingToken);
                await requests.SynchronizeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Sonarr synchronization will retry"); }
        }
    }

    // Episodes Cruncharr still intends to download, including failures waiting for an automatic retry.
    public static HashSet<string> QueuedEpisodeIds(IEnumerable<QueueItem> items) => items
        .Where(item => item.DownloadProgress.State is not (DownloadState.Done or DownloadState.Error or DownloadState.Cancelled) ||
                       item.DownloadProgress.IsWaitingForRetry)
        .Select(item => item.Episode.Id)
        .ToHashSet();
}
