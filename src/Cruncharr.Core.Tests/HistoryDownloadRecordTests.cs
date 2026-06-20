using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// Reproduces the "downloads never show in History" bug: the download flow wrote only the flat
// history and never populated the RICH history the UI reads, so SetAsDownloadedAsync had nothing
// to mark ("Couldn't update download history") and the History page stayed empty - even though the
// download completed. The fix populates rich history on completion with a non-empty series id
// (CR id, else the series title) before marking downloaded.
public class HistoryDownloadRecordTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"test_histrec_{Guid.NewGuid()}.json");
    private readonly HistoryService _history;

    public HistoryDownloadRecordTests()
    {
        _history = new HistoryService(_path, null, null, null, null, null, new CruncharrConfig());
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task CompletedDownload_NoSeriesId_AppearsInRichHistoryMarkedDownloaded()
    {
        var episode = new EpisodeInfo
        {
            Id = "GRW4MEX3Y",
            SeriesTitle = "Fairy Tail",
            Title = "The Lamia Scale Thanksgiving Festival",
            SeasonNumber = 3,
            EpisodeNumber = 278
            // SeriesId / Guid / SeasonId intentionally empty = the failure scenario.
        };

        // Mirror DownloadService completion: choose a non-empty series id, ensure a season id,
        // populate rich history, then mark downloaded.
        var richSeriesId = !string.IsNullOrWhiteSpace(episode.SeriesId) ? episode.SeriesId!
            : !string.IsNullOrWhiteSpace(episode.Guid) ? episode.Guid : episode.SeriesTitle;
        episode.SeriesId = richSeriesId;
        if (string.IsNullOrWhiteSpace(episode.SeasonId)) episode.SeasonId = $"{richSeriesId}|S{episode.SeasonNumber}";

        await _history.UpdateWithSeasonDataAsync(new List<EpisodeInfo> { episode });
        await _history.SetAsDownloadedAsync(richSeriesId, episode.SeasonId, episode.Id,
            new List<string> { "ja-JP" }, new List<string>());

        var series = await _history.GetHistorySeriesAsync();
        var s = series.FirstOrDefault(x => x.SeriesTitle == "Fairy Tail");
        Assert.NotNull(s);
        var ep = s!.Seasons.SelectMany(se => se.EpisodesList).FirstOrDefault(e => e.EpisodeId == "GRW4MEX3Y");
        Assert.NotNull(ep);
        Assert.True(ep!.WasDownloaded);
    }
}
