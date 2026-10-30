using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Moq;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Tests;

public class ScheduledDownloadsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "subscriptions-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IHistoryService> _history = new();
    private readonly Mock<ICrunchyrollAuthService> _auth = new();
    private readonly Mock<ISonarrService> _sonarr = new();
    private readonly Mock<IQueueService> _queue = new();
    private readonly CruncharrConfig _config = new();
    private readonly List<EpisodeInfo> _queued = [];
    private readonly HistorySeries _series = new()
    {
        SeriesId = "SERIES",
        SeriesTitle = "Example",
        SonarrSeriesId = "10",
        Seasons = [new HistorySeason { SeasonId = "S1", SeasonNum = "1", EpisodesList = [] }]
    };

    public ScheduledDownloadsTests()
    {
        _history.Setup(h => h.CrUpdateSeriesAsync("SERIES", "")).ReturnsAsync(true);
        _history.Setup(h => h.GetHistorySeriesAsync()).ReturnsAsync([_series]);
        _history.Setup(h => h.GetAllAsync(0, int.MaxValue)).ReturnsAsync([]);
        _auth.SetupGet(a => a.IsAuthenticated).Returns(true);
        _auth.SetupGet(a => a.Profile).Returns(new CrProfile { HasPremium = true });
        _queue.Setup(q => q.AddToQueue(It.IsAny<EpisodeInfo>())).Returns<EpisodeInfo>(e =>
        {
            _queued.Add(e);
            return new QueueAddResult(true, new QueueItem { Episode = e });
        });
    }

    private ScheduledDownloadsService Service() => new(_history.Object, _queue.Object, _auth.Object,
        _sonarr.Object, _config, Path.Combine(_root, "subscriptions.json"));
    private HistoryEpisode Episode(string id, params string[] dubs)
    {
        var episode = new HistoryEpisode
        {
            EpisodeId = id,
            Episode = "1",
            IsEpisodeAvailableOnStreamingService = true,
            HistoryEpisodeAvailableDubLang = dubs.Length > 0 ? dubs.ToList() : ["ja-JP"]
        };
        _series.Seasons[0].EpisodesList.Add(episode);
        return episode;
    }

    [Fact]
    public async Task GuestSubscriptionDelegatesNewEpisodesToSonarrOnce()
    {
        _auth.SetupGet(a => a.IsAuthenticated).Returns(false);
        _config.Sonarr.Enabled = true;
        _config.Sonarr.SearchWithoutPremium = true;
        _sonarr.Setup(s => s.GetCurrentEpisodesAsync(10, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var requests = new Mock<ISonarrAcquisitionService>();
        requests.Setup(r => r.PrepareAsync(It.IsAny<EpisodeInfo>(), false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrAcquisitionState());
        var service = new ScheduledDownloadsService(_history.Object, _queue.Object, _auth.Object,
            _sonarr.Object, _config, Path.Combine(_root, "subscriptions.json"), requests.Object);
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW");
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        requests.Verify(r => r.PrepareAsync(It.Is<EpisodeInfo>(e => e.Id == "NEW" && e.SeriesId == "SERIES"), false, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribingSeedsBackCatalogAndNewEpisodesAreQueuedOnceAcrossRestart()
    {
        Episode("OLD");
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        Episode("NEW");
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        await Service().RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal("NEW", Assert.Single(_queued).Id);
    }

    [Fact]
    public async Task DelayedDubWaitsAndUsesSeasonOverrides()
    {
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        _series.HistorySeriesDubLangOverride = ["fr-FR"];
        _series.Seasons[0].HistorySeasonDubLangOverride = ["en-US"];
        _series.Seasons[0].HistorySeasonSoftSubsOverride = ["es-419"];
        var episode = Episode("NEW");
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        episode.HistoryEpisodeAvailableDubLang.Add("en-US");
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["en-US"], Assert.Single(_queued).SelectedDubs);
        Assert.Equal(["es-419"], _queued[0].SelectedSubs);
        Assert.Equal("S1", _queued[0].SeasonId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task GuestOrFreeAccountNeverQueuesAndCanCatchUpAfterPremiumLogin(bool loggedIn, bool premium)
    {
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW");
        _auth.SetupGet(a => a.IsAuthenticated).Returns(loggedIn);
        _auth.SetupGet(a => a.Profile).Returns(new CrProfile { HasPremium = premium });
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        _auth.SetupGet(a => a.IsAuthenticated).Returns(true);
        _auth.SetupGet(a => a.Profile).Returns(new CrProfile { HasPremium = true });
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task FutureEpisodesAreNotIncludedInBaselineAndWaitUntilRelease()
    {
        var episode = Episode("FUTURE");
        episode.EpisodeCrPremiumAirDate = DateTime.UtcNow.AddDays(1);
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        episode.EpisodeCrPremiumAirDate = DateTime.UtcNow.AddMinutes(-1);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task SonarrFilesAreSkippedEvenWhenHistoryCountSettingIsOff()
    {
        _config.Sonarr.Enabled = true;
        _config.History.CountSonarr = false;
        _sonarr.Setup(s => s.GetCurrentEpisodesAsync(10, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrEpisode { Id = 20, HasFile = true }]);
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("IN-SONARR").SonarrEpisodeId = "20";
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
    }

    [Fact]
    public async Task UnsavedQueueAdmissionIsRetriedInsteadOfMarkedHandled()
    {
        var persisted = false;
        _queue.Setup(q => q.AddToQueue(It.IsAny<EpisodeInfo>())).Returns<EpisodeInfo>(e =>
        {
            _queued.Add(e);
            return new QueueAddResult(true, new QueueItem { Episode = e }, persisted);
        });
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW");

        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Contains("could not be saved", JObject.FromObject(service.GetStatus())["LastError"]!.Value<string>());

        persisted = true;
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        await Service().RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["NEW", "NEW"], _queued.Select(e => e.Id));
    }

    [Fact]
    public async Task SonarrFailureDoesNotQueueOrConsumePendingReleases()
    {
        _config.Sonarr.Enabled = true;
        _sonarr.Setup(s => s.GetCurrentEpisodesAsync(10, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW").SonarrEpisodeId = "20";
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        Assert.Contains("Sonarr is unavailable", JObject.FromObject(service.GetStatus())["LastError"]!.Value<string>());
        _sonarr.Setup(s => s.GetCurrentEpisodesAsync(10, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrEpisode { Id = 20, HasFile = false }]);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task PausingAndIntervalChangesPersistAndApplyWithoutRestart()
    {
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW");
        await service.ConfigureAsync(false, 1, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        service = Service();
        Assert.False(JObject.FromObject(service.GetStatus())["Enabled"]!.Value<bool>());
        await service.ConfigureAsync(true, 1, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
        Episode("NEW2");
        await service.RunCheckAsync(false, TestContext.Current.CancellationToken); // interval hasn't elapsed
        Assert.Single(_queued);
        await service.SubscribeAsync("SERIES", false, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task FailedBaselineDoesNotCreateSubscription()
    {
        _history.Setup(h => h.CrUpdateSeriesAsync("SERIES", "")).ReturnsAsync(false);
        var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken));
        Assert.Empty((JArray)JObject.FromObject(service.GetStatus())["Subscriptions"]!);
    }

    [Fact]
    public async Task CorruptStorageDisablesSchedulerWithoutOverwritingBaselineOrBreakingAppStartup()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "subscriptions.json");
        File.WriteAllText(path, "broken");
        var service = Service();
        await service.RunCheckAsync(false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureAsync(true, 15, TestContext.Current.CancellationToken));
        Assert.Equal("broken", File.ReadAllText(path));
    }

    // Live 2026-10-30 (Firefly Wedding, HORROR COLLECTOR, ...): episodes 2-3 were out in Japanese
    // when the user subscribed with English selected, so the baseline marked them handled and their
    // English dubs would never have been queued.
    [Fact]
    public async Task EpisodeAwaitingTheSelectedDubAtSubscribeIsQueuedOnceTheDubArrives()
    {
        _config.Download.DefaultAudio = "en-US";
        var episode = Episode("JP-ONLY", "ja-JP");
        Episode("HAS-DUB", "ja-JP", "en-US"); // released with the dub already: back catalog, not queued
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);

        var waiting = await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);
        Assert.Equal((1, 1), (waiting!.Shows[0].WaitingForLanguage, waiting.Shows[0].MissingFromBaseline));

        episode.HistoryEpisodeAvailableDubLang.Add("en-US");
        await Service().RunCheckAsync(true, TestContext.Current.CancellationToken); // across a restart
        await Service().RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal("JP-ONLY", Assert.Single(_queued).Id);
        Assert.Equal(["en-US"], _queued[0].SelectedDubs);
    }

    [Fact]
    public async Task RunSummaryExplainsEveryEpisodeThatWasNotAdded()
    {
        Episode("OLD"); // released before subscribing, no file anywhere
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        Episode("NEW");
        Episode("DUB-LATER", "en-US"); // default audio is ja-JP; only English is out so far
        Episode("FUTURE").EpisodeCrPremiumAirDate = DateTime.UtcNow.AddDays(1);

        var summary = await service.RunCheckAsync(true, TestContext.Current.CancellationToken);

        var show = Assert.Single(summary!.Shows);
        Assert.Equal((1, 1, 1, 1, 0), (show.Added, show.MissingFromBaseline, show.WaitingForLanguage, show.NotReleased, summary.Failed));
        Assert.Null(show.Error);
        // The same summary survives for the Settings page.
        Assert.Equal(1, JObject.FromObject(service.GetStatus())["LastRunSummary"]!["Shows"]![0]!["MissingFromBaseline"]!.Value<int>());
    }

    [Fact]
    public async Task RunSummaryReportsAPartialFailure()
    {
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        _history.Setup(h => h.CrUpdateSeriesAsync("SERIES", "")).ReturnsAsync(false);

        var summary = await service.RunCheckAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary!.Failed);
        Assert.Contains("Could not refresh", summary.Shows[0].Error);
    }

    [Fact]
    public async Task BackfillQueuesEpisodesMissingSinceSubscribingExactlyOnce()
    {
        Episode("OLD");
        var service = Service();
        await service.SubscribeAsync("SERIES", true, TestContext.Current.CancellationToken);
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(_queued);

        var summary = await service.BackfillAsync("SERIES", TestContext.Current.CancellationToken);
        Assert.True(summary.Backfill);
        Assert.Equal(1, summary.Added);
        Assert.Equal("OLD", Assert.Single(_queued).Id);

        // Scheduled checks, including after a restart, do not queue it again.
        await service.RunCheckAsync(true, TestContext.Current.CancellationToken);
        await Service().RunCheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(_queued);
        await Assert.ThrowsAsync<ArgumentException>(() => service.BackfillAsync("OTHER", TestContext.Current.CancellationToken));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
