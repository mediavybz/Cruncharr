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
}
