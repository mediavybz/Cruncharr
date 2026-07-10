using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// Regression guard: CR's /content/v2/cms/series/{id} returns the series wrapped in a data ARRAY
// (upstream CrSeriesBase.Data is SeriesBaseItem[], consumed via Data.First()). The web port
// deserialized it into a single-object Data, so Newtonsoft threw on every live response,
// SeriesByIdAsync always returned null, and History cover art was never repaired — series rows
// kept the download-time episode screenshot instead of poster_tall ("screenshot instead of
// cover art" on the History page).
public class SeriesBaseParsingTests
{
    // Live-shaped payload: data is an array, images hold variant lists ordered small -> large.
    private const string SeriesJson = @"{
        ""total"": 1,
        ""data"": [
            {
                ""id"": ""GSERIES1"",
                ""title"": ""Test Series"",
                ""description"": ""A test series."",
                ""images"": {
                    ""poster_tall"": [[
                        { ""height"": 180, ""width"": 120, ""type"": ""poster_tall"", ""source"": ""https://img.cr/tall-120.jpg"" },
                        { ""height"": 720, ""width"": 480, ""type"": ""poster_tall"", ""source"": ""https://img.cr/tall-480.jpg"" },
                        { ""height"": 1080, ""width"": 720, ""type"": ""poster_tall"", ""source"": ""https://img.cr/tall-720.jpg"" }
                    ]],
                    ""poster_wide"": [[
                        { ""height"": 270, ""width"": 480, ""type"": ""poster_wide"", ""source"": ""https://img.cr/wide-480.jpg"" },
                        { ""height"": 720, ""width"": 1280, ""type"": ""poster_wide"", ""source"": ""https://img.cr/wide-1280.jpg"" }
                    ]]
                }
            }
        ],
        ""meta"": {}
    }";

    [Fact]
    public void SeriesResponse_dataArray_parsesAndYieldsPosterTallCover()
    {
        var series = CrunchyrollApiService.ParseSeriesBaseResponse(SeriesJson);

        Assert.NotNull(series);
        Assert.Equal("GSERIES1", series!.Id);
        Assert.Equal("Test Series", series.Title);
        // The series cover MUST come from poster_tall — this is what History stores in
        // ThumbnailImageUrl. An episode screenshot must never end up here.
        Assert.Equal("https://img.cr/tall-480.jpg", series.CoverArtUrl);
        Assert.Equal("https://img.cr/wide-480.jpg", series.ThumbnailUrl);
    }

    [Fact]
    public void SeriesResponse_emptyData_returnsNull()
    {
        var series = CrunchyrollApiService.ParseSeriesBaseResponse(@"{ ""total"": 0, ""data"": [], ""meta"": {} }");
        Assert.Null(series);
    }
}
