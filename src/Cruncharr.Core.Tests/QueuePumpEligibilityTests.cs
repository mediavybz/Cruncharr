using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cruncharr.Core.Tests;

// GUARD — the auto-download pump must NOT restart a Paused (or Cancelled) item. With
// AutoDownload on, an earlier bug had the pump immediately re-start a download the moment it
// was paused, so Pause did nothing. Only an explicit Resume (-> Queued) may requeue.
public class QueuePumpEligibilityTests
{
    private static DownloadProgress P(DownloadState state) => new() { State = state };

    [Fact]
    public void QueueBroadcast_SlowClientReceivesLatestSnapshot()
    {
        var queue = new Mock<IQueueService>();
        queue.Setup(service => service.GetQueue()).Returns([]);
        queue.Setup(service => service.HasActiveDownloads).Returns(true);
        queue.SetupSequence(service => service.ActiveDownloads)
            .Returns(1)
            .Returns(2);
        using var broadcast = new QueueBroadcastService(
            queue.Object,
            NullLogger<QueueBroadcastService>.Instance);
        var reader = broadcast.Subscribe(Guid.NewGuid());

        queue.Raise(service => service.QueueStateChanged += null, EventArgs.Empty);
        queue.Raise(service => service.QueueStateChanged += null, EventArgs.Empty);

        Assert.True(reader.TryRead(out var latest));
        Assert.Contains("\"activeDownloads\":2", latest);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Paused_IsNotAutoStartEligible()
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(DownloadState.Paused)));
    }

    [Fact]
    public void Cancelled_IsNotAutoStartEligible()
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(DownloadState.Cancelled)));
    }

    [Theory]
    [InlineData(DownloadState.Downloading)]
    [InlineData(DownloadState.Processing)]
    [InlineData(DownloadState.Done)]
    [InlineData(DownloadState.Error)]
    public void TerminalOrInFlight_IsNotAutoStartEligible(DownloadState state)
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(state)));
    }

    [Fact]
    public void Queued_IsAutoStartEligible()
    {
        Assert.True(QueueService.IsAutoStartEligibleState(P(DownloadState.Queued)));
    }

    [Fact]
    public void MissingLanguage_IsNotRetried()
    {
        Assert.False(QueueService.IsRetryableDownloadError(DownloadErrorType.MissingLanguage));
        Assert.True(QueueService.IsRetryableDownloadError(DownloadErrorType.RateLimited));
        Assert.True(QueueService.IsRetryableDownloadError(DownloadErrorType.NetworkError));
    }

    [Fact]
    public async Task AutoStartDelay_CanBePausedBeforeDownloadBegins()
    {
        var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadService = new Mock<IDownloadService>();
        downloadService
            .Setup(service => service.DownloadEpisodeAsync(
                It.IsAny<EpisodeInfo>(),
                It.IsAny<CruncharrConfig>(),
                It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action?>()))
            .Callback(() => downloadStarted.TrySetResult(true))
            .ReturnsAsync(new DownloadResult { Success = true });

        using var provider = new ServiceCollection()
            .AddSingleton(downloadService.Object)
            .BuildServiceProvider();
        using var queue = new QueueService(provider);
        using var stop = new CancellationTokenSource();
        var config = new CruncharrConfig();
        config.Queue.AutoDownload = true;
        config.Download.CooldownDelaySeconds = 1;

        var processor = queue.ProcessQueueAsync(config, cancellationToken: stop.Token);
        queue.SetInitialized(true);
        queue.AddToQueue(new EpisodeInfo { Id = "pause-during-delay", Title = "Episode" });

        await WaitForAsync(() => queue.ActiveDownloads == 1);
        var item = Assert.Single(queue.GetQueue());
        Assert.True(queue.PauseItem(item.Id));

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.False(downloadStarted.Task.IsCompleted);
        Assert.Equal(DownloadState.Paused, item.DownloadProgress.State);

        stop.Cancel();
        await processor;
    }

    [Fact]
    public async Task Scheduler_UsesSeasonLanguagesAndPopulatesEpisodeMetadata()
    {
        var episode = new HistoryEpisode
        {
            EpisodeId = "EP1",
            Episode = "1",
            EpisodeTitle = "First Episode",
            EpisodeDescription = "Description",
            ThumbnailImageUrl = "https://img/episode.jpg",
            IsEpisodeAvailableOnStreamingService = true,
            HistoryEpisodeAvailableDubLang = ["ja-JP", "en-US"],
            HistoryEpisodeAvailableSoftSubs = ["en-US", "es-419"]
        };
        var season = new HistorySeason
        {
            SeasonId = "SEASON1",
            SeasonNum = "1",
            SeasonTitle = "Season 1",
            HistorySeasonDubLangOverride = ["en-US"],
            HistorySeasonSoftSubsOverride = ["all"],
            EpisodesList = [episode]
        };
        var series = new HistorySeries
        {
            SeriesId = "SERIES1",
            SeriesTitle = "Example Show",
            SeriesDescription = "Series description",
            ThumbnailImageUrl = "https://img/series.jpg",
            HistorySeriesDubLangOverride = ["ja-JP"],
            Seasons = [season]
        };

        var history = new Mock<IHistoryService>();
        history.Setup(service => service.GetHistorySeriesAsync()).ReturnsAsync([series]);
        history.Setup(service => service.CrUpdateSeriesAsync(It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);

        EpisodeInfo? queuedEpisode = null;
        var queue = new Mock<IQueueService>();
        queue.Setup(service => service.GetQueue()).Returns([]);
        queue.Setup(service => service.AddToQueue(It.IsAny<EpisodeInfo>()))
            .Callback<EpisodeInfo>(item => queuedEpisode = item);

        using var provider = new ServiceCollection()
            .AddSingleton(history.Object)
            .AddSingleton(queue.Object)
            .AddSingleton(Mock.Of<ICrunchyrollApiService>())
            .AddSingleton(Mock.Of<ICrunchyrollAuthService>())
            .BuildServiceProvider();
        var scheduler = new AutoDownloadSchedulerService(
            provider,
            NullLogger<AutoDownloadSchedulerService>.Instance);
        var config = new CruncharrConfig();
        config.History.Enabled = true;
        config.History.AutoRefreshMode = 0;
        config.History.AutoRefreshAddToQueue = true;
        config.History.CountMissing = true;
        config.History.Lang = "fr-FR";

        await scheduler.RunCheckAsync(provider, config, CancellationToken.None);

        Assert.NotNull(queuedEpisode);
        Assert.Equal(["en-US"], queuedEpisode!.SelectedDubs);
        Assert.Equal(["en-US", "es-419"], queuedEpisode.SelectedSubs);
        Assert.Equal("en-US", queuedEpisode.AudioLocale);
        Assert.Equal("fr-FR", queuedEpisode.Locale);
        Assert.Equal("Description", queuedEpisode.Description);
        Assert.Equal("https://img/episode.jpg", queuedEpisode.ThumbnailUrl);
        Assert.Equal("https://img/series.jpg", queuedEpisode.CoverArtUrl);
        Assert.Equal("SEASON1", queuedEpisode.SeasonId);
        Assert.Equal("SERIES1", queuedEpisode.SeriesId);
    }

    [Fact]
    public async Task Scheduler_SkipsUnmonitoredSonarrEpisodeWhenConfigured()
    {
        var series = new HistorySeries
        {
            SeriesId = "SERIES1",
            SeriesTitle = "Example Show",
            SonarrSeriesId = "10",
            Seasons =
            [
                new HistorySeason
                {
                    SeasonId = "SEASON1",
                    SeasonNum = "1",
                    EpisodesList =
                    [
                        new HistoryEpisode
                        {
                            EpisodeId = "EP1",
                            Episode = "1",
                            SonarrEpisodeId = "100",
                            SonarrHasFile = false,
                            SonarrIsMonitored = false,
                            HistoryEpisodeAvailableDubLang = ["en-US"]
                        }
                    ]
                }
            ]
        };

        var history = new Mock<IHistoryService>();
        history.Setup(service => service.GetHistorySeriesAsync()).ReturnsAsync([series]);
        history.Setup(service => service.CrUpdateSeriesAsync(It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);

        var queue = new Mock<IQueueService>();
        queue.Setup(service => service.GetQueue()).Returns([]);

        using var provider = new ServiceCollection()
            .AddSingleton(history.Object)
            .AddSingleton(queue.Object)
            .AddSingleton(Mock.Of<ICrunchyrollApiService>())
            .AddSingleton(Mock.Of<ICrunchyrollAuthService>())
            .BuildServiceProvider();
        var scheduler = new AutoDownloadSchedulerService(
            provider,
            NullLogger<AutoDownloadSchedulerService>.Instance);
        var config = new CruncharrConfig();
        config.Sonarr.Enabled = true;
        config.History.Enabled = true;
        config.History.SkipUnmonitored = true;
        config.History.CountSonarr = true;
        config.History.AutoRefreshMode = 0;
        config.History.AutoRefreshAddToQueue = true;

        await scheduler.RunCheckAsync(provider, config, CancellationToken.None);

        queue.Verify(service => service.AddToQueue(It.IsAny<EpisodeInfo>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_SerializesOverlappingManualAndTimedChecks()
    {
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int active = 0;
        int maxActive = 0;
        var history = new Mock<IHistoryService>();
        history.Setup(service => service.GetHistorySeriesAsync()).ReturnsAsync(
        [
            new HistorySeries { SeriesId = "SERIES1", SeriesTitle = "Example Show" }
        ]);
        history.Setup(service => service.CrUpdateSeriesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(async () =>
            {
                var call = Interlocked.Increment(ref calls);
                var nowActive = Interlocked.Increment(ref active);
                int observedMax;
                do
                {
                    observedMax = Volatile.Read(ref maxActive);
                }
                while (nowActive > observedMax &&
                       Interlocked.CompareExchange(ref maxActive, nowActive, observedMax) != observedMax);

                try
                {
                    if (call == 1)
                    {
                        firstEntered.TrySetResult(true);
                        await releaseFirst.Task.WaitAsync(TestContext.Current.CancellationToken);
                    }
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        using var provider = new ServiceCollection()
            .AddSingleton(history.Object)
            .AddSingleton(Mock.Of<IQueueService>())
            .AddSingleton(Mock.Of<ICrunchyrollApiService>())
            .AddSingleton(Mock.Of<ICrunchyrollAuthService>())
            .BuildServiceProvider();
        using var scheduler = new AutoDownloadSchedulerService(
            provider,
            NullLogger<AutoDownloadSchedulerService>.Instance);
        var config = new CruncharrConfig();
        config.History.Enabled = true;
        config.History.AutoRefreshMode = 0;

        var first = scheduler.RunCheckAsync(provider, config, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = scheduler.RunCheckAsync(provider, config, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsRunning);
        Assert.Equal(1, Volatile.Read(ref calls));

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maxActive));
        Assert.False(scheduler.IsRunning);
        Assert.NotNull(scheduler.LastRun);
    }

    [Fact]
    public async Task RestoredRetry_WakesAndStartsWhenRetryTimeArrives()
    {
        var queuePath = Path.Combine(Path.GetTempPath(), $"cruncharr-queue-{Guid.NewGuid():N}.json");
        try
        {
            using var persistence = new QueuePersistenceService(queuePath);
            persistence.SaveQueue(new List<QueueItem>
            {
                new()
                {
                    Episode = new EpisodeInfo { Id = "restored-retry", Title = "Episode" },
                    DownloadProgress = new DownloadProgress
                    {
                        State = DownloadState.Queued,
                        RetryAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(300)
                    }
                }
            });

            var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloadService = new Mock<IDownloadService>();
            downloadService
                .Setup(service => service.DownloadEpisodeAsync(
                    It.IsAny<EpisodeInfo>(),
                    It.IsAny<CruncharrConfig>(),
                    It.IsAny<IProgress<DownloadProgress>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action?>()))
                .Callback(() => downloadStarted.TrySetResult(true))
                .ReturnsAsync(new DownloadResult { Success = true });

            using var provider = new ServiceCollection()
                .AddSingleton(downloadService.Object)
                .BuildServiceProvider();
            using var queue = new QueueService(provider, persistenceService: persistence);
            using var stop = new CancellationTokenSource();
            var config = new CruncharrConfig();
            config.Queue.AutoDownload = true;

            var processor = queue.ProcessQueueAsync(config, cancellationToken: stop.Token);
            queue.SetInitialized(true);

            Assert.True(await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            await WaitForAsync(() => !File.Exists(queuePath) && !File.Exists(queuePath + ".tmp"));

            stop.Cancel();
            await processor;
        }
        finally
        {
            File.Delete(queuePath);
            File.Delete(queuePath + ".tmp");
        }
    }

    [Fact]
    public async Task ProcessorStart_RequestsPumpWhenInitializedFirst()
    {
        var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadService = new Mock<IDownloadService>();
        downloadService
            .Setup(service => service.DownloadEpisodeAsync(
                It.IsAny<EpisodeInfo>(),
                It.IsAny<CruncharrConfig>(),
                It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action?>()))
            .Callback(() => downloadStarted.TrySetResult(true))
            .ReturnsAsync(new DownloadResult { Success = true });

        using var provider = new ServiceCollection()
            .AddSingleton(downloadService.Object)
            .BuildServiceProvider();
        using var queue = new QueueService(provider);
        queue.AddToQueue(new EpisodeInfo { Id = "startup-order", Title = "Episode" });
        queue.SetInitialized(true);

        using var stop = new CancellationTokenSource();
        var config = new CruncharrConfig();
        config.Queue.AutoDownload = true;
        var processor = queue.ProcessQueueAsync(config, cancellationToken: stop.Token);

        Assert.True(await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        stop.Cancel();
        await processor;
    }

    [Fact]
    public async Task ReplaceQueue_CancelsTheWorkItReplaces()
    {
        var downloadService = new BlockingDownloadService();
        using var provider = new ServiceCollection()
            .AddSingleton<IDownloadService>(downloadService)
            .BuildServiceProvider();
        using var queue = new QueueService(provider);
        using var stop = new CancellationTokenSource();
        var config = new CruncharrConfig();
        var processor = queue.ProcessQueueAsync(config, cancellationToken: stop.Token);

        queue.AddToQueue(new EpisodeInfo { Id = "old-item", Title = "Old" });
        var oldItem = Assert.Single(queue.GetQueue());
        Assert.True(queue.StartItem(oldItem.Id));
        Assert.True(await downloadService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        queue.ReplaceQueue(new List<QueueItem> { Item("new-item", DownloadState.Queued) });

        Assert.True(await downloadService.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal("new-item", Assert.Single(queue.GetQueue()).Episode.Id);

        stop.Cancel();
        await processor;
    }

    [Fact]
    public void Persistence_PreservesPausedAndCancelledButRequeuesInterruptedWork()
    {
        var queuePath = Path.Combine(Path.GetTempPath(), $"cruncharr-queue-{Guid.NewGuid():N}.json");
        try
        {
            using var persistence = new QueuePersistenceService(queuePath);
            persistence.SaveQueue(new List<QueueItem>
            {
                Item("paused", DownloadState.Paused),
                Item("cancelled", DownloadState.Cancelled),
                Item("downloading", DownloadState.Downloading),
                Item("processing", DownloadState.Processing)
            });

            var restored = Assert.IsType<List<QueueItem>>(persistence.LoadQueue());
            Assert.Equal(DownloadState.Paused, restored.Single(item => item.Episode.Id == "paused").DownloadProgress.State);
            Assert.Equal(DownloadState.Cancelled, restored.Single(item => item.Episode.Id == "cancelled").DownloadProgress.State);
            Assert.Equal(DownloadState.Queued, restored.Single(item => item.Episode.Id == "downloading").DownloadProgress.State);
            Assert.Equal(DownloadState.Queued, restored.Single(item => item.Episode.Id == "processing").DownloadProgress.State);
        }
        finally
        {
            File.Delete(queuePath);
            File.Delete(queuePath + ".tmp");
        }
    }

    private static QueueItem Item(string id, DownloadState state) => new()
    {
        Episode = new EpisodeInfo { Id = id, Title = id },
        DownloadProgress = new DownloadProgress { State = state }
    };

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate(), "Condition was not reached before timeout.");
    }

    private sealed class BlockingDownloadService : IDownloadService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, Action? onDownloadComplete = null)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new DownloadResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }

        public Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
