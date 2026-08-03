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
    public async Task MatchHistorySeriesWithSonarrAsync_MatchesViaAlternateTitle()
    {
        // CR uses the English title; Sonarr's primary title is the romaji. The match must
        // succeed via the alternate title (the primary title is too dissimilar to clear 0.8).
        var history = CreateTestHistory();
        history[0].SeriesTitle = "Frieren: Beyond Journey's End";
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 200,
                Title = "Sousou no Frieren",
                CleanTitle = "sousounofrieren",
                TvdbId = 424536,
                TitleSlug = "frieren",
                AlternateTitles = new List<SonarrAlternateTitle>{
                    new(){ Title = "Frieren: Beyond Journey's End" }
                }
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        var result = await _historyService.GetHistorySeriesAsync();
        var matched = result.First();

        Assert.Equal("200", matched.SonarrSeriesId);
        Assert.Equal("424536", matched.SonarrTvDbId);
        Assert.Equal("frieren", matched.SonarrSlugTitle);
    }

    [Fact]
    public async Task MatchHistorySeriesWithSonarrAsync_NoAlternateTitle_DoesNotMatchDissimilarPrimary()
    {
        // Negative control: same data WITHOUT the alternate title must NOT match (proves the
        // alternate title is what enables the match, not a loose threshold).
        var history = CreateTestHistory();
        history[0].SeriesTitle = "Frieren: Beyond Journey's End";
        await SaveTestHistory(history);

        var sonarrSeries = new List<SonarrSeries>{
            new(){
                Id = 200,
                Title = "Sousou no Frieren",
                CleanTitle = "sousounofrieren",
                TvdbId = 424536,
                TitleSlug = "frieren"
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>()))
            .ReturnsAsync(sonarrSeries);

        await _historyService.MatchHistorySeriesWithSonarrAsync();

        var result = await _historyService.GetHistorySeriesAsync();
        Assert.Null(result.First().SonarrSeriesId);
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

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_DecimalEpisodeUsesSonarrSpecialIdentity()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList[0].Episode = "5.5";
        history[0].Seasons[0].EpisodesList[0].EpisodeTitle = "The Bonus Adventure";
        history[0].Seasons[0].EpisodesList[0].SpecialEpisode = true;
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(service => service.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 1005,
                    SeriesId = 100,
                    SeasonNumber = 1,
                    EpisodeNumber = 5,
                    Title = "The Bonus Adventure"
                },
                new SonarrEpisode
                {
                    Id = 2007,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 7,
                    Title = "The Bonus Adventure"
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];
        Assert.Equal("5.5", episode.Episode);
        Assert.Equal("2007", episode.SonarrEpisodeId);
        Assert.Equal("S00E07", episode.SonarrSeasonEpisodeText);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_RevalidatesStaleNumberBasedIdentity()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        var historyEpisode = history[0].Seasons[0].EpisodesList[0];
        historyEpisode.Episode = "25";
        historyEpisode.EpisodeTitle = "The Inserted Chronicle";
        historyEpisode.SonarrEpisodeId = "1025";
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(service => service.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 1025,
                    SeriesId = 100,
                    SeasonNumber = 1,
                    EpisodeNumber = 25,
                    Title = "The Next Regular Chapter"
                },
                new SonarrEpisode
                {
                    Id = 2008,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 8,
                    Title = "The Inserted Chronicle"
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var rematched = result[0].Seasons[0].EpisodesList[0];
        Assert.Equal("2008", rematched.SonarrEpisodeId);
        Assert.Equal("S00E08", rematched.SonarrSeasonEpisodeText);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_SpecialsAlignToOrderedTvdbSpecials()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons =
        [
            new HistorySeason
            {
                SeasonId = "cr-specials",
                SeasonTitle = "Specials",
                SeasonNum = "4",
                EpisodesList =
                [
                    new HistoryEpisode
                    {
                        EpisodeId = "special-1",
                        EpisodeTitle = "No Coincidences in This Summer Break (Part 1)",
                        EpisodeDescription = "Futaro decides to take the summer off from tutoring. Nino acts extra cold to him for some reason.",
                        Episode = "1",
                        EpisodeSeasonNum = "4"
                    },
                    new HistoryEpisode
                    {
                        EpisodeId = "special-2",
                        EpisodeTitle = "No Coincidences in This Summer Break (Part 2)",
                        EpisodeDescription = "Futaro invites the sisters to the pool. Nino and Miku try to get closer to him.",
                        Episode = "2",
                        EpisodeSeasonNum = "4"
                    },
                    new HistoryEpisode
                    {
                        EpisodeId = "special-3",
                        EpisodeTitle = "Operation Quintuplets (Part 1)",
                        EpisodeDescription = "Futaro and the sisters head to Hawaii.",
                        Episode = "3",
                        EpisodeSeasonNum = "4"
                    },
                    new HistoryEpisode
                    {
                        EpisodeId = "special-4",
                        EpisodeTitle = "Operation Quintuplets (Part 2)",
                        EpisodeDescription = "The quintuplets help prepare for a date in Hawaii.",
                        Episode = "4",
                        EpisodeSeasonNum = "4"
                    }
                ]
            }
        ];
        await SaveTestHistory(history);

        var episodes = new List<SonarrEpisode>
        {
            new()
            {
                Id = 2001,
                SeriesId = 100,
                SeasonNumber = 0,
                EpisodeNumber = 1,
                AbsoluteEpisodeNumber = 25,
                Title = "Movie",
                Overview = "The feature film concludes the school festival story."
            },
            new()
            {
                Id = 2002,
                SeriesId = 100,
                SeasonNumber = 0,
                EpisodeNumber = 2,
                AbsoluteEpisodeNumber = 26,
                Title = "五等分の花嫁∽ 偶然のない夏休み 前編",
                Overview = "Fuutarou takes a break from tutoring during summer vacation to focus on entrance exams."
            },
            new()
            {
                Id = 2003,
                SeriesId = 100,
                SeasonNumber = 0,
                EpisodeNumber = 3,
                AbsoluteEpisodeNumber = 27,
                Title = "五等分の花嫁∽ 偶然のない夏休み 後編",
                Overview = "The quintuplets are invited to the swimming pool by Fuutarou. Nino and Miku try to get close to him."
            },
            new()
            {
                Id = 2004,
                SeriesId = 100,
                SeasonNumber = 0,
                EpisodeNumber = 4,
                AbsoluteEpisodeNumber = 29,
                Title = "五等分の花嫁* Part 1",
                Overview = "Futaro and the quintuplets plan a Hawaii trip."
            },
            new()
            {
                Id = 2005,
                SeriesId = 100,
                SeasonNumber = 0,
                EpisodeNumber = 5,
                AbsoluteEpisodeNumber = 30,
                Title = "五等分の花嫁* Part 2",
                Overview = "The quintuplets continue their Hawaii trip."
            }
        };

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var matched = result[0].Seasons[0].EpisodesList;

        Assert.Equal(["2002", "2003", "2004", "2005"], matched.Select(episode => episode.SonarrEpisodeId));
        Assert.All(matched, episode => Assert.Equal("0", episode.SonarrSeasonNumber));
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_SpecialNeverUsesUnrelatedAbsoluteNumber()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].SeasonTitle = "Specials";
        history[0].Seasons[0].SeasonNum = "4";
        history[0].Seasons[0].EpisodesList[0].EpisodeTitle = "Brand New OVA";
        history[0].Seasons[0].EpisodesList[0].EpisodeSeasonNum = "4";
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 1001,
                    SeriesId = 100,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    AbsoluteEpisodeNumber = 1,
                    Title = "Unrelated Pilot"
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        Assert.Null(result[0].Seasons[0].EpisodesList[0].SonarrEpisodeId);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_StaleSpecialRegularIdentityIsCleared()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        var special = history[0].Seasons[0].EpisodesList[0];
        special.Episode = "13.5";
        special.SpecialEpisode = true;
        special.SonarrEpisodeId = "1013";
        special.EpisodeTitle = "Since That Day";
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 1013,
                    SeriesId = 100,
                    SeasonNumber = 1,
                    EpisodeNumber = 13,
                    Title = "Primal Desire"
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        Assert.Null(result[0].Seasons[0].EpisodesList[0].SonarrEpisodeId);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_InsertedRegularSeasonSpecialsAlignToSonarrS00()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].SeasonTitle = "Season 1";
        history[0].Seasons[0].EpisodesList =
        [
            new HistoryEpisode
            {
                EpisodeId = "inserted-24-5",
                Episode = "24.5",
                EpisodeSeasonNum = "1",
                EpisodeTitle = "Episode 24.5",
                EpisodeDescription = "alpha beta gamma delta",
                SpecialEpisode = true
            },
            new HistoryEpisode
            {
                EpisodeId = "inserted-48-5",
                Episode = "48.5",
                EpisodeSeasonNum = "1",
                EpisodeTitle = "Episode 48.5",
                EpisodeDescription = "red blue green yellow",
                SpecialEpisode = true
            }
        ];
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(s => s.GetEpisodesAsync(100, It.IsAny<SonarrConfig>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 2001,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 1,
                    Title = "Movie",
                    Overview = "unrelated feature film"
                },
                new SonarrEpisode
                {
                    Id = 2002,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 2,
                    Title = "Episode 7",
                    Overview = "alpha beta epsilon zeta"
                },
                new SonarrEpisode
                {
                    Id = 2003,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 3,
                    Title = "Episode 13",
                    Overview = "red blue orange purple"
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var matched = result[0].Seasons[0].EpisodesList;
        Assert.Equal(["2002", "2003"], matched.Select(episode => episode.SonarrEpisodeId));
        Assert.All(matched, episode => Assert.Equal("0", episode.SonarrSeasonNumber));
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
