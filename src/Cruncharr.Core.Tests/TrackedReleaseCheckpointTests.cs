using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CrBrowseEpisode = Cruncharr.Core.Services.CrBrowseEpisode;
using CrBrowseEpisodeBase = Cruncharr.Core.Services.CrBrowseEpisodeBase;
using CrBrowseEpisodeMetaData = Cruncharr.Core.Services.CrBrowseEpisodeMetaData;

namespace Cruncharr.Core.Tests;

// Audit F10: the tracked-release notifier ignored its stored TrackedSeriesReleaseLastCheckUtc on the
// first run after a restart (that run only seeded an in-memory baseline) and never wrote it back,
// so releases during downtime were never announced.
[Collection("ConfigurationEnvironment")]
public class TrackedReleaseCheckpointTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-release-checkpoint-");
    private readonly string? _previous = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");
    private readonly Mock<IHistoryService> _history = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly List<string> _announced = [];
    private List<CrBrowseEpisode> _feed = [];
    private readonly CruncharrConfig _config = new();

    public TrackedReleaseCheckpointTests()
    {
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", ConfigPath);
        _history.Setup(h => h.GetHistorySeriesAsync()).ReturnsAsync([new HistorySeries { SeriesId = "TRACKED", SeriesTitle = "Tracked Show" }]);
        _notifications.Setup(n => n.NotifyTrackedSeriesReleasedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CruncharrConfig>()))
            .Callback<string, string, string, CruncharrConfig>((_, _, id, _) => _announced.Add(id))
            .Returns(Task.CompletedTask);
        _config.History.Enabled = true;
        _config.History.AutoRefreshMode = 50;
        _config.History.AutoRefreshAddToQueue = false;
        _config.Notifications.NotifyTrackedSeriesReleased = true;
    }

    private string ConfigPath => Path.Combine(_root.FullName, "cruncharr.yaml");

    [Fact]
    public async Task Restart_AnnouncesReleasesSinceTheStoredCheckpointOnce()
    {
        var checkpoint = DateTime.UtcNow.AddHours(-2);
        _config.TrackedSeriesReleaseLastCheckUtc = checkpoint;
        _feed =
        [
            Release("DURING_DOWNTIME", "TRACKED", hoursAgo: 1),
            Release("DURING_DOWNTIME", "TRACKED", hoursAgo: 1), // duplicate feed entry
            Release("BEFORE_CHECKPOINT", "TRACKED", hoursAgo: 3),
            Release("UNTRACKED", "OTHER", hoursAgo: 1)
        ];

        var scheduler = Scheduler();
        await RunAsync(scheduler);

        Assert.Equal(["DURING_DOWNTIME"], _announced);
        var saved = CruncharrConfig.Load(ConfigPath).TrackedSeriesReleaseLastCheckUtc;
        Assert.NotNull(saved);
        Assert.True(saved.Value.ToUniversalTime() > checkpoint.AddMinutes(1));

        // The next check (and the next restart) does not announce it again.
        await RunAsync(scheduler);
        await RunAsync(Scheduler(), CruncharrConfig.Load(ConfigPath));
        Assert.Equal(["DURING_DOWNTIME"], _announced);
    }

    [Fact]
    public async Task FirstInstall_SeedsTheCheckpointWithoutAnnouncingOldReleases()
    {
        _feed = [Release("OLD", "TRACKED", hoursAgo: 1)];

        await RunAsync(Scheduler());

        Assert.Empty(_announced);
        Assert.NotNull(CruncharrConfig.Load(ConfigPath).TrackedSeriesReleaseLastCheckUtc);
    }

    [Fact]
    public async Task FailedCheck_KeepsTheCheckpointAndRetries()
    {
        var checkpoint = DateTime.UtcNow.AddHours(-2);
        _config.TrackedSeriesReleaseLastCheckUtc = checkpoint;
        _feed = [Release("MISSED", "TRACKED", hoursAgo: 1)];
        _history.SetupSequence(h => h.GetHistorySeriesAsync())
            .ThrowsAsync(new IOException("History unreadable"))
            .ReturnsAsync([new HistorySeries { SeriesId = "TRACKED", SeriesTitle = "Tracked Show" }]);

        var scheduler = Scheduler();
        await RunAsync(scheduler);
        Assert.Empty(_announced);
        Assert.Equal(checkpoint, _config.TrackedSeriesReleaseLastCheckUtc);
        Assert.False(File.Exists(ConfigPath));

        await RunAsync(scheduler);
        Assert.Equal(["MISSED"], _announced);
    }

    [Fact]
    public async Task LongDowntime_CatchesUpOnlyARecentWindow()
    {
        _config.TrackedSeriesReleaseLastCheckUtc = DateTime.UtcNow.AddDays(-30);
        _feed = [Release("WEEKS_AGO", "TRACKED", hoursAgo: 24 * 10), Release("YESTERDAY", "TRACKED", hoursAgo: 24)];

        await RunAsync(Scheduler());

        Assert.Equal(["YESTERDAY"], _announced);
    }

    private AutoDownloadSchedulerService Scheduler() => new(Provider(), NullLogger<AutoDownloadSchedulerService>.Instance);

    private Task RunAsync(AutoDownloadSchedulerService scheduler, CruncharrConfig? config = null) =>
        scheduler.RunCheckAsync(Provider(), config ?? _config, CancellationToken.None);

    private ServiceProvider Provider()
    {
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetNewEpisodesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CrBrowseEpisodeBase { Data = _feed });
        var auth = Mock.Of<ICrunchyrollAuthService>(a => a.Token == new CrToken { access_token = "guest" } && a.Profile == new CrProfile());
        return new ServiceCollection()
            .AddSingleton(_history.Object)
            .AddSingleton(Mock.Of<IQueueService>())
            .AddSingleton(api.Object)
            .AddSingleton(auth)
            .AddSingleton(_notifications.Object)
            .BuildServiceProvider();
    }

    private static CrBrowseEpisode Release(string id, string seriesId, double hoursAgo) => new()
    {
        Id = id,
        Title = id,
        EpisodeMetadata = new CrBrowseEpisodeMetaData { SeriesId = seriesId, PremiumAvailableDate = DateTime.UtcNow.AddHours(-hoursAgo) }
    };

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", _previous);
        _root.Delete(recursive: true);
    }
}
