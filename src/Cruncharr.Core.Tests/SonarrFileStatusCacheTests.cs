using System.Net;
using System.Reflection;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

// Opening History re-read every matched series' file status from Sonarr, one after another,
// because a read was only reusable for 15 s. With 53 matched series that took minutes while
// Sonarr was busy. Ordinary reads now reuse it for 2 minutes; forced reads still fetch.
public class SonarrFileStatusCacheTests
{
    private static SonarrConfig Config() => new() { Enabled = true, Host = "sonarr.test", Port = 8989, ApiKey = "key" };

    [Fact]
    public async Task FileStatus_IsReusedForAFewMinutesButForcedReadsFetch()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":1,"seriesId":7,"seasonNumber":1,"episodeNumber":1,"hasFile":true}]""")
            };
        }));
        var sonarr = new SonarrService(new TestHttpClientFactory(client));
        var config = Config();

        Assert.Single(await sonarr.GetCurrentEpisodesAsync(7, config, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);

        // A minute later an ordinary read (opening History) still uses the cached status.
        AgeCachedEpisodeList(sonarr, TimeSpan.FromMinutes(1));
        await sonarr.GetCurrentEpisodesAsync(7, config, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);

        // Forced reads always ask Sonarr.
        await sonarr.GetCurrentEpisodesAsync(7, config, forceRefresh: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, requests);

        // After the reuse window an ordinary read fetches again.
        AgeCachedEpisodeList(sonarr, TimeSpan.FromMinutes(3));
        await sonarr.GetCurrentEpisodesAsync(7, config, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, requests);
    }

    // Moves every cached episode list's fetch time back, as if that much time had passed.
    private static void AgeCachedEpisodeList(SonarrService sonarr, TimeSpan age)
    {
        var field = typeof(SonarrService).GetField("_episodeListCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (Dictionary<string, (DateTime FetchedUtc, List<SonarrEpisode> Data)>)field.GetValue(sonarr)!;
        foreach (var key in cache.Keys.ToList())
            cache[key] = (DateTime.UtcNow - age, cache[key].Data);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
