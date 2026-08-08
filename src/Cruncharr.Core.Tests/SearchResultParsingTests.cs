using System.Web;
using Cruncharr.Core.Services;
using Moq;

namespace Cruncharr.Core.Tests;

public class SearchResultParsingTests
{
    [Fact]
    public async Task EnsureAuthenticatedAsync_RefreshesAnExistingSessionBeforeCatalogRequests()
    {
        var auth = new Mock<ICrunchyrollAuthService>();
        auth.SetupGet(service => service.IsAuthenticated).Returns(true);
        auth.Setup(service => service.RefreshTokenAsync(true, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(true);
        var api = new CrunchyrollApiService(auth.Object);

        var authenticated = await api.EnsureAuthenticatedAsync(true, CancellationToken.None);

        Assert.True(authenticated);
        auth.Verify(service => service.RefreshTokenAsync(true, It.IsAny<CancellationToken>(), false), Times.Once);
        auth.Verify(service => service.AuthenticateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_AuthenticatesWhenNoSessionExists()
    {
        var auth = new Mock<ICrunchyrollAuthService>();
        auth.SetupGet(service => service.IsAuthenticated).Returns(false);
        auth.Setup(service => service.AuthenticateAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var api = new CrunchyrollApiService(auth.Object);

        var authenticated = await api.EnsureAuthenticatedAsync(true, CancellationToken.None);

        Assert.True(authenticated);
        auth.Verify(service => service.AuthenticateAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(service => service.RefreshTokenAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void BuildSearchUri_RequestsEveryUpstreamSearchCategoryWithExpandedLimit()
    {
        var uri = CrunchyrollApiService.BuildSearchUri("SPY x FAMILY CODE: White", 100, 100);
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("SPY x FAMILY CODE: White", query["q"]);
        Assert.Equal("100", query["n"]);
        Assert.Equal("100", query["start"]);
        Assert.Equal("top_results,series,movie_listing,episode,music", query["type"]);
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

    [Fact]
    public void SearchHasNextPage_UsesPerGroupCountAndIgnoresEpisodes()
    {
        var firstPage = new CrSearchResult
        {
            Data =
            [
                new CrSearchGroup
                {
                    Type = "series",
                    Count = 143,
                    Items = Enumerable.Range(0, 100)
                        .Select(index => new CrSearchItem { Id = $"SERIES{index}" })
                        .ToList()
                },
                new CrSearchGroup
                {
                    Type = "movie_listing",
                    Count = 6,
                    Items = Enumerable.Range(0, 6)
                        .Select(index => new CrSearchItem { Id = $"MOVIE{index}" })
                        .ToList()
                },
                new CrSearchGroup { Type = "episode", Count = 5_000, Items = [] }
            ]
        };

        Assert.True(CrunchyrollApiService.SearchHasNextPage(firstPage, start: 0));

        var lastPage = new CrSearchResult
        {
            Data =
            [
                new CrSearchGroup
                {
                    Type = "series",
                    Count = 143,
                    Items = Enumerable.Range(100, 43)
                        .Select(index => new CrSearchItem { Id = $"SERIES{index}" })
                        .ToList()
                }
            ]
        };

        Assert.False(CrunchyrollApiService.SearchHasNextPage(lastPage, start: 100));
    }

    [Fact]
    public void ParseSearchResults_MergesPagesAndRanksLaterExactHit()
    {
        var groups = new[]
        {
            new CrSearchGroup
            {
                Type = "series",
                Items =
                [
                    new CrSearchItem { Id = "FUZZY", Title = "A Fuzzy Family Show" },
                    new CrSearchItem { Id = "DUPLICATE", Title = "Duplicate" }
                ]
            },
            new CrSearchGroup
            {
                Type = "movie_listing",
                Items =
                [
                    new CrSearchItem { Id = "EXACT", Title = "SPY x FAMILY CODE: White" },
                    new CrSearchItem { Id = "DUPLICATE", Title = "Duplicate" }
                ]
            }
        };

        var results = CrunchyrollApiService.ParseSearchResults(groups, "SPY x FAMILY CODE: White");

        Assert.Equal("EXACT", results[0].Id);
        Assert.Single(results, result => result.Id == "DUPLICATE");
    }

    [Fact]
    public void SearchContainsExactTitle_IgnoresNonDownloadableGroupsAndCase()
    {
        var groups = new[]
        {
            new CrSearchGroup
            {
                Type = "episode",
                Items = [new CrSearchItem { Id = "EP", Title = "Missing Show" }]
            },
            new CrSearchGroup
            {
                Type = "series",
                Items = [new CrSearchItem { Id = "SERIES", Title = "MISSING SHOW" }]
            }
        };

        Assert.True(CrunchyrollApiService.SearchContainsExactTitle(groups, "Missing Show"));
        Assert.False(CrunchyrollApiService.SearchContainsExactTitle(groups[..1], "Missing Show"));
    }

    [Theory]
    [InlineData("https://www.crunchyroll.com/series/GYZJ43JMR/that-time-i-got-reincarnated-as-a-slime", "GYZJ43JMR")]
    [InlineData("https://www.crunchyroll.com/watch/G14U411V1/demons-and-strategies", "G14U411V1")]
    [InlineData("GYZJ43JMR", "GYZJ43JMR")]
    public void ExtractIdFromUrl_UsesOpaqueIdInsteadOfTrailingSlug(string input, string expected)
    {
        Assert.Equal(expected, CrunchyrollApiService.ExtractIdFromUrl(input));
    }

    [Fact]
    public void ExtractDirectSeriesId_AcceptsSeriesUrlButNotEpisodeUrl()
    {
        Assert.Equal(
            "GYZJ43JMR",
            CrunchyrollApiService.ExtractDirectSeriesId(
                "https://www.crunchyroll.com/series/GYZJ43JMR/that-time-i-got-reincarnated-as-a-slime"));
        Assert.Null(CrunchyrollApiService.ExtractDirectSeriesId(
            "https://www.crunchyroll.com/watch/G14U411V1/demons-and-strategies"));
    }
}
