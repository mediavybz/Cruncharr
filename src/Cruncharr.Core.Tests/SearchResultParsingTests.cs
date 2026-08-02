using System.Web;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

public class SearchResultParsingTests
{
    [Fact]
    public void BuildSearchUri_RequestsSeriesAndMoviesWithExpandedLimit()
    {
        var uri = CrunchyrollApiService.BuildSearchUri("SPY x FAMILY CODE: White", 100, 100);
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("SPY x FAMILY CODE: White", query["q"]);
        Assert.Equal("100", query["n"]);
        Assert.Equal("100", query["start"]);
        Assert.Equal("series,movie_listing", query["type"]);
    }

    [Fact]
    public void ParseSearchResults_IncludesMoviesDeduplicatesAndExcludesEpisodes()
    {
        const string json = """
        {
          "total": 4,
          "data": [
            { "type": "series", "count": 2, "items": [
              { "id": "SERIES1", "title": "A Fuzzy Family Show", "description": "Series" },
              { "id": "MOVIE1", "title": "Duplicate shell", "description": "Duplicate" }
            ]},
            { "type": "movie_listing", "count": 1, "items": [
              { "id": "MOVIE1", "title": "SPY x FAMILY CODE: White", "description": "Movie" }
            ]},
            { "type": "episode", "count": 1, "items": [
              { "id": "EPISODE1", "title": "SPY x FAMILY CODE: White Episode", "description": "Episode" }
            ]}
          ]
        }
        """;

        var results = CrunchyrollApiService.ParseSearchResults(json, "SPY x FAMILY CODE: White");

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, item => item.Id == "EPISODE1");
        Assert.Single(results, item => item.Id == "MOVIE1");
        Assert.Contains(results, item => item.ContentType == "movie_listing");
    }

    [Fact]
    public void ParseSearchResults_RanksExactMovieAheadOfFuzzySeries()
    {
        const string json = """
        { "data": [
          { "type": "series", "items": [
            { "id": "SERIES1", "title": "Dragon Ball Super" }
          ]},
          { "type": "movie_listing", "items": [
            { "id": "MOVIE1", "title": "Dragon Ball Super: SUPER HERO" }
          ]}
        ]}
        """;

        var results = CrunchyrollApiService.ParseSearchResults(json, "Dragon Ball Super: SUPER HERO");

        Assert.Equal("MOVIE1", results[0].Id);
    }
}
