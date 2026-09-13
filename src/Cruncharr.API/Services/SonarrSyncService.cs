using Cruncharr.Core.Services;

namespace Cruncharr.API.Services;

public sealed class SonarrSyncService(ISonarrAcquisitionService requests, ILogger<SonarrSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await requests.SynchronizeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Sonarr synchronization will retry"); }
        }
    }
}
