using System.Net;
using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Cruncharr.Core.Tests;

public class SonarrServiceTests
{
    private readonly Mock<ILogger<SonarrService>> _loggerMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly SonarrService _service;

    public SonarrServiceTests()
    {
        _loggerMock = new Mock<ILogger<SonarrService>>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        _service = new SonarrService(_httpClientFactoryMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void BuildBaseUrl_WithSsl_ReturnsHttps()
    {
        var config = new SonarrConfig
        {
            Host = "localhost",
            Port = 8989,
            UseSsl = true,
            ApiKey = "test-key"
        };

        var url = InvokeBuildBaseUrl(config);

        Assert.Equal("https://localhost:8989/api/v3", url);
    }

    [Fact]
    public void BuildBaseUrl_WithoutSsl_ReturnsHttp()
    {
        var config = new SonarrConfig
        {
            Host = "sonarr.example.com",
            Port = 80,
            UseSsl = false,
            ApiKey = "test-key"
        };

        var url = InvokeBuildBaseUrl(config);

        Assert.Equal("http://sonarr.example.com:80/api/v3", url);
    }

    [Fact]
    public void BuildBaseUrl_WithUrlBase_AppendsCorrectly()
    {
        var config = new SonarrConfig
        {
            Host = "localhost",
            Port = 8989,
            UseSsl = false,
            UrlBase = "/sonarr",
            ApiKey = "test-key"
        };

        var url = InvokeBuildBaseUrl(config);

        Assert.Equal("http://localhost:8989/sonarr/api/v3", url);
    }

    [Fact]
    public void BuildBaseUrl_WithUrlBaseTrailingSlash_HandlesCorrectly()
    {
        var config = new SonarrConfig
        {
            Host = "localhost",
            Port = 8989,
            UseSsl = false,
            UrlBase = "sonarr/",
            ApiKey = "test-key"
        };

        var url = InvokeBuildBaseUrl(config);

        Assert.Equal("http://localhost:8989/sonarr/api/v3", url);
    }

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsTrue()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains("/system/status") &&
                    req.Headers.Contains("X-Api-Key")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"version\":\"4.0\"}")
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.TestConnectionAsync(config);

        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_Failure_ReturnsFalse()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.TestConnectionAsync(config);

        Assert.False(result);
    }

    [Fact]
    public async Task GetSeriesAsync_Success_ReturnsSeriesList()
    {
        var expectedSeries = new List<SonarrSeries>{
            new(){
                Id = 1,
                Title = "Test Series",
                CleanTitle = "testseries",
                Year = 2023,
                TvdbId = 12345
            },
            new(){
                Id = 2,
                Title = "Another Series",
                CleanTitle = "anotherseries",
                Year = 2022,
                TvdbId = 67890
            }
        };

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains("/series")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(expectedSeries))
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetSeriesAsync(config);

        Assert.Equal(2, result.Count);
        Assert.Equal("Test Series", result[0].Title);
        Assert.Equal("Another Series", result[1].Title);
    }

    [Fact]
    public async Task GetSeriesAsync_Failure_ReturnsEmptyList()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetSeriesAsync(config);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSeriesByTitleAsync_ExactMatch_ReturnsSeries()
    {
        var series = new List<SonarrSeries>{
            new(){
                Id = 1,
                Title = "Attack on Titan",
                CleanTitle = "attackontitan"
            }
        };

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(series))
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetSeriesByTitleAsync("Attack on Titan", config);

        Assert.NotNull(result);
        Assert.Equal("Attack on Titan", result!.Title);
    }

    [Fact]
    public async Task GetSeriesByTitleAsync_NormalizedCleanTitle_ReturnsCanonicalSeries()
    {
        var series = new List<SonarrSeries>
        {
            new()
            {
                Id = 801,
                Title = "Canonical Display Title",
                CleanTitle = "shiboyugiplayingdeathgamestoputfoodonthetable",
                Path = "/tv/SHIBOYUGI - Playing Death Games to Put Food on the Table"
            }
        };
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(JsonResponse(JsonSerializer.Serialize(series)));
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handlerMock.Object)),
            _loggerMock.Object);

        var result = await service.GetSeriesByTitleAsync(
            "SHIBOYUGI: Playing Death Games to Put Food on the Table",
            CreateTestConfig());

        Assert.NotNull(result);
        Assert.Equal(801, result!.Id);
    }

    [Fact]
    public async Task GetSeriesByTitleAsync_NoMatch_ReturnsNull()
    {
        var series = new List<SonarrSeries>{
            new(){
                Id = 1,
                Title = "Different Series",
                CleanTitle = "different"
            }
        };

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(series))
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetSeriesByTitleAsync("Non Existent", config);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEpisodesAsync_Success_ReturnsEpisodeList()
    {
        var expectedEpisodes = new List<SonarrEpisode>{
            new(){
                Id = 101,
                SeriesId = 1,
                EpisodeNumber = 1,
                SeasonNumber = 1,
                Title = "Episode 1",
                HasFile = true,
                Monitored = true,
                AbsoluteEpisodeNumber = 1,
                AirDateUtc = DateTimeOffset.UtcNow.AddDays(-7)
            },
            new(){
                Id = 102,
                SeriesId = 1,
                EpisodeNumber = 2,
                SeasonNumber = 1,
                Title = "Episode 2",
                HasFile = false,
                Monitored = true,
                AbsoluteEpisodeNumber = 2,
                AirDateUtc = DateTimeOffset.UtcNow.AddDays(7)
            }
        };

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains("/episode?seriesId=1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(expectedEpisodes))
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetEpisodesAsync(1, config);

        Assert.Equal(2, result.Count);
        Assert.Equal("Episode 1", result[0].Title);
        Assert.True(result[0].HasFile);
        Assert.False(result[1].HasFile);
    }

    [Fact]
    public async Task GetEpisodeAsync_UsesExactSavedEpisodeRoute()
    {
        var expectedEpisode = new SonarrEpisode
        {
            Id = 27447,
            SeriesId = 91,
            EpisodeNumber = 3,
            SeasonNumber = 2,
            Title = "One Single Magic Spell",
            AbsoluteEpisodeNumber = 15
        };
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.AbsolutePath == "/api/v3/episode/27447" &&
                    req.Headers.Contains("X-Api-Key")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(expectedEpisode))
            });
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handlerMock.Object)),
            _loggerMock.Object);

        var result = await service.GetEpisodeAsync(27447, CreateTestConfig());

        Assert.NotNull(result);
        Assert.Equal(27447, result!.Id);
        Assert.Equal(2, result.SeasonNumber);
        Assert.Equal(3, result.EpisodeNumber);
        Assert.Equal(15, result.AbsoluteEpisodeNumber);
    }

    [Fact]
    public async Task GetEpisodesAsync_Failure_ReturnsEmptyList()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound
            });

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetEpisodesAsync(999, config);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEpisodesAsync_NetworkError_ReturnsEmptyList()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = new SonarrServiceWithHttpClient(_loggerMock.Object, new HttpClient(handlerMock.Object));
        var config = CreateTestConfig();

        var result = await service.GetEpisodesAsync(1, config);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSeriesAsync_ConcurrentCacheMiss_CoalescesRequest()
    {
        var handler = new StubHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return JsonResponse("[{\"id\":801,\"title\":\"Canonical Series\",\"path\":\"/tv/Canonical Series\"}]");
        });
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handler)),
            _loggerMock.Object);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.GetSeriesAsync(CreateTestConfig())));

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, series => Assert.Equal(801, Assert.Single(series).Id));
    }

    [Fact]
    public async Task GetEpisodeAsync_TransientConnectionReset_Retries()
    {
        var handler = new StubHttpMessageHandler((_, call, _) =>
        {
            if (call == 1)
            {
                throw new HttpRequestException("Connection reset by peer", new IOException("reset"));
            }

            return Task.FromResult(JsonResponse(
                "{\"id\":26363,\"seriesId\":801,\"seasonNumber\":1,\"episodeNumber\":5,\"title\":\"Canonical Episode\"}"));
        });
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handler)),
            _loggerMock.Object);

        var episode = await service.GetEpisodeAsync(26363, CreateTestConfig());

        Assert.NotNull(episode);
        Assert.Equal("Canonical Episode", episode!.Title);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetEpisodeAsync_ReusesEpisodeListCache()
    {
        var handler = new StubHttpMessageHandler((request, _, _) =>
        {
            Assert.Contains("/episode?seriesId=801", request.RequestUri!.ToString());
            return Task.FromResult(JsonResponse(
                "[{\"id\":26363,\"seriesId\":801,\"seasonNumber\":1,\"episodeNumber\":5,\"title\":\"Cached Episode\"}]"));
        });
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handler)),
            _loggerMock.Object);
        var config = CreateTestConfig();

        await service.GetEpisodesAsync(801, config);
        var episode = await service.GetEpisodeAsync(26363, config);

        Assert.NotNull(episode);
        Assert.Equal("Cached Episode", episode!.Title);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetNamingConfigAsync_MapsExistingSonarrContract()
    {
        var handler = new StubHttpMessageHandler((request, _, _) =>
        {
            Assert.EndsWith("/api/v3/config/naming", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                "{\"renameEpisodes\":true,\"replaceIllegalCharacters\":true,\"colonReplacementFormat\":4," +
                "\"standardEpisodeFormat\":\"{Series Title} - S{season:00}E{episode:00} - {Episode Title}\"," +
                "\"seriesFolderFormat\":\"{Series Title}\",\"seasonFolderFormat\":\"Season {season:00}\"," +
                "\"specialsFolderFormat\":\"Specials\"}"));
        });
        var service = new SonarrService(
            new TestHttpClientFactory(new HttpClient(handler)),
            _loggerMock.Object);

        var naming = await service.GetNamingConfigAsync(CreateTestConfig());

        Assert.NotNull(naming);
        Assert.True(naming!.RenameEpisodes);
        Assert.Equal(SonarrColonReplacementFormat.Smart, naming.ColonReplacementFormat);
        Assert.Equal("Season {season:00}", naming.SeasonFolderFormat);
        Assert.Equal(1, handler.CallCount);
    }

    private static HttpResponseMessage JsonResponse(string json) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent(json)
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            return sendAsync(request, call, cancellationToken);
        }
    }

    private static SonarrConfig CreateTestConfig()
    {
        return new SonarrConfig
        {
            Host = "localhost",
            Port = 8989,
            UseSsl = false,
            ApiKey = "test-api-key-12345"
        };
    }

    private string InvokeBuildBaseUrl(SonarrConfig config)
    {
        var method = typeof(SonarrService).GetMethod("BuildBaseUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(_service, new object[] { config })!;
    }
}

// Helper class to inject HttpClient for testing
public class SonarrServiceWithHttpClient : SonarrService
{
    private readonly HttpClient _httpClient;

    public SonarrServiceWithHttpClient(ILogger<SonarrService>? logger, HttpClient httpClient) : base(new TestHttpClientFactory(httpClient), logger)
    {
        _httpClient = httpClient;
    }

    private string BuildBaseUrl(SonarrConfig config)
    {
        var scheme = config.UseSsl ? "https" : "http";
        var baseUrl = $"{scheme}://{config.Host}:{config.Port}";
        if (!string.IsNullOrEmpty(config.UrlBase))
        {
            baseUrl = baseUrl.TrimEnd('/') + "/" + config.UrlBase.TrimStart('/');
        }
        return baseUrl + "/api/v3";
    }

    public override async Task<bool> TestConnectionAsync(SonarrConfig config)
    {
        try
        {
            var url = $"{BuildBaseUrl(config)}/system/status";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public override async Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config)
    {
        try
        {
            var url = $"{BuildBaseUrl(config)}/series";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<SonarrSeries>();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<SonarrSeries>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SonarrSeries>();
        }
        catch
        {
            return new List<SonarrSeries>();
        }
    }

    public override async Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config)
    {
        try
        {
            var url = $"{BuildBaseUrl(config)}/episode?seriesId={seriesId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<SonarrEpisode>();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<SonarrEpisode>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SonarrEpisode>();
        }
        catch
        {
            return new List<SonarrEpisode>();
        }
    }
}

public class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _httpClient;
    public TestHttpClientFactory(HttpClient httpClient) => _httpClient = httpClient;
    public HttpClient CreateClient(string name) => _httpClient;
}
