using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// Guard for the History grouping bug: downloaded episodes of one show must group under a single
// series, not appear as individual "Episode N" series. The root cause was resolving the grouping
// id from the PER-EPISODE Guid before the (stable) series title, so every episode got a unique id.
public class HistoryGroupingTests
{
    [Fact]
    public void ResolveRichSeriesId_prefersRealSeriesId()
    {
        var ep = new EpisodeInfo { SeriesId = "GR123", SeriesTitle = "Demon School", Guid = "EPGUID1" };
        Assert.Equal("GR123", DownloadService.ResolveRichSeriesId(ep));
    }

    [Fact]
    public void ResolveRichSeriesId_fallsBackToSeriesTitle_notPerEpisodeGuid()
    {
        // No series id, but a title -> must use the title so episodes of one show group together.
        var ep1 = new EpisodeInfo { SeriesId = "", SeriesTitle = "Demon School", Guid = "EPGUID1" };
        var ep2 = new EpisodeInfo { SeriesId = "", SeriesTitle = "Demon School", Guid = "EPGUID2" };

        var id1 = DownloadService.ResolveRichSeriesId(ep1);
        var id2 = DownloadService.ResolveRichSeriesId(ep2);

        Assert.Equal("Demon School", id1);
        Assert.Equal(id1, id2); // same series -> same grouping id (regression guard)
    }

    [Fact]
    public void ResolveRichSeriesId_usesGuidOnlyAsLastResort()
    {
        var ep = new EpisodeInfo { SeriesId = "", SeriesTitle = "", Guid = "EPGUID1" };
        Assert.Equal("EPGUID1", DownloadService.ResolveRichSeriesId(ep));
    }

    // Guard the migration dedup: a downloaded episode left in a legacy fallback season must collapse
    // into the same episode in the real (available) season, keeping its downloaded state.
    [Fact]
    public void DeduplicateEpisodesAcrossSeasons_mergesDownloadStateIntoRealSeason()
    {
        var series = new HistorySeries
        {
            SeriesId = "GR123",
            Seasons = new List<HistorySeason>
            {
                // Legacy fallback season: episode downloaded, marked unavailable.
                new HistorySeason
                {
                    SeasonId = "Fairy Tail|S3",
                    EpisodesList = new List<HistoryEpisode>
                    {
                        new HistoryEpisode { EpisodeId = "EP278", Episode = "278", WasDownloaded = true,
                            IsEpisodeAvailableOnStreamingService = false,
                            DownloadedDubLang = new List<string> { "ja-JP" } }
                    }
                },
                // Real populated season: same episode, not downloaded, available.
                new HistorySeason
                {
                    SeasonId = "GRSEASONREAL",
                    EpisodesList = new List<HistoryEpisode>
                    {
                        new HistoryEpisode { EpisodeId = "EP278", Episode = "278", WasDownloaded = false,
                            IsEpisodeAvailableOnStreamingService = true }
                    }
                }
            }
        };

        HistoryService.DeduplicateEpisodesAcrossSeasons(series);

        // One season left (the real one), one episode, downloaded state carried over.
        var allEps = series.Seasons.SelectMany(s => s.EpisodesList).ToList();
        Assert.Single(allEps);
        Assert.Equal("GRSEASONREAL", series.Seasons.Single().SeasonId);
        Assert.True(allEps[0].WasDownloaded);
        Assert.Contains("ja-JP", allEps[0].DownloadedDubLang);
    }
}
