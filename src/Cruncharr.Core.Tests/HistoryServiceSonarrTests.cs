using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Cruncharr.Core.Tests;

public class HistoryServiceSonarrTests : IDisposable
{
    private readonly string _testHistoryPath;
    private readonly Mock<ILogger<HistoryService>> _loggerMock;
    private readonly Mock<ISonarrService> _sonarrServiceMock;
    private readonly Mock<ICrunchyrollApiService> _apiServiceMock;
    private readonly CruncharrConfig _config;
    private readonly HistoryService _historyService;

    public HistoryServiceSonarrTests()
    {
        _testHistoryPath = Path.Combine(Path.GetTempPath(), $"test_history_{Guid.NewGuid()}.json");
        _loggerMock = new Mock<ILogger<HistoryService>>();
        _sonarrServiceMock = new Mock<ISonarrService>();
        _apiServiceMock = new Mock<ICrunchyrollApiService>();
        _config = new CruncharrConfig
        {
            Sonarr = new SonarrConfig
            {
                Enabled = true,
                Host = "localhost",
                Port = 8989,
                ApiKey = "test-key"
            }
        };
        _historyService = new HistoryService(
            _testHistoryPath,
            _loggerMock.Object,
            _sonarrServiceMock.Object,
            _apiServiceMock.Object,
            null,
            null,
            _config
        );
    }

    public void Dispose()
    {
        if (File.Exists(_testHistoryPath))
        {
            File.Delete(_testHistoryPath);
        }
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_DisabledConfig_DoesNothing()
    {
        _config.Sonarr.Enabled = false;

        var history = CreateTestHistory();
        await SaveTestHistory(history);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        _sonarrServiceMock.Verify(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()), Times.Never);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_NoSonarrService_DoesNothing()
    {
        var serviceWithoutSonarr = new HistoryService(
            _testHistoryPath,
            _loggerMock.Object,
            null, // No Sonarr service
            _apiServiceMock.Object,
            null,
            null,
            _config
        );

        var history = CreateTestHistory();
        await SaveTestHistory(history);

        await serviceWithoutSonarr.MatchHistorySeriesWithSonarrAsync();

        // Should not throw
        Assert.True(true);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_UnmatchedSeries_MatchesByTitle()
    {
        var history = CreateTestHistory();
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 100,
                Title = "Attack on Titan",
                CleanTitle = "attackontitan",
                TvdbId = 267440,
                TitleSlug = "attack-on-titan"
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        var result = await _historyService.GetHistorySeriesAsync();
        var matchedSeries = result.First(s => s.SeriesTitle == "Attack on Titan");

        Assert.Equal("100", matchedSeries.SonarrSeriesId);
        Assert.Equal("267440", matchedSeries.SonarrTvDbId);
        Assert.Equal("attack-on-titan", matchedSeries.SonarrSlugTitle);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_AlreadyMatched_DoesNotRematch()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 999,
                Title = "Different Series",
                CleanTitle = "different"
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        var result = await _historyService.GetHistorySeriesAsync();
        var series = result.First(s => s.SeriesTitle == "Attack on Titan");

        // Should keep original match
        Assert.Equal("100", series.SonarrSeriesId);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_UpdateAll_UpdatesExistingMatches()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].SonarrTvDbId = "267440";
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 100,
                Title = "Attack on Titan",
                CleanTitle = "attackontitan",
                TvdbId = 267441, // Changed
                TitleSlug = "attack-on-titan-final"
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync(updateAll: true);

        var result = await _historyService.GetHistorySeriesAsync();
        var series = result.First(s => s.SeriesTitle == "Attack on Titan");

        Assert.Equal("267441", series.SonarrTvDbId);
        Assert.Equal("attack-on-titan-final", series.SonarrSlugTitle);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_ArtistSeries_SkipsMatching()
    {
        var history = CreateTestHistory();
        history[0].SeriesType = SeriesType.Artist;
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 100,
                Title = "Attack on Titan",
                CleanTitle = "attackontitan"
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        var result = await _historyService.GetHistorySeriesAsync();
        var series = result.First(s => s.SeriesTitle == "Attack on Titan");

        Assert.Null(series.SonarrSeriesId);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_Success_MatchesEpisodes()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        await SaveTestHistory(history);

        var episodes = new List<SonarrEpisode>{
            new(){
                Id = 1001,
                SeriesId = 100,
                EpisodeNumber = 1,
                SeasonNumber = 1,
                Title = "To You, in 2000 Years",
                HasFile = true,
                Monitored = true,
                AbsoluteEpisodeNumber = 1,
                AirDateUtc = DateTimeOffset.UtcNow.AddDays(-30)
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];

        Assert.Equal("1001", episode.SonarrEpisodeId);
        Assert.Equal("1", episode.SonarrEpisodeNumber);
        Assert.True(episode.SonarrHasFile);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_NoSeriesId_DoesNothing()
    {
        var history = CreateTestHistory();
        await SaveTestHistory(history);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        _sonarrServiceMock.Verify(s => s.GetEpisodesAsync(It.IsAny<int>(), It.IsAny<SonarrConfig>()), Times.Never);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_InvalidSeriesId_DoesNothing()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "invalid";
        await SaveTestHistory(history);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        _sonarrServiceMock.Verify(s => s.GetEpisodesAsync(It.IsAny<int>(), It.IsAny<SonarrConfig>()), Times.Never);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_RematchAll_ClearsAndRematches()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList[0].SonarrEpisodeId = "1001";
        await SaveTestHistory(history);

        var episodes = new List<SonarrEpisode>{
            new(){
                Id = 2001,
                SeriesId = 100,
                EpisodeNumber = 1,
                SeasonNumber = 1,
                Title = "To You, in 2000 Years",
                HasFile = true,
                Monitored = true,
                AbsoluteEpisodeNumber = 1,
                AirDateUtc = DateTimeOffset.UtcNow.AddDays(-30)
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!, rematchAll: true);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];

        Assert.Equal("2001", episode.SonarrEpisodeId);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_MatchByEpisodeNumber_FallsBackToNumber()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList[0].EpisodeTitle = "Completely Different Title";
        await SaveTestHistory(history);

        var episodes = new List<SonarrEpisode>{
            new(){
                Id = 1001,
                SeriesId = 100,
                EpisodeNumber = 1,
                SeasonNumber = 1,
                Title = "To You, in 2000 Years",
                HasFile = true,
                Monitored = true,
                AbsoluteEpisodeNumber = 1,
                AirDateUtc = DateTimeOffset.UtcNow.AddDays(-30)
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];

        Assert.Equal("1001", episode.SonarrEpisodeId);
    }

    private List<HistorySeries> CreateTestHistory()
    {
        return new List<HistorySeries>{
            new(){
                SeriesId = "series-1",
                SeriesTitle = "Attack on Titan",
                SeriesType = SeriesType.Series,
                Seasons = new List<HistorySeason>{
                    new(){
                        SeasonId = "season-1",
                        SeasonNum = "1",
                        EpisodesList = new List<HistoryEpisode>{
                            new(){
                                EpisodeId = "ep-1",
                                EpisodeTitle = "To You, in 2000 Years",
                                Episode = "1",
                                EpisodeSeasonNum = "1",
                                WasDownloaded = true
                            }
                        }
                    }
                }
            }
        };
    }

    private async Task SaveTestHistory(List<HistorySeries> history)
    {
        var json = JsonSerializer.Serialize(history);
        await File.WriteAllTextAsync(_testHistoryPath, json);
    }
}