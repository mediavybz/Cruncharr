using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Cruncharr.Core.Tests;

public class HistoryServiceSonarrTests : IDisposable
{
    [Theory]
    [InlineData("Shuffle!", "0")]
    [InlineData("The Last Summoner: Season 1", "0")]
    [InlineData("A Returner's Magic Should Be Special", "1")]
    public async Task RegularSeasonsDoNotBecomeSpecialsFromMissingNumbersOrWordsInTheTitle(string title, string number)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        var season = history[0].Seasons[0];
        season.SeasonTitle = title; season.SeasonNum = number; season.SpecialSeason = true;
        season.EpisodesList[0].EpisodeSeasonNum = number;
        await SaveTestHistory(history);
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrEpisode { Id = 101, Title = season.EpisodesList[0].EpisodeTitle, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true }]);
        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);
        var matched = (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList[0];
        Assert.Equal("101", matched.SonarrEpisodeId);
        Assert.True(matched.SonarrHasFile);
    }

    [Fact]
    public async Task DuplicateProviderEditionsShareAnEpisodeWithoutStealingAnotherEpisodeInTheSeason()
    {
        var history = CreateTestHistory(); history[0].SonarrSeriesId = "100";
        history[0].Seasons.Add(new HistorySeason
        {
            SeasonId = "uncut",
            SeasonNum = "1",
            SeasonTitle = "Uncut",
            EpisodesList = [new HistoryEpisode { EpisodeId = "uncut-1", Episode = "1", EpisodeSeasonNum = "1", EpisodeTitle = "To You, in 2000 Years" }]
        });
        await SaveTestHistory(history);
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrEpisode { Id = 101, Title = "To You, in 2000 Years", SeasonNumber = 1, EpisodeNumber = 1, HasFile = true }]);
        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);
        var all = (await _historyService.GetHistorySeriesAsync())[0].Seasons.SelectMany(s => s.EpisodesList);
        Assert.All(all, e => Assert.Equal("101", e.SonarrEpisodeId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateEditionCanReuseATranslatedEpisodeWithAnEstablishedNumericIdentity(bool sameTitle)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList = new[] { "The Arrival", "The Journey", "A Day at the Beach" }
            .Select((title, i) => new HistoryEpisode
            {
                EpisodeId = $"ep-{i + 1}",
                Episode = (i + 1).ToString(),
                EpisodeSeasonNum = "1",
                EpisodeTitle = title
            }).ToList();
        history[0].Seasons.Add(new HistorySeason
        {
            SeasonId = "hd",
            SeasonNum = "0",
            SeasonTitle = "Special Edition HD",
            EpisodesList = [new HistoryEpisode { EpisodeId = "hd-1", Episode = "3", EpisodeSeasonNum = "0",
                EpisodeTitle = sameTitle ? "A Day at the Beach" : "An Unrelated Story" }]
        });
        await SaveTestHistory(history);
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "The Arrival", "The Journey", "Summer Vacation" }.Select((title, i) =>
                new SonarrEpisode { Id = 101 + i, Title = title, SeasonNumber = 1, EpisodeNumber = i + 1 }).ToList());

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        var seasons = (await _historyService.GetHistorySeriesAsync())[0].Seasons;
        Assert.Equal("103", seasons[0].EpisodesList[2].SonarrEpisodeId);
        Assert.Equal(sameTitle ? "103" : null, seasons[1].EpisodesList[0].SonarrEpisodeId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TranslatedTitlesCorroborateNumbersOnlyWhenIndependentEpisodesAgree(bool numbersAgree)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        string[] sourceTitles = ["Red Green Blue Yellow", "Bronze Silver Golden Road", "Completely Translated Finale"];
        history[0].Seasons[0].EpisodesList = sourceTitles.Select((title, i) => new HistoryEpisode
        {
            EpisodeId = $"ep-{i + 1}",
            Episode = (i + 1).ToString(),
            EpisodeSeasonNum = "1",
            EpisodeTitle = title
        }).ToList();
        await SaveTestHistory(history);
        string[] targetTitles = ["Red Green Blue Orange", "Bronze Silver Golden Castle", "Summer Vacation"];
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetTitles.Select((title, i) => new SonarrEpisode
            {
                Id = 101 + i,
                Title = title,
                SeasonNumber = 1,
                EpisodeNumber = numbersAgree || i == 2 ? i + 1 : 2 - i
            }).ToList());

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        var matched = (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList;
        Assert.Equal(numbersAgree ? new[] { "101", "102", "103" } : new string?[] { null, null, null },
            matched.Select(e => e.SonarrEpisodeId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorroboratedSeasonNumbersPreventSimilarTitlesFromTakingTheWrongEpisode(bool rematch)
    {
        var history = CreateTestHistory(); history[0].SonarrSeriesId = "100";
        string[] titles = ["A New Arrival", "A Quiet Dinner", "The Battle Begins", "The Battle Begin"];
        history[0].Seasons[0].EpisodesList = titles.Select((title, i) => new HistoryEpisode
        {
            EpisodeId = $"ep-{i + 1}",
            Episode = (i + 1).ToString(),
            EpisodeSeasonNum = "1",
            EpisodeTitle = title,
            SonarrEpisodeId = !rematch && i < 3 ? (101 + i).ToString() : null
        }).ToList();
        await SaveTestHistory(history);
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), rematch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(titles.Select((title, i) => new SonarrEpisode
            {
                Id = 101 + i,
                SeasonNumber = 1,
                EpisodeNumber = i + 1,
                Title = i == 3 ? "Uniforms are Uniforms" : title,
                HasFile = true
            }).ToList());
        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", rematch);
        var matched = (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList;
        Assert.Equal(new[] { "101", "102", "103", "104" }, matched.Select(e => e.SonarrEpisodeId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeasonNumberingDoesNotGiveARecapTheRegularEpisodesSlot(bool duplicateNumber)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        string[] titles = ["A New Arrival", "A Quiet Dinner", "The Battle Begins", "The Final Journey"];
        var sources = titles.Select((title, i) => new HistoryEpisode
        {
            EpisodeId = $"ep-{i + 1}",
            Episode = (i + 1).ToString(),
            EpisodeSeasonNum = "1",
            EpisodeTitle = title
        }).ToList();
        sources.Insert(0, new HistoryEpisode
        {
            EpisodeId = "recap",
            Episode = duplicateNumber ? "4" : "5",
            EpisodeSeasonNum = "1",
            EpisodeTitle = "The Story So Far",
            SpecialEpisode = false
        });
        history[0].Seasons[0].EpisodesList = sources;
        await SaveTestHistory(history);
        var targets = titles.Select((title, i) => new SonarrEpisode
        {
            Id = 101 + i,
            SeasonNumber = 1,
            EpisodeNumber = i + 1,
            Title = title
        }).ToList();
        targets.Add(new SonarrEpisode { Id = 200, SeasonNumber = 0, EpisodeNumber = 1, Title = "The Story So Far" });
        targets.Add(new SonarrEpisode { Id = 105, SeasonNumber = 1, EpisodeNumber = 5, Title = "An Unrelated Episode" });
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targets);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        var matched = (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList;
        Assert.Equal("200", matched.Single(e => e.EpisodeId == "recap").SonarrEpisodeId);
        Assert.Equal(new[] { "101", "102", "103", "104" },
            matched.Where(e => e.EpisodeId != "recap").Select(e => e.SonarrEpisodeId));
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedEpisodeRematch_PreservesSavedIdentityAndReportsFailure(bool requestFails)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList[0].SonarrEpisodeId = "1001";
        await SaveTestHistory(history);
        var setup = _sonarrServiceMock.Setup(service => service.GetCurrentEpisodesAsync(
            100, It.IsAny<SonarrConfig>(), true, It.IsAny<CancellationToken>()));
        if (requestFails) setup.ThrowsAsync(new HttpRequestException("Unavailable"));
        else setup.ReturnsAsync([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!, rematchAll: true));

        var saved = await _historyService.GetHistorySeriesAsync();
        Assert.Equal("1001", saved[0].Seasons[0].EpisodesList[0].SonarrEpisodeId);
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
    public async Task MatchHistorySeriesWithSonarrAsync_OneUnverifiableSeriesDoesNotBlockTheOthers()
    {
        var history = CreateTestHistory();
        history.Add(new HistorySeries
        {
            SeriesId = "series-2",
            SeriesTitle = "Frieren",
            SeriesType = SeriesType.Series,
            Seasons = [new HistorySeason { SeasonId = "s", SeasonNum = "1", EpisodesList = [] }]
        });
        await SaveTestHistory(history);

        _sonarrServiceMock.Setup(s => s.GetSeriesAsync(It.IsAny<SonarrConfig>())).ReturnsAsync(
        [
            new SonarrSeries { Id = 100, Title = "Attack on Titan", TvdbId = 267440, TitleSlug = "attack-on-titan" },
            new SonarrSeries { Id = 200, Title = "Frieren: Beyond Journey's End", TvdbId = 424536, TitleSlug = "frieren" }
        ]);
        // "Frieren" is only a candidate for the subtitled Sonarr entry; its verification fails
        // (no episodes listed yet). The exact "Attack on Titan" link must still be saved.
        _sonarrServiceMock.Setup(s => s.ResolveSeriesAsync("series-2", "Frieren", It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Crunchyroll lists no episodes for 'Frieren'"));

        var result = await _historyService.MatchHistorySeriesWithSonarrAsync();

        var saved = await _historyService.GetHistorySeriesAsync();
        Assert.Equal("100", saved.First(s => s.SeriesTitle == "Attack on Titan").SonarrSeriesId);
        Assert.True(string.IsNullOrEmpty(saved.First(s => s.SeriesTitle == "Frieren").SonarrSeriesId));
        Assert.Equal(1, result.Matched);
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

        _sonarrServiceMock.Verify(s => s.GetCurrentEpisodesAsync(It.IsAny<int>(), It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_InvalidSeriesId_DoesNothing()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "invalid";
        await SaveTestHistory(history);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        _sonarrServiceMock.Verify(s => s.GetCurrentEpisodesAsync(It.IsAny<int>(), It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!, rematchAll: true);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];

        Assert.Equal("2001", episode.SonarrEpisodeId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NumericFallbackDoesNotIgnoreConflictingMetadataWithoutCorroboration(bool metadataAvailable, bool storedMatch)
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList[0].EpisodeTitle = metadataAvailable ? "Completely Different Title" : null;
        history[0].Seasons[0].EpisodesList[0].SonarrEpisodeId = storedMatch ? "1001" : null;
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodes);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!);

        var result = await _historyService.GetHistorySeriesAsync();
        var episode = result[0].Seasons[0].EpisodesList[0];

        Assert.Equal(metadataAvailable ? null : "1001", episode.SonarrEpisodeId);
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
            .Setup(service => service.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            .Setup(service => service.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

    // Live 2026-10-30: Crunchyroll lists PSYCHO-PASS's films/side stories and Tanya's OVA as
    // ordinary seasons, while Sonarr files them in season 0 under prefixed titles. They stayed
    // unmatched (grey) although Sonarr had every file.
    [Fact]
    public async Task ProviderSeasonsThatSonarrFilesAsSpecialsMatchSeasonZero()
    {
        static HistorySeason Season(string id, string number, string title, params string[] episodeTitles) => new()
        {
            SeasonId = id,
            SeasonNum = number,
            SeasonTitle = title,
            EpisodesList = episodeTitles.Select((episodeTitle, i) => new HistoryEpisode
            {
                EpisodeId = $"{id}-{i + 1}",
                Episode = (i + 1).ToString(),
                EpisodeSeasonNum = number,
                EpisodeTitle = episodeTitle
            }).ToList()
        };
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons =
        [
            Season("main", "1", "PSYCHO-PASS", "Crime Coefficient", "Those Capable", "Nobody Knows Your Mask"),
            Season("sinners", "4", "PSYCHO-PASS: Sinners of the System", "Crime and Punishment", "First Guardian", "On the Other Side of Love and Hate"),
            Season("inspector", "6", "PSYCHO-PASS 3: First Inspector", "Act One (Ziggurat Capture Part 1)", "Act Two (Ziggurat Capture Part 2)"),
            Season("pasta", "3", "Operation Desert Pasta", "Saga of Tanya the Evil: Operation Desert Pasta"),
            Season("unrelated", "5", "Something Else", "A Completely Different Story")
        ];
        // Differently worded overviews used to drag the containment evidence below the threshold.
        history[0].Seasons[3].EpisodesList[0].EpisodeDescription = "Tanya's battalion is sent to the southern continent.";
        await SaveTestHistory(history);
        SonarrEpisode Episode(int id, int season, int number, string title) =>
            new() { Id = id, SeriesId = 100, SeasonNumber = season, EpisodeNumber = number, Title = title, HasFile = true };
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Episode(101, 1, 1, "Crime Coefficient"), Episode(102, 1, 2, "Those Capable"), Episode(103, 1, 3, "Nobody Knows Your Mask"),
                Episode(201, 0, 1, "Criminal Coefficient / Those Capable (Extended)"),
                Episode(212, 0, 12, "Psycho-Pass: The Movie"),
                Episode(213, 0, 13, "Psycho-Pass: Sinners of the System - Case.1 Crime and Punishment"),
                Episode(214, 0, 14, "Psycho-Pass: Sinners of the System - Case.2 First Guardian"),
                Episode(215, 0, 15, "Sinners of the System: Case.3 - On the Other Side of Love and Hate"),
                Episode(216, 0, 16, "First Inspector: Ziggurat Capture (Part 1)"),
                Episode(217, 0, 17, "First Inspector: Ziggurat Capture (Part 2)"),
                new SonarrEpisode { Id = 218, SeriesId = 100, SeasonNumber = 0, EpisodeNumber = 18, Title = "Operation Desert Pasta",
                    HasFile = true, Overview = "The Imperial Army opens a front in the desert against the Republic." }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        var ids = (await _historyService.GetHistorySeriesAsync())[0].Seasons
            .ToDictionary(season => season.SeasonId!, season => season.EpisodesList.Select(e => e.SonarrEpisodeId).ToList());
        Assert.Equal(["101", "102", "103"], ids["main"]);
        Assert.Equal(["213", "214", "215"], ids["sinners"]);
        Assert.Equal(["216", "217"], ids["inspector"]);
        Assert.Equal(["218"], ids["pasta"]);
        Assert.Equal([null], ids["unrelated"]);
    }

    // Live 2026-10-30 (Glass Fleet): a recap Sonarr files in season 0 shifts every later number by
    // one. Numbering gave "Like Flight" the slot of "Like a Lion...", leaving "Like a Lion" grey.
    [Fact]
    public async Task StrongTitleOverridesNumberingShiftedByARecapSpecial()
    {
        string[] before = ["Like Majesty", "Like an Epoch", "Like a Storm", "Like a Banner", "Like a Gale", "Like a Crown"];
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList = before.Concat(["Like an Embrace", "Like Flight", "Like a Lion", "Like a Royal Road"])
            .Select((title, i) => new HistoryEpisode { EpisodeId = $"ep-{i + 1}", Episode = (i + 1).ToString(), EpisodeSeasonNum = "1", EpisodeTitle = title })
            .ToList();
        await SaveTestHistory(history);
        var sonarr = before.Concat(["Like Flights...", "Like a Lion...", "Like a Royal Road..."])
            .Select((title, i) => new SonarrEpisode { Id = 101 + i, SeriesId = 100, SeasonNumber = 1, EpisodeNumber = i + 1, Title = title, HasFile = true })
            .Append(new SonarrEpisode { Id = 201, SeriesId = 100, SeasonNumber = 0, EpisodeNumber = 1, Title = "Like an Embrace...", HasFile = true })
            .ToList();
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sonarr);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        var ids = (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList.Select(e => e.SonarrEpisodeId).ToList();
        Assert.Equal(["101", "102", "103", "104", "105", "106", "201", "107", "108", "109"], ids);
    }

    // Live 2026-10-30 (My Hero Academia): Crunchyroll "HLB" is Sonarr S00E13 "HLB - Hero League of Baseball".
    [Fact]
    public async Task ShortTitleMatchesSonarrTitleThatSubtitlesIt()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        history[0].Seasons[0].EpisodesList = [new HistoryEpisode { EpisodeId = "hlb", Episode = "7", EpisodeSeasonNum = "5", EpisodeTitle = "HLB" }];
        await SaveTestHistory(history);
        _sonarrServiceMock.Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrEpisode { Id = 213, SeriesId = 100, SeasonNumber = 0, EpisodeNumber = 13, Title = "HLB - Hero League of Baseball", HasFile = true }]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync("series-1", true);

        Assert.Equal("213", (await _historyService.GetHistorySeriesAsync())[0].Seasons[0].EpisodesList[0].SonarrEpisodeId);
    }

    [Theory]
    [InlineData("HLB", "HLB - Hero League of Baseball", true)]
    [InlineData("HLB", "The HLB Finals", false)]
    [InlineData("Crime and Punishment", "Psycho-Pass: Sinners of the System - Case.1 Crime and Punishment", true)]
    [InlineData("Saga of Tanya the Evil: Operation Desert Pasta", "Operation Desert Pasta", true)]
    [InlineData("Part 1", "Ziggurat Capture Part 1", false)]
    [InlineData("Capture and Release", "Ziggurat Capture (Part 1)", false)]
    public void TitlePhraseContainmentNeedsAFullThreeWordPhrase(string first, string second, bool expected) =>
        Assert.Equal(expected, HistoryService.ContainsTitlePhrase(first, second));

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
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task MatchHistoryEpisodesWithSonarrAsync_SonarrS00CorrectsCollapsedProviderSpecial()
    {
        var history = CreateTestHistory();
        history[0].SonarrSeriesId = "100";
        var providerEpisode = history[0].Seasons[0].EpisodesList[0];
        providerEpisode.Episode = "1";
        providerEpisode.EpisodeSeasonNum = "2";
        providerEpisode.EpisodeTitle = "Wistoria: Wand and Sword Special Episode";
        providerEpisode.EpisodeDescription = "A look back at Season 1 and a short preview of what's to come!";
        providerEpisode.SpecialEpisode = false;
        await SaveTestHistory(history);

        _sonarrServiceMock
            .Setup(s => s.GetCurrentEpisodesAsync(100, It.IsAny<SonarrConfig>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SonarrEpisode
                {
                    Id = 2101,
                    SeriesId = 100,
                    SeasonNumber = 2,
                    EpisodeNumber = 1,
                    Title = "The Boy with No Talent",
                    Overview = "Will begins his second season at the academy."
                },
                new SonarrEpisode
                {
                    Id = 2002,
                    SeriesId = 100,
                    SeasonNumber = 0,
                    EpisodeNumber = 2,
                    Title = "Season 2 Broadcast Commemorative Special Compilation Episode",
                    Overview = providerEpisode.EpisodeDescription
                }
            ]);

        await _historyService.MatchHistoryEpisodesWithSonarrAsync(history[0].SeriesId!, rematchAll: true);

        var result = await _historyService.GetHistorySeriesAsync();
        var matched = result[0].Seasons[0].EpisodesList[0];
        Assert.Equal("2002", matched.SonarrEpisodeId);
        Assert.Equal("S00E02", matched.SonarrSeasonEpisodeText);
        Assert.True(matched.SpecialEpisode);
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
