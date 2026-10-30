using System.Net;
using System.Reflection;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using CrBrowseEpisodeBase = Cruncharr.Core.Services.CrBrowseEpisodeBase;
using CrBrowseEpisode = Cruncharr.Core.Services.CrBrowseEpisode;
using CrBrowseEpisodeMetaData = Cruncharr.Core.Services.CrBrowseEpisodeMetaData;

namespace Cruncharr.Core.Tests;

public class CalendarLoadingTests
{
    private static readonly DateTime Date = new(2026, 10, 21);

    private static CrBrowseEpisodeBase Episodes() => new()
    {
        Data = [new CrBrowseEpisode
        {
            Id = "EPISODE1", Title = "Episode 1",
            EpisodeMetadata = new CrBrowseEpisodeMetaData
            {
                SeriesId = "SERIES1", SeriesTitle = "Example", SeasonTitle = "Season 1",
                Episode = "1", AudioLocale = "en-US", PremiumAvailableDate = Date
            }
        }]
    };

    private static Mock<ICrunchyrollApiService> Api()
    {
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Episodes);
        return api;
    }

    private static HttpClientWrapper SetTransport(CalendarService service, HttpMessageHandler handler)
    {
        var wrapper = (HttpClientWrapper)typeof(CalendarService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        wrapper.Client.Dispose();
        typeof(HttpClientWrapper).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(wrapper, new HttpClient(handler));
        return wrapper;
    }

    [Fact]
    public async Task UpcomingSetting_SkipsNetworkWhenDisabledAndChangesCachedCalendar()
    {
        var config = new CruncharrConfig();
        config.Calendar.ShowUpcomingEpisodes = false;
        var service = new CalendarService(Api().Object, config: config);
        var calls = 0;
        using var transport = SetTransport(service, new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new
                {
                    data = new
                    {
                        Page = new
                        {
                            pageInfo = new { hasNextPage = false },
                            airingSchedules = new[] {
                        new { episode = 2, airingAt = new DateTimeOffset(Date.AddHours(12)).ToUnixTimeSeconds(),
                            media = new { title = new { english = "Upcoming show" },
                                externalLinks = new[] { new { site = "Crunchyroll", url = "https://www.crunchyroll.com/series/UPCOMING1" } } } }
                    }
                        }
                    }
                }))
            });
        }));

        var disabled = await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Single(disabled.CalendarDays!.SelectMany(day => day.CalendarEpisodes));

        config.Calendar.ShowUpcomingEpisodes = true;
        var enabled = await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Contains(enabled.CalendarDays!.SelectMany(day => day.CalendarEpisodes), episode => episode.AnilistEpisode);

        config.Calendar.ShowUpcomingEpisodes = false;
        var disabledAgain = await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(disabledAgain.CalendarDays!.SelectMany(day => day.CalendarEpisodes), episode => episode.AnilistEpisode);
        Assert.Equal(1, calls);
    }

    // Live 2026-10-30: every grey Calendar dot was an episode Sonarr had a file for, because the
    // Calendar only looked at whether Cruncharr itself had downloaded it.
    [Theory]
    [InlineData(true, true, false, CalendarHistoryDownloadState.Downloaded)]
    [InlineData(false, true, false, CalendarHistoryDownloadState.NotDownloaded)] // "count Sonarr" is off
    [InlineData(true, false, false, CalendarHistoryDownloadState.NotDownloaded)]
    [InlineData(true, false, true, CalendarHistoryDownloadState.Downloaded)]
    public async Task HistoryMark_CountsAFileSonarrOwns(bool countSonarr, bool sonarrHasFile, bool wasDownloaded, CalendarHistoryDownloadState expected)
    {
        var config = new CruncharrConfig();
        config.History.Enabled = true;
        config.History.CountSonarr = countSonarr;
        config.History.CheckPartialDownloads = false;
        var history = new Mock<IHistoryService>();
        history.Setup(value => value.GetHistorySeriesAsync()).ReturnsAsync([new HistorySeries
        {
            SeriesId = "SERIES1",
            Seasons = [new HistorySeason
            {
                SeasonId = "SEASON1",
                EpisodesList = [new HistoryEpisode { EpisodeId = "EPISODE1", SonarrHasFile = sonarrHasFile, WasDownloaded = wasDownloaded }]
            }]
        }]);
        var service = new CalendarService(Api().Object, null, history.Object, config);

        var week = await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);

        var episode = Assert.Single(week.CalendarDays!.SelectMany(day => day.CalendarEpisodes));
        Assert.True(episode.IsInHistory);
        Assert.Equal(expected, episode.HistoryDownloadState);
    }

    [Fact]
    public async Task ConcurrentLoads_ShareFetchButNotMutableResponseData()
    {
        var api = Api();
        var pending = new TaskCompletionSource<CrBrowseEpisodeBase?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Setup(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var service = new CalendarService(api.Object);
        var first = service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        var second = service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        pending.SetResult(Episodes());
        var weeks = await Task.WhenAll(first, second);

        api.Verify(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()), Times.Once);
        var episode = Assert.Single(weeks[0].CalendarDays!.SelectMany(day => day.CalendarEpisodes));
        episode.HistoryDownloadState = CalendarHistoryDownloadState.Downloaded;
        episode.CalendarEpisodes.Add(new CalendarEpisode());
        var otherEpisode = Assert.Single(weeks[1].CalendarDays!.SelectMany(day => day.CalendarEpisodes));
        Assert.Empty(otherEpisode.CalendarEpisodes);
        Assert.Equal(CalendarHistoryDownloadState.None, otherEpisode.HistoryDownloadState);
    }

    [Fact]
    public async Task CachedWeek_DoesNotWaitForAnotherWeeksSlowFetch()
    {
        var api = Api();
        var service = new CalendarService(api.Object);
        await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        var pending = new TaskCompletionSource<CrBrowseEpisodeBase?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Setup(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var coldWeek = service.GetCustomCalendarAsync(Date.AddDays(7), "en-US", cancellationToken: TestContext.Current.CancellationToken);
        var warmWeek = service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(warmWeek.IsCompletedSuccessfully);
        Assert.False(coldWeek.IsCompleted);
        pending.SetResult(Episodes());
        await coldWeek;
        Assert.Single((await warmWeek).CalendarDays!.SelectMany(day => day.CalendarEpisodes));
    }

    [Fact]
    public async Task FailedFetch_IsRetryableAndDoesNotCacheAnEmptyWeek()
    {
        var api = Api();
        api.SetupSequence(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrBrowseEpisodeBase?)null).ReturnsAsync(Episodes()).ReturnsAsync((CrBrowseEpisodeBase?)null);
        var service = new CalendarService(api.Object);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken));
        var fresh = await service.GetCustomCalendarAsync(Date, "en-US", cancellationToken: TestContext.Current.CancellationToken);
        var stale = await service.GetCustomCalendarAsync(Date, "en-US", true, TestContext.Current.CancellationToken);
        Assert.Single(fresh.CalendarDays!.SelectMany(day => day.CalendarEpisodes));
        Assert.Single(stale.CalendarDays!.SelectMany(day => day.CalendarEpisodes));
    }

    [Fact]
    public async Task LeavingCalendar_CancelsUpstreamAndAllowsAnotherLoad()
    {
        var api = Api();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Setup(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, DateTime? _, bool _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Episodes();
            });
        var service = new CalendarService(api.Object);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var controller = new CalendarController(service, new CruncharrConfig(), NullLogger<CalendarController>.Instance);
        var request = controller.GetCustomCalendar("2026-10-21", "en-US", cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        api.Setup(value => value.GetNewEpisodesAsync("en-US", 2000, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>())).ReturnsAsync(Episodes);
        Assert.IsType<OkObjectResult>((await controller.GetCustomCalendar("2026-10-21", "en-US", cancellationToken: TestContext.Current.CancellationToken)).Result);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
