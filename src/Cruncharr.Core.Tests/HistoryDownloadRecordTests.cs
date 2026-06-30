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

    // Guard (upstream CRD v1.6.14): a manual "Mark as Downloaded" (no dubs/subs supplied) must reset
    // the downloaded dub/sub tracking to the full available set, so the episode is not re-flagged as
    // a partial download.
    [Fact]
    public async Task ManualMarkDownloaded_ResetsTrackingToAvailable_NotPartial()
    {
        var episode = new EpisodeInfo
        {
            Id = "EP_MANUAL_1",
            SeriesId = "S_MANUAL",
            SeriesTitle = "Manual Mark Show",
            Title = "Ep 1",
            SeasonNumber = 1,
            SeasonId = "S_MANUAL|S1",
            EpisodeNumber = 1
        };

        await _history.UpdateWithSeasonDataAsync(new List<EpisodeInfo> { episode });

        // Episode actually only got the ja-JP dub from a real download, but en-US is also available.
        await _history.SetAsDownloadedAsync(episode.SeriesId, episode.SeasonId, episode.Id,
            new List<string> { "ja-JP" }, new List<string>());

        var hist = await _history.GetHistoryEpisodeAsync(episode.SeriesId, episode.SeasonId, episode.Id);
        Assert.NotNull(hist);
        hist!.UpdateAvailableMedia(new List<string> { "ja-JP", "en-US" }, new List<string>());
        Assert.True(hist.IsPartiallyDownloaded(new[] { "ja-JP", "en-US" }, Array.Empty<string>()));

        // Manual mark (no dubs/subs) must reset tracking to everything available => no longer partial.
        await _history.SetAsDownloadedAsync(episode.SeriesId, episode.SeasonId, episode.Id);

        var after = await _history.GetHistoryEpisodeAsync(episode.SeriesId, episode.SeasonId, episode.Id);
        Assert.NotNull(after);
        Assert.True(after!.WasDownloaded);
        Assert.False(after.IsPartiallyDownloaded(new[] { "ja-JP", "en-US" }, Array.Empty<string>()));
    }
}
