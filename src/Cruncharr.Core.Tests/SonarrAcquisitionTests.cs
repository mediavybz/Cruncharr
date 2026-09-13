using System.Net;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Tests;

public sealed class SonarrAcquisitionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cruncharr-sonarr-requests-" + Guid.NewGuid());
    private readonly SonarrServer _server = new();
    private readonly Mock<ICrunchyrollApiService> _api = new();
    private readonly CruncharrConfig _config = new();
    private readonly SonarrService _sonarr;
    private readonly HistoryService _history;
    private readonly SonarrAcquisitionService _requests;
    private readonly List<EpisodeInfo> _episodes;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public SonarrAcquisitionTests()
    {
        Directory.CreateDirectory(_directory);
        _config.Sonarr = new SonarrConfig { Enabled = true, Host = "sonarr.test", Port = 8989, ApiKey = "fixture-key" };
        _config.Download.OutputDirectory = _directory;
        _episodes = Enumerable.Range(1, 3).Select(n => new EpisodeInfo {
            Id = $"GEPISODE{n}", SeriesId = "GSERIES", SeriesTitle = "The Example", SeasonId = "GSEASON",
            SeasonTitle = "The Example", SeasonNumber = 1, EpisodeNumber = n, Episode = n.ToString(), Title = $"An Adventure with Character {n}"
        }).ToList();
        _api.Setup(a => a.ParseEpisodeByIdAsync(It.IsAny<string>(), null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, bool _, CancellationToken _) => _episodes.FirstOrDefault(e => e.Id == id));
        _api.Setup(a => a.GetEpisodesAsync("GSERIES", true, It.IsAny<CancellationToken>())).ReturnsAsync(_episodes);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_server));
        _sonarr = new SonarrService(factory.Object, api: _api.Object);
        _history = new HistoryService(Path.Combine(_directory, "history.json"), sonarrService: _sonarr, apiService: _api.Object, config: _config);
        _requests = CreateRequests();
    }

    private SonarrAcquisitionService CreateRequests() => new(_sonarr, _api.Object, _history, _config,
        statePath: Path.Combine(_directory, "requests.json"));

    [Fact]
    public async Task PremiumRegistersUnmonitoredSeriesAndNeverSearches()
    {
        var result = await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, cancellationToken: Token);
        await _requests.SynchronizeAsync(Token);

        Assert.Equal(42, result.SonarrSeriesId);
        var add = Assert.Single(_server.Writes, w => w.Path == "series" && w.Method == "POST").Body;
        Assert.False(add["monitored"]!.Value<bool>());
        Assert.Equal("none", add["addOptions"]!["monitor"]!.Value<string>());
        Assert.False(add["addOptions"]!["searchForMissingEpisodes"]!.Value<bool>());
        Assert.False(add["addOptions"]!["searchForCutoffUnmetEpisodes"]!.Value<bool>());
        Assert.DoesNotContain(_server.Writes, w => w.Path == "command");
        Assert.Equal("42", (await _history.GetHistorySeriesAsync()).Single().SonarrSeriesId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingSeriesKeepsProfilesAndTagsWhenPremiumMonitoringPolicyIsApplied(bool unmonitor)
    {
        _server.Library.Add(_server.Series());
        _config.Sonarr.UnmonitorPremiumRequests = unmonitor;
        await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, cancellationToken: Token);

        Assert.DoesNotContain(_server.Writes, w => w.Path == "series" && w.Method == "POST");
        Assert.Equal(!unmonitor, _server.Library[0]["monitored"]!.Value<bool>());
        Assert.Equal(9, _server.Library[0]["qualityProfileId"]!.Value<int>());
        Assert.Equal(7, _server.Library[0]["tags"]![0]!.Value<int>());
        Assert.DoesNotContain(_server.Writes, w => w.Path == "command");
    }

    [Fact]
    public async Task GuestSearchesOnlySelectedMissingEpisodesAndDoesNotRepeatAfterRestart()
    {
        _server.Files.Add(101);
        await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, false, cancellationToken: Token);
        await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE2" }, false, cancellationToken: Token);
        await _requests.SynchronizeAsync(Token);

        var search = Assert.Single(_server.Writes, w => w.Path == "command").Body;
        Assert.Equal("EpisodeSearch", search["name"]!.Value<string>());
        Assert.Equal(new[] { 102 }, search["episodeIds"]!.Values<int>());
        var reloaded = CreateRequests();
        await reloaded.PrepareAsync(new EpisodeInfo { Id = "GEPISODE2" }, false, cancellationToken: Token);
        await reloaded.SynchronizeAsync(Token);
        Assert.Single(_server.Writes, w => w.Path == "command");
        Assert.Empty((await reloaded.GetStatusAsync()).Single().PendingEpisodes);
        Assert.True((await _history.GetHistorySeriesAsync()).Single().Seasons.Single().EpisodesList[0].SonarrHasFile);
    }

    [Fact]
    public async Task ConcurrentEpisodesCreateOneSeries()
    {
        await Task.WhenAll(_episodes.Select(e => _requests.PrepareAsync(new EpisodeInfo { Id = e.Id }, false, cancellationToken: Token)));
        Assert.Single(_server.Writes, w => w.Path == "series" && w.Method == "POST");
        Assert.Equal(3, (await _requests.GetStatusAsync()).Single().PendingEpisodes.Count);
    }

    [Fact]
    public async Task AmbiguousEditionRequiresSelectionBeforeAnyMutation()
    {
        _server.Lookup.Add(new JObject { ["tvdbId"] = 200, ["title"] = "The Example (2006)", ["year"] = 2006 });
        var error = await Assert.ThrowsAsync<SonarrMatchRequiredException>(() =>
            _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, cancellationToken: Token));
        Assert.Equal(2, error.Candidates.Count);
        Assert.Empty(_server.Writes);
        var selected = await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, 200, Token);
        Assert.Equal(200, selected.TvdbId);
    }

    [Fact]
    public async Task LostAddResponseReconcilesByTvdbInsteadOfCreatingAnotherSeries()
    {
        _server.FailAddResponse = true;
        var result = await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, cancellationToken: Token);
        Assert.Equal(42, result.SonarrSeriesId);
        Assert.Single(_server.Library);
        Assert.Single(_server.Writes, w => w.Path == "series" && w.Method == "POST");
    }

    [Fact]
    public async Task ExistingSeriesFoundAfterStaleReadIsNotAddedAgain()
    {
        Assert.Empty(await _sonarr.GetCurrentSeriesAsync(_config.Sonarr, Token));
        _server.Library.Add(_server.Series());
        var result = await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, false, cancellationToken: Token);
        Assert.Equal(42, result.SonarrSeriesId);
        Assert.DoesNotContain(_server.Writes, w => w.Path == "series" && w.Method == "POST");
    }

    [Fact]
    public async Task PendingImportSurvivesRestartAndRefreshesActualFileStatus()
    {
        await _requests.PrepareAsync(new EpisodeInfo { Id = "GEPISODE1" }, true, cancellationToken: Token);
        _config.Sonarr.DownloadPath = "/incoming";
        await _requests.RegisterImportAsync(_episodes[0], Path.Combine(_directory, "Example", "episode.mkv"), Token);
        await _requests.SynchronizeAsync(Token);
        var command = Assert.Single(_server.Writes, w => w.Path == "command").Body;
        Assert.Equal("ManualImport", command["name"]!.Value<string>());
        Assert.Equal(42, command["files"]![0]!["seriesId"]!.Value<int>());
        Assert.Equal(new[]{101}, command["files"]![0]!["episodeIds"]!.Values<int>());
        Assert.Equal("/incoming/Example/episode.mkv", command["files"]![0]!["path"]!.Value<string>());
        Assert.Equal("Copy", command["importMode"]!.Value<string>());

        _server.Commands[0]["status"] = "completed";
        _server.Files.Add(101);
        var reloaded = CreateRequests();
        await reloaded.SynchronizeAsync(Token);
        Assert.Single(_server.Writes, w => w.Path == "command");
        var state = (await reloaded.GetStatusAsync()).Single();
        Assert.Empty(state.PendingImports);
        Assert.Equal(1, state.EpisodeFileCount);
        Assert.True((await _history.GetHistorySeriesAsync()).Single().Seasons.Single().EpisodesList[0].SonarrHasFile);
    }

    [Fact]
    public async Task FilesAlreadyInTheSeriesFolderUseARescan()
    {
        _server.Library.Add(_server.Series());
        await _sonarr.ImportFileAsync("/library/The Example/Season 01/file.mkv", 42, _config.Sonarr, Token);
        var command = Assert.Single(_server.Writes, w => w.Path == "command").Body;
        Assert.Equal("RescanSeries", command["name"]!.Value<string>());
        Assert.Equal(42, command["seriesId"]!.Value<int>());
    }

    [Fact]
    public async Task GuestAdmissionNeverTouchesCruncharrDownloadQueue()
    {
        var queue = new Mock<IQueueService>();
        var auth = Mock.Of<ICrunchyrollAuthService>(a => a.IsAuthenticated == false && a.Profile == new CrProfile());
        var controller = new QueueController(queue.Object, _history, Mock.Of<ILanguagePrefsService>(), _config,
            NullLogger<QueueController>.Instance, auth, _requests);
        var response = Assert.IsType<OkObjectResult>(await controller.AddToQueue(new QueueRequest { EpisodeId = "GEPISODE1" }, Token));
        Assert.Equal("sonarr", JObject.FromObject(response.Value!)["Destination"]!.Value<string>());
        queue.Verify(q => q.AddToQueue(It.IsAny<EpisodeInfo>()), Times.Never);
    }

    [Fact]
    public void ImportMappingRejectsPathsOutsideTheConfiguredDownloadFolder()
    {
        Assert.Throws<InvalidOperationException>(() => SonarrAcquisitionService.MapImportPath(
            Path.Combine(_directory, "..", "elsewhere.mkv"), _directory, "/incoming"));
    }

    [Fact]
    public async Task LostSearchResponseIsReconciledBeforeRetry()
    {
        _server.Library.Add(_server.Series());
        _server.FailSearchResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _sonarr.SearchEpisodesAsync(42, [102], _config.Sonarr, Token));
        await _sonarr.SearchEpisodesAsync(42, [102], _config.Sonarr, Token);
        Assert.Single(_server.Commands);
        Assert.Single(_server.Writes, w => w.Path == "command");
    }

    [Fact]
    public async Task PremiumQueueAdmissionRegistersFirstAndBlocksOnSonarrErrors()
    {
        var queue = new Mock<IQueueService>();
        queue.Setup(q => q.AddToQueue(It.IsAny<EpisodeInfo>())).Returns((EpisodeInfo e) =>
        {
            Assert.Single(_server.Library);
            Assert.False(_server.Library[0]["monitored"]!.Value<bool>());
            return new QueueAddResult(true, new QueueItem { Episode = e });
        });
        var auth = Mock.Of<ICrunchyrollAuthService>(a => a.IsAuthenticated == true && a.Profile == new CrProfile { HasPremium = true });
        var controller = new QueueController(queue.Object, _history, Mock.Of<ILanguagePrefsService>(), _config,
            NullLogger<QueueController>.Instance, auth, _requests);
        var response = Assert.IsType<OkObjectResult>(await controller.AddToQueue(new QueueRequest { EpisodeId = "GEPISODE1" }, Token));
        Assert.Equal("cruncharr", JObject.FromObject(response.Value!)["Destination"]!.Value<string>());
        Assert.Empty(_server.Commands);
        _server.FailReads = true;
        _sonarr.InvalidateCache();
        Assert.Equal(500, Assert.IsType<ObjectResult>(await controller.AddToQueue(new QueueRequest { EpisodeId = "GEPISODE2" }, Token)).StatusCode);
        queue.Verify(q => q.AddToQueue(It.IsAny<EpisodeInfo>()), Times.Once);
    }

    [Fact]
    public async Task FailedImportStaysPendingAcrossRestart()
    {
        await _requests.PrepareAsync(_episodes[0], true, cancellationToken: Token);
        await _requests.RegisterImportAsync(_episodes[0], Path.Combine(_directory, "episode.mkv"), Token);
        await _requests.SynchronizeAsync(Token);
        _server.Commands[0]["status"] = "failed";
        await CreateRequests().SynchronizeAsync(Token);
        var saved = Assert.Single(await CreateRequests().GetStatusAsync());
        Assert.Single(saved.PendingImports);
        Assert.Null(saved.PendingImports.Values.Single());
        Assert.Contains("could not import", saved.LastError);
        Assert.Equal(0, saved.EpisodeFileCount);
    }

    [Fact]
    public async Task SonarrOutagePreservesPreviouslyConfirmedFiles()
    {
        _server.Files.Add(101);
        await _requests.PrepareAsync(_episodes[0], true, cancellationToken: Token);
        await _requests.SynchronizeAsync(Token);
        _server.FailReads = true;
        _sonarr.InvalidateCache();
        await _history.RefreshSonarrFileStatusAsync(Token);
        Assert.True((await _history.GetHistorySeriesAsync()).Single().Seasons.Single().EpisodesList[0].SonarrHasFile);
    }

    [Fact]
    public async Task ImportRejectionsLeaveTheFilePendingWithoutIssuingACommand()
    {
        _server.RejectImport = true;
        await _requests.PrepareAsync(_episodes[0], true, cancellationToken: Token);
        await _requests.RegisterImportAsync(_episodes[0], Path.Combine(_directory, "unrecognized-name.mkv"), Token);
        await _requests.SynchronizeAsync(Token);
        Assert.Empty(_server.Commands);
        var state = Assert.Single(await CreateRequests().GetStatusAsync());
        Assert.Contains("No audio tracks", state.LastError);
        Assert.Single(state.PendingImports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PremiumChecksActualSonarrFilesBeforeQueueingAndRespectsReplaceExisting(bool replace)
    {
        _server.Files.Add(101);
        _config.Download.ReplaceExistingFiles = replace;
        var queue = new Mock<IQueueService>();
        queue.Setup(q => q.AddToQueue(It.IsAny<EpisodeInfo>())).Returns((EpisodeInfo e) => new QueueAddResult(true, new QueueItem { Episode = e }));
        var auth = Mock.Of<ICrunchyrollAuthService>(a => a.IsAuthenticated == true && a.Profile == new CrProfile { HasPremium = true });
        var controller = new QueueController(queue.Object, _history, Mock.Of<ILanguagePrefsService>(), _config,
            NullLogger<QueueController>.Instance, auth, _requests);
        var response = Assert.IsType<OkObjectResult>(await controller.AddToQueue(new QueueRequest { EpisodeId = "GEPISODE1" }, Token));
        Assert.Equal(replace, JObject.FromObject(response.Value!)["Added"]!.Value<bool>());
        queue.Verify(q => q.AddToQueue(It.IsAny<EpisodeInfo>()), replace ? Times.Once() : Times.Never());
        Assert.Empty(_server.Commands);
    }

    public void Dispose()
    {
        _history.Dispose();
        _server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class SonarrServer : HttpMessageHandler
    {
        public JArray Library { get; } = [];
        public JArray Lookup { get; } = [new JObject { ["tvdbId"] = 100, ["title"] = "The Example", ["year"] = 2020, ["seasons"] = new JArray(new JObject { ["seasonNumber"] = 1 }) }];
        public JArray Commands { get; } = [];
        public HashSet<int> Files { get; } = [];
        public bool FailAddResponse { get; set; }
        public bool FailSearchResponse { get; set; }
        public bool FailReads { get; set; }
        public bool RejectImport { get; set; }
        public List<(string Method, string Path, JToken Body)> Writes { get; } = [];
        public JObject Series() => new() { ["id"] = 42, ["tvdbId"] = 100, ["title"] = "The Example",
            ["path"] = "/library/The Example", ["qualityProfileId"] = 9, ["monitored"] = true, ["tags"] = new JArray(7) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v3/", "");
            var method = request.Method.Method;
            if (method == "GET" && FailReads) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var body = request.Content == null ? new JObject() : JToken.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (method != "GET") Writes.Add((method, path, body.DeepClone()));
            JToken result;
            if (path == "series" && method == "POST")
            {
                result = body.DeepClone(); result["id"] = 42; result["path"] = "/library/The Example"; Library.Add(result.DeepClone());
                if (FailAddResponse) { FailAddResponse = false; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
            }
            else if (path == "series")
            {
                foreach (var series in Library) series["statistics"] = JObject.FromObject(new { totalEpisodeCount = 3, episodeCount = 3, episodeFileCount = Files.Count });
                result = Library;
            }
            else if (path == "series/lookup") result = Lookup;
            else if (path == "series/42")
            {
                if (method == "PUT") Library[0] = body.DeepClone();
                result = Library[0];
            }
            else if (path == "qualityprofile") result = JArray.Parse("[{\"id\":9,\"name\":\"Anime\"}]");
            else if (path == "rootfolder") result = JArray.Parse("[{\"id\":1,\"path\":\"/library\"}]");
            else if (path == "episode") result = JArray.FromObject(Enumerable.Range(1, 3).Select(n => new {
                id = 100 + n, seriesId = 42, episodeNumber = n, seasonNumber = 1, absoluteEpisodeNumber = n,
                title = $"An Adventure with Character {n}", hasFile = Files.Contains(100 + n), monitored = true
            }));
            else if (path == "episode/monitor") result = new JArray();
            else if (path == "manualimport" && method == "GET") result = new JArray(new JObject {
                ["path"] = Uri.UnescapeDataString(request.RequestUri.Query.Split("folder=")[1].Split('&')[0]),
                ["quality"] = JObject.Parse("{\"quality\":{\"id\":5},\"revision\":{\"version\":1}}"),
                ["languages"] = JArray.Parse("[{\"id\":8,\"name\":\"Japanese\"}]")
            });
            else if (path == "manualimport")
            {
                result = body.DeepClone();
                result[0]!["rejections"] = RejectImport ? JArray.Parse("[{\"reason\":\"No audio tracks detected\"}]") : new JArray();
            }
            else if (path == "command" && method == "POST")
            {
                result = JObject.FromObject(new { id = 200 + Commands.Count, name = (string?)body["name"], status = "queued", queued = DateTime.UtcNow, body });
                Commands.Add(result.DeepClone());
                if (FailSearchResponse) { FailSearchResponse = false; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
            }
            else if (path == "command") result = Commands;
            else if (path.StartsWith("command/")) result = Commands.Single(c => c["id"]!.Value<int>().ToString() == path.Split('/')[1]);
            else throw new InvalidOperationException("Unexpected Sonarr request: " + method + " " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToString(Formatting.None)) };
        }
    }
}
