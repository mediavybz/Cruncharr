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

namespace Cruncharr.Core.Tests;

public class GuestCatalogAccessTests
{
    private const string Series = """
        {"total":1,"data":[{"id":"GSERIES01","title":"Naruto","series_metadata":{"episode_count":1}}]}
        """;
    private const string Seasons = """
        {"total":1,"data":[{"id":"GSEASON01","title":"Season 1","season_number":1,"identifier":"GSERIES01|S1"}]}
        """;
    private const string Episodes = """
        {"total":1,"data":[{"id":"GEPISODE1","title":"Episode 1","episode":"1","episode_number":1,"season_number":1,"series_id":"GSERIES01","series_title":"Naruto","season_id":"GSEASON01","season_title":"Season 1","audio_locale":"ja-JP","is_premium_only":true}]}
        """;

    private static Mock<ICrunchyrollAuthService> Guest()
    {
        var auth = new Mock<ICrunchyrollAuthService>();
        auth.SetupGet(value => value.Token).Returns(new CrToken { access_token = "guest" });
        auth.SetupGet(value => value.Profile).Returns(new CrProfile { Username = "???" });
        auth.Setup(value => value.RefreshTokenAsync(true, It.IsAny<CancellationToken>(), false)).ReturnsAsync(true);
        return auth;
    }

    private static HttpClientWrapper ApiTransport(CrunchyrollApiService api) =>
        (HttpClientWrapper)typeof(CrunchyrollApiService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(api)!;

    private static void SetTransport(HttpClientWrapper wrapper, HttpMessageHandler handler)
    {
        wrapper.Client.Dispose();
        typeof(HttpClientWrapper).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(wrapper, new HttpClient(handler));
    }

    [Fact]
    public void GuestCanSelectPremiumEpisodeMetadataForSonarrRequests()
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        var selected = api.ItemSelectMultiDub(new Dictionary<string, EpisodeAndLanguage> {
            ["1"] = new() { Variants = [new EpisodeVariant(new CrEpisodeDetail {
                Id = "GEPISODE1", Episode = "1", Title = "Example", SeriesTitle = "Example", SeriesId = "GSERIES01",
                SeasonId = "GSEASON01", IsPremiumOnly = true
            }, new LanguageItem { CrLocale = "ja-JP" })] }
        }, ["ja-JP"], false, ["1"]);
        Assert.Equal("GEPISODE1", Assert.Single(selected).Value.EpisodeId);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("direct-search")]
    [InlineData("series")]
    [InlineData("episodes")]
    [InlineData("episode")]
    [InlineData("parse-episode")]
    [InlineData("seasons")]
    [InlineData("season-episodes")]
    [InlineData("list")]
    [InlineData("browse")]
    public async Task PublicCatalogReads_WorkWithGuestToken(string operation)
    {
        var auth = Guest();
        using var api = new CrunchyrollApiService(auth.Object);
        var requests = 0;
        SetTransport(ApiTransport(api), new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("guest", request.Headers.Authorization?.Parameter);
            Assert.Equal("beta-api.crunchyroll.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            var body = path.EndsWith("/search") ? """
                {"data":[{"type":"series","count":1,"items":[{"id":"GSERIES01","title":"Naruto","type":"series"}]}]}
                """ : path.EndsWith("/seasons") ? Seasons : path.Contains("/episodes") ? Episodes : Series;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));

        switch (operation)
        {
            case "search": Assert.Single(await api.SearchAsync("Naruto", false, TestContext.Current.CancellationToken)); break;
            case "direct-search": Assert.Single(await api.SearchAsync("https://www.crunchyroll.com/series/GSERIES01/naruto", false, TestContext.Current.CancellationToken)); break;
            case "series": Assert.NotNull(await api.GetSeriesAsync("GSERIES01", false, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "episodes": Assert.Single(await api.GetEpisodesAsync("GSERIES01", false, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "episode": Assert.NotNull(await api.GetEpisodeAsync("GEPISODE1", false, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "parse-episode": Assert.NotNull(await api.ParseEpisodeByIdAsync("GEPISODE1", null, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "seasons": Assert.Single(await api.ParseSeriesByIdAsync("GSERIES01", null, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "season-episodes": Assert.Single(await api.GetSeasonDataByIdAsync("GSEASON01", null, cancellationToken: TestContext.Current.CancellationToken)); break;
            case "list": Assert.NotEmpty((await api.ListSeriesIdAsync("GSERIES01", "", null, cancellationToken: TestContext.Current.CancellationToken))!.List); break;
            case "browse": Assert.Single(await api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken)); break;
        }
        Assert.True(requests > 0);
        auth.Verify(value => value.AuthenticateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogFailures_AreNotReturnedAsEmptySuccess(bool browse)
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        SetTransport(ApiTransport(api), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => browse ? api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken) : api.SearchAsync("Naruto", true, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PartialCatalog_IsNotReturnedAsComplete()
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        var requests = 0;
        SetTransport(ApiTransport(api), new Handler((_, _) => Task.FromResult(++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Series.Replace("\"total\":1", "\"total\":101")) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CatalogPagination_AdvancesByReturnedPageSizeAndKeepsZeroEpisodeTitles()
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        var starts = new List<string>();
        SetTransport(ApiTransport(api), new Handler((request, _) =>
        {
            var start = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["start"]!;
            starts.Add(start);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"total\":3,\"data\":[{{\"id\":\"SERIES{start}\",\"title\":\"Title {start}\",\"series_metadata\":{{\"episode_count\":0}}}}]}}")
            });
        }));
        var result = await api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "0", "1", "2" }, starts);
        Assert.Equal(3, result.Count);
        Assert.All(result, series => Assert.Equal(0, series.EpisodeCount));
    }

    [Fact]
    public async Task CatalogPagination_RejectsRepeatedPageInsteadOfLoopingOrReturningPartialSuccess()
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        var calls = 0;
        SetTransport(ApiTransport(api), new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Series.Replace("\"total\":1", "\"total\":3")) });
        }));
        await Assert.ThrowsAsync<Newtonsoft.Json.JsonException>(() => api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SearchCancellation_StopsTheUpstreamRequest()
    {
        using var api = new CrunchyrollApiService(Guest().Object);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetTransport(ApiTransport(api), new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource();
        var search = api.SearchAsync("Naruto", true, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotSendAStaleBearer()
    {
        var auth = Guest();
        auth.Setup(value => value.RefreshTokenAsync(true, It.IsAny<CancellationToken>(), false)).ReturnsAsync(false);
        using var api = new CrunchyrollApiService(auth.Object);
        Assert.False(await api.EnsureTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FreshGuestSession_IsReusedAcrossConcurrentRequests()
    {
        var auth = new CrunchyrollAuthService(new CruncharrConfig { TokenFilePath = Path.Combine(Path.GetTempPath(), $"guest-{Guid.NewGuid():N}.json") });
        using var transport = auth.HttpClient;
        var token = new CrToken { access_token = "guest", expires = DateTime.Now.AddMinutes(10) };
        typeof(CrunchyrollAuthService).GetProperty(nameof(auth.Token))!.SetValue(auth, token);
        SetTransport(transport, new Handler((_, _) => throw new InvalidOperationException("Fresh guests should not make auth requests.")));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            Assert.False(await auth.AuthenticateAsync(true));
            Assert.True(await auth.RefreshTokenAsync(true));
        }));
        Assert.Same(token, auth.Token);
        Assert.False(auth.IsAuthenticated);
    }

    [Fact]
    public async Task GuestStatus_DoesNotRequestAccountProfiles()
    {
        var auth = Guest();
        auth.SetupGet(value => value.Profile).Returns(new CrProfile { Username = "???", PreferredContentAudioLanguage = "ja-JP", PreferredContentSubtitleLanguage = "de-DE" });
        var controller = new AuthController(auth.Object);
        var result = await controller.GetStatus(refresh: true);
        var status = Assert.IsType<AuthStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(status.PreferredAudioLanguage);
        Assert.Empty(status.PreferredSubtitleLanguage);
        auth.Verify(value => value.GetMultiProfileAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Downloads_RequireBothLoginAndPremium(bool loggedIn, bool premium)
    {
        var auth = Guest();
        auth.SetupGet(value => value.IsAuthenticated).Returns(loggedIn);
        auth.SetupGet(value => value.Profile).Returns(new CrProfile { HasPremium = premium });
        auth.Setup(value => value.AuthenticateAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(loggedIn);
        using var transport = new HttpClientWrapper();
        auth.SetupGet(value => value.HttpClient).Returns(transport);
        var queue = new Mock<IQueueService>();
        var controller = new QueueController(queue.Object, Mock.Of<IHistoryService>(), Mock.Of<ILanguagePrefsService>(), new CruncharrConfig(), NullLogger<QueueController>.Instance, auth.Object);
        var response = Assert.IsType<ObjectResult>(await controller.AddToQueue(new QueueRequest { EpisodeId = "GEPISODE1" }, TestContext.Current.CancellationToken));
        Assert.Equal(403, response.StatusCode);
        queue.Verify(value => value.AddToQueue(It.IsAny<EpisodeInfo>()), Times.Never);
        var api = new Mock<ICrunchyrollApiService>();
        var series = new SeriesController(api.Object, NullLogger<SeriesController>.Instance, auth.Object);
        Assert.IsType<BadRequestObjectResult>(series.ItemSelectMultiDub(new ItemSelectMultiDubRequest()));
        var download = new DownloadService(auth.Object, api.Object);
        var result = await download.DownloadEpisodeAsync(new EpisodeInfo { Id = "GEPISODE1" }, new CruncharrConfig(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(loggedIn ? DownloadErrorType.PremiumContent : DownloadErrorType.NotAuthenticated, result.ErrorType);
        api.VerifyNoOtherCalls();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
