using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Moq;

namespace Cruncharr.Core.Tests;

// Audit F01: a failed season fetch during a refresh used to leave the series' episodes marked
// unavailable, so cleanup removed them (and the whole series) and saved that state.
public class HistoryRefreshSafetyTests : IDisposable
{
    private const string SeriesId = "G_SAFE_SERIES";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"test_histsafe_{Guid.NewGuid()}.json");
    private readonly Mock<ICrunchyrollApiService> _api = new();

    public HistoryRefreshSafetyTests()
    {
        _api.Setup(x => x.ParseSeriesByIdAsync(SeriesId, It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season("S1", 1), Season("S2", 2)]);
        SeasonReturns("S1", Episode("E1", "S1", 1));
        SeasonReturns("S2", Episode("E2", "S2", 2));
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + "*"))
            File.Delete(file);
    }

    [Fact]
    public async Task FailedEpisodeFetch_KeepsTheSeriesAndReportsFailure()
    {
        using (var history = await SeededHistory())
        {
            _api.Setup(x => x.GetSeasonDataByIdAsync("S2", It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("outage"));

            Assert.False(await history.CrUpdateSeriesAsync(SeriesId, ""));
            Assert.Equal(["E1", "E2"], await EpisodeIds(history));
        }

        using var reloaded = new HistoryService(_path, null, null, _api.Object, null, new CruncharrConfig());
        Assert.Equal(["E1", "E2"], await EpisodeIds(reloaded));
    }

    [Fact]
    public async Task FailedSeasonList_LeavesAvailabilityUntouched()
    {
        using var history = await SeededHistory();
        _api.Setup(x => x.ParseSeriesByIdAsync(SeriesId, It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("outage"));

        Assert.False(await history.CrUpdateSeriesAsync(SeriesId, ""));

        var episodes = (await history.GetHistorySeriesAsync()).Single().Seasons.SelectMany(s => s.EpisodesList).ToList();
        Assert.All(episodes, e => Assert.True(e.IsEpisodeAvailableOnStreamingService));
    }

    [Fact]
    public async Task PartialRefresh_RemovesOnlyWithinTheFetchedSeason()
    {
        using var history = await SeededHistory();
        // S1 now carries E3 instead of E1 (E1 genuinely removed); S2 cannot be fetched.
        SeasonReturns("S1", Episode("E3", "S1", 1));
        _api.Setup(x => x.GetSeasonDataByIdAsync("S2", It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Assert.False(await history.CrUpdateSeriesAsync(SeriesId, ""));
        Assert.Equal(["E2", "E3"], await EpisodeIds(history));
    }

    [Fact]
    public async Task CompleteRefresh_StillRemovesEpisodesCrunchyrollDropped()
    {
        using var history = await SeededHistory();
        SeasonReturns("S1", Episode("E3", "S1", 1));

        Assert.True(await history.CrUpdateSeriesAsync(SeriesId, ""));
        Assert.Equal(["E2", "E3"], await EpisodeIds(history));
    }

    private async Task<HistoryService> SeededHistory()
    {
        var history = new HistoryService(_path, null, null, _api.Object, null, new CruncharrConfig());
        Assert.True(await history.CrUpdateSeriesAsync(SeriesId, ""));
        Assert.Equal(["E1", "E2"], await EpisodeIds(history));
        return history;
    }

    private void SeasonReturns(string seasonId, EpisodeInfo episode) =>
        _api.Setup(x => x.GetSeasonDataByIdAsync(seasonId, It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([episode]);

    private static async Task<List<string?>> EpisodeIds(HistoryService history) =>
        (await history.GetHistorySeriesAsync())
            .Where(s => s.SeriesId == SeriesId)
            .SelectMany(s => s.Seasons).SelectMany(s => s.EpisodesList)
            .Select(e => e.EpisodeId).Order(StringComparer.Ordinal).ToList();

    private static SeasonInfo Season(string id, int number) => new() { Id = id, Title = $"Season {number}", SeasonNumber = number };

    private static EpisodeInfo Episode(string id, string seasonId, int seasonNumber) => new()
    {
        Id = id,
        SeriesId = SeriesId,
        SeriesTitle = "Safe Show",
        SeasonId = seasonId,
        SeasonTitle = $"Season {seasonNumber}",
        SeasonNumber = seasonNumber,
        EpisodeNumber = 1,
        Title = id
    };
}
