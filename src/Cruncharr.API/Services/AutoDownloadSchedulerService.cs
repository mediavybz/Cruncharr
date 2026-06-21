using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cruncharr.API.Services;

public class AutoDownloadSchedulerService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoDownloadSchedulerService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _executeTask;
    private DateTimeOffset? _lastRun;
    private bool _isRunning;
    private DateTimeOffset? _lastNotifyCheck;

    public AutoDownloadSchedulerService(IServiceProvider serviceProvider, ILogger<AutoDownloadSchedulerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public bool IsRunning => _isRunning;
    public DateTimeOffset? LastRun => _lastRun;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Auto-download scheduler starting...");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = ExecuteAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Auto-download scheduler stopping...");
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
        if (_executeTask != null)
        {
            try
            {
                await _executeTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Auto-download scheduler did not stop within 10 seconds");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var config = scope.ServiceProvider.GetRequiredService<CruncharrConfig>();

                var intervalMinutes = config.History?.AutoRefreshIntervalMinutes ?? 0;
                if (intervalMinutes <= 0)
                {
                    // No interval set, wait and check again later
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    continue;
                }

                _logger.LogInformation("Auto-download check starting (mode: {Mode}, interval: {Interval}m)",
                    config.History?.AutoRefreshMode ?? 50, intervalMinutes);

                _isRunning = true;
                await RunCheckAsync(scope.ServiceProvider, config, cancellationToken);
                _lastRun = DateTimeOffset.UtcNow;
                _isRunning = false;

                _logger.LogInformation("Auto-download check completed. Next check in {Interval} minutes", intervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-download check failed");
                _isRunning = false;
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async Task RunCheckAsync(IServiceProvider serviceProvider, CruncharrConfig config, CancellationToken cancellationToken)
    {
        if (config.History == null || !config.History.Enabled) return;

        var historyService = serviceProvider.GetRequiredService<IHistoryService>();
        var queueService = serviceProvider.GetRequiredService<IQueueService>();
        var apiService = serviceProvider.GetRequiredService<ICrunchyrollApiService>();
        var authService = serviceProvider.GetRequiredService<ICrunchyrollAuthService>();

        var mode = config.History.AutoRefreshMode;

        switch (mode)
        {
            case 0: // DefaultAll
                _logger.LogInformation("Refreshing all history series...");
                await RefreshHistoryAsync(historyService, cancellationToken);
                break;
            case 1: // DefaultActive
                _logger.LogInformation("Refreshing active history series...");
                await RefreshHistoryAsync(historyService, cancellationToken);
                break;
            case 50: // FastNewReleases
            default:
                _logger.LogInformation("Checking for new releases...");
                await RefreshHistoryWithNewReleasesAsync(historyService, apiService, authService,
                    serviceProvider.GetService<INotificationService>(), config, cancellationToken);
                break;
        }

        if (config.History.AutoRefreshAddToQueue)
        {
            _logger.LogInformation("Adding missing episodes to queue...");
            await AddNewMissingToQueueAsync(historyService, queueService, cancellationToken);
        }
    }

    private async Task RefreshHistoryAsync(IHistoryService historyService, CancellationToken cancellationToken)
    {
        try
        {
            var seriesList = await historyService.GetHistorySeriesAsync();
            if (seriesList == null || seriesList.Count == 0) return;

            foreach (var series in seriesList)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogDebug("Refreshing series: {SeriesId} ({Title})", series.SeriesId, series.SeriesTitle);
                    await historyService.CrUpdateSeriesAsync(series.SeriesId, "");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh series {SeriesId}", series.SeriesId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History refresh failed");
        }
    }

    private async Task RefreshHistoryWithNewReleasesAsync(IHistoryService historyService, ICrunchyrollApiService apiService, ICrunchyrollAuthService authService, INotificationService? notification, CruncharrConfig config, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(authService.Token?.access_token))
            {
                _logger.LogWarning("Cannot check for new releases: not authenticated");
                return;
            }

            var lang = authService.Profile?.PreferredContentAudioLanguage ?? "ja-JP";
            var newEpisodesBase = await apiService.GetNewEpisodesAsync(lang, 1000, true, cancellationToken);
            if (newEpisodesBase?.Data == null || newEpisodesBase.Data.Count == 0) return;

            _logger.LogInformation("Found {Count} new episodes", newEpisodesBase.Data.Count);
            await historyService.UpdateWithEpisodeAsync(newEpisodesBase.Data);

            await NotifyTrackedSeriesReleasesAsync(historyService, notification, config, newEpisodesBase.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "New releases check failed");
        }
    }

    // Notify newly released episodes of series the user tracks (anything in history). Gated on the
    // toggle; in-memory dedup by release date - the first run seeds the baseline and notifies nothing,
    // so historical episodes never spam. Baseline seeds from the persisted config field if present.
    private async Task NotifyTrackedSeriesReleasesAsync(IHistoryService historyService, INotificationService? notification, CruncharrConfig config, List<Cruncharr.Core.Services.CrBrowseEpisode> newEpisodes)
    {
        if (notification == null || config.Notifications?.NotifyTrackedSeriesReleased != true) return;
        try
        {
            var baseline = (_lastNotifyCheck ?? (config.TrackedSeriesReleaseLastCheckUtc.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(config.TrackedSeriesReleaseLastCheckUtc.Value, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow)).UtcDateTime;
            bool firstRun = _lastNotifyCheck == null;
            _lastNotifyCheck = DateTimeOffset.UtcNow;
            if (firstRun) return; // seed baseline only; don't notify pre-existing releases

            var seriesList = await historyService.GetHistorySeriesAsync();
            var trackedById = (seriesList ?? new List<HistorySeries>())
                .Where(s => !string.IsNullOrEmpty(s.SeriesId))
                .GroupBy(s => s.SeriesId!)
                .ToDictionary(g => g.Key, g => g.First().SeriesTitle ?? string.Empty);

            foreach (var ep in newEpisodes)
            {
                var sid = ep.EpisodeMetadata?.SeriesId;
                if (string.IsNullOrEmpty(sid) || !trackedById.TryGetValue(sid, out var seriesTitle)) continue;
                if (ep.EpisodeMetadata!.PremiumAvailableDate <= baseline) continue;
                await notification.NotifyTrackedSeriesReleasedAsync(seriesTitle, ep.Title ?? string.Empty, ep.Id ?? string.Empty, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tracked-series-released notification step failed");
        }
    }

    private async Task AddNewMissingToQueueAsync(IHistoryService historyService, IQueueService queueService, CancellationToken cancellationToken)
    {
        try
        {
            var seriesList = await historyService.GetHistorySeriesAsync();
            if (seriesList == null || seriesList.Count == 0) return;

            int addedCount = 0;
            var queueItems = queueService.GetQueue();

            foreach (var series in seriesList)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (series.Seasons == null || series.Seasons.Count == 0) continue;

                    foreach (var season in series.Seasons)
                    {
                        if (season.EpisodesList == null || season.EpisodesList.Count == 0) continue;

                        foreach (var episode in season.EpisodesList)
                        {
                            // Skip already downloaded episodes
                            if (episode.WasDownloaded) continue;

                            // Skip episodes already in queue
                            bool inQueue = queueItems.Any(q =>
                                q.Episode?.Id == episode.EpisodeId ||
                                (q.Episode?.SeriesId == series.SeriesId &&
                                 q.Episode?.Episode == episode.Episode));

                            if (inQueue) continue;

                            // Add to queue
                            var episodeInfo = new EpisodeInfo
                            {
                                Id = episode.EpisodeId ?? $"{series.SeriesId}-{season.SeasonNum}-{episode.Episode}",
                                Title = episode.EpisodeTitle ?? $"Episode {episode.Episode}",
                                SeriesTitle = series.SeriesTitle ?? "Unknown",
                                SeriesId = series.SeriesId,
                                SeasonNumber = int.TryParse(season.SeasonNum, out var seasonNum) ? seasonNum : 0,
                                EpisodeNumber = int.TryParse(episode.Episode, out var epNum) ? epNum : 0,
                                Episode = episode.Episode,
                                SeasonTitle = season.SeasonTitle,
                                SeasonId = season.SeasonId,
                                AudioLocale = "ja-JP",
                                Locale = "ja-JP"
                            };

                            queueService.AddToQueue(episodeInfo);
                            addedCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add missing episodes for series {SeriesId}", series.SeriesId);
                }
            }

            if (addedCount > 0)
            {
                _logger.LogInformation("Added {Count} missing episodes to queue", addedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add missing to queue failed");
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
