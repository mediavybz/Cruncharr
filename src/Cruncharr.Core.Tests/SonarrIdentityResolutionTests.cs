using System.Net;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Moq;

namespace Cruncharr.Core.Tests;

public class SonarrIdentityResolutionTests
{
    private sealed class EditionsHandler(bool sameEpisodes = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/episode")
                ? (sameEpisodes || request.RequestUri.Query.Contains("seriesId=2")
                    ? "[{\"id\":1,\"title\":\"Bewilderment\"},{\"id\":2,\"title\":\"Defiance\"}]"
                    : "[{\"id\":3,\"title\":\"Parallax\"},{\"id\":4,\"title\":\"Conundrum\"}]")
                : "[{\"id\":1,\"tvdbId\":79072,\"title\":\"Witchblade\"},{\"id\":2,\"tvdbId\":79473,\"title\":\"Witchblade (2006)\"}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json)});
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, null)]
    public async Task EpisodeEvidenceResolvesYearQualifiedEditionsAndRejectsAmbiguousEvidence(bool sameEpisodes, int? expected)
    {
        var api=new Mock<ICrunchyrollApiService>();
        api.Setup(a=>a.GetEpisodesAsync("CR",true,It.IsAny<CancellationToken>())).ReturnsAsync(
            [new EpisodeInfo {Title="Bewilderment"},new EpisodeInfo {Title="Defiance"}]);
        var service=new SonarrService(new TestHttpClientFactory(new HttpClient(new EditionsHandler(sameEpisodes))),api:api.Object);
        var result=await service.ResolveSeriesAsync("CR","Witchblade",new SonarrConfig {Host="sonarr.test",Port=8989},TestContext.Current.CancellationToken);
        Assert.Equal(expected,result?.Id);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int LookupCalls;
        public bool RejectEpisodes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var lookup = request.RequestUri!.AbsolutePath.EndsWith("/lookup");
            if (lookup) LookupCalls++;
            return Task.FromResult(new HttpResponseMessage(request.RequestUri.AbsolutePath.EndsWith("/episode") && RejectEpisodes ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/episode")
                    ? "[{\"id\":1,\"title\":\"A New Friend\"},{\"id\":2,\"title\":\"The Journey\"}]"
                    : "[{\"id\":312,\"tvdbId\":251908,\"title\":\"Haganai: I Don't Have Many Friends\"}]")
            });
        }
    }

    [Fact]
    public async Task VerifiedIdentityCoalescesLookupAndEpisodeReadsAcrossConsumers()
    {
        var handler = new Handler();
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetEpisodesAsync("CR", true, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new EpisodeInfo { Title = "A New Friend" }, new EpisodeInfo { Title = "The Journey" }]);
        var service = new SonarrService(new TestHttpClientFactory(new HttpClient(handler)), api: api.Object);
        var config = new SonarrConfig { Host = "sonarr.test", Port = 8989, ApiKey = "test-key" };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.ResolveSeriesAsync("CR", "Haganai", config, TestContext.Current.CancellationToken)));
        Assert.All(results, r => Assert.Equal(312, r!.Id));
        Assert.Equal(1, handler.LookupCalls);
        api.Verify(a => a.GetEpisodesAsync("CR", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EpisodeFailurePropagatesAndCanBeRetriedWithoutRestart()
    {
        var handler = new Handler { RejectEpisodes = true };
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetEpisodesAsync("CR", true, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new EpisodeInfo { Title = "A New Friend" }, new EpisodeInfo { Title = "The Journey" }]);
        var service = new SonarrService(new TestHttpClientFactory(new HttpClient(handler)), api: api.Object);
        var config = new SonarrConfig { Host = "sonarr.test", Port = 8989, ApiKey = "test-key" };
        await Assert.ThrowsAsync<HttpRequestException>(() => service.ResolveSeriesAsync("CR", "Haganai", config, TestContext.Current.CancellationToken));
        handler.RejectEpisodes = false;
        Assert.Equal(312, (await service.ResolveSeriesAsync("CR", "Haganai", config, TestContext.Current.CancellationToken))!.Id);
        Assert.Equal(1, handler.LookupCalls);
    }

    [Fact]
    public async Task TitleLookupAloneCannotAssignAnUnrelatedEpisodeList()
    {
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetEpisodesAsync("CR", true, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new EpisodeInfo { Title = "Orchestra Concert" }]);
        var service = new SonarrService(new TestHttpClientFactory(new HttpClient(new Handler())), api: api.Object);
        Assert.Null(await service.ResolveSeriesAsync("CR", "Haganai", new SonarrConfig { Host = "sonarr.test", Port = 8989 }, TestContext.Current.CancellationToken));
    }
}
