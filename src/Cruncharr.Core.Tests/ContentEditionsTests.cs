using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Tests;

public class ContentEditionsTests
{
    [Theory]
    [InlineData("Example (Uncensored)", "Uncensored")]
    [InlineData("Example (Uncensored Version)", "Uncensored")]
    [InlineData("Example [Censored]", "Censored")]
    [InlineData("Example - Uncut", "Uncut")]
    [InlineData("Example (Edited Version)", "Edited")]
    [InlineData("Example (TV Broadcast Version)", "TV broadcast")]
    [InlineData("Example (Director’s Cut)", "Director's cut")]
    [InlineData("Example (Home Video)", "Home video")]
    [InlineData("Example (UNCENSORED) (English Dub)", "Uncensored")]
    public void ExplicitSeasonLabels_ArePreservedWithoutEquatingUncutWithUncensored(string title, string label)
    {
        var episode = Episode("SEASON", "Example");
        ContentEditions.Apply("SERIES", [new SeasonInfo { Id = "SEASON", Title = title }], [episode]);
        Assert.Equal(label, episode.Edition?.Label);
        Assert.Null(episode.Edition?.SourceUrl);
    }

    [Theory]
    [InlineData("Uncensored Adventures")]
    [InlineData("The Edited Life")]
    [InlineData("Broadcast Club")]
    [InlineData("Example (English Dub)")]
    [InlineData("Example (TV-MA)")]
    public void OrdinaryNamesRatingsAndDubs_AreNotEditionEvidence(string title)
    {
        var episode = Episode("SEASON", title);
        episode.Description = "An edited description of an uncensored debate.";
        ContentEditions.Apply("SERIES", [], [episode]);
        Assert.Null(episode.Edition);
    }

    [Fact]
    public void VerifiedCorrections_UseExactIdsEvenWhenOrderAndSeasonNumbersChange()
    {
        var broadcast = Episode("GRVD1PG4R", "Nobunaga teacher's young bride");
        var uncensored = Episode("G63K98G46", "Nobunaga teacher's young bride");
        broadcast.Season = "99";
        uncensored.Season = "99";
        ContentEditions.Apply("GRVD1PE0R", [], [broadcast, uncensored]);
        Assert.Equal("TV broadcast", broadcast.Edition?.Label);
        Assert.Equal("Uncensored", uncensored.Edition?.Label);
        Assert.StartsWith("https://www.crunchyroll.com/news/", uncensored.Edition!.SourceUrl);

        ContentEditions.Apply("OTHER_SERIES", [], [broadcast, uncensored]);
        Assert.Equal("Edition unknown", broadcast.Edition?.Label);
        Assert.Equal("Edition unknown", uncensored.Edition?.Label);
    }

    [Fact]
    public void CurrentExplicitLabels_OverrideHistoricalCorrections()
    {
        var episode = Episode("G63K98G46", "Example (Edited)");
        ContentEditions.Apply("GRVD1PE0R", [], [episode]);
        Assert.Equal("Edited", episode.Edition?.Label);
    }

    [Fact]
    public void AmbiguousListings_AreUnknownWithoutInventingACensoredCounterpart()
    {
        var episodes = new[] { Episode("A", "Example"), Episode("B", "Example"), Episode("C", "Other season") };
        ContentEditions.Apply("SERIES", [new SeasonInfo { Id = "A", Title = "Example (Uncut)" }], episodes);
        Assert.Equal("Uncut", episodes[0].Edition?.Label);
        Assert.Equal("Edition unknown", episodes[1].Edition?.Label);
        Assert.Null(episodes[2].Edition);
        Assert.Equal(new[] { "A", "B", "C" }, episodes.Select(episode => episode.Id));
    }

    [Fact]
    public void ConflictingLabels_AreNotSilentlyResolvedByTheirOrder()
    {
        var episode = Episode("A", "Example (Censored)");
        ContentEditions.Apply("SERIES", [new SeasonInfo { Id = "A", Title = "Example (Uncensored)" }], [episode]);
        Assert.Equal("Edition unknown", episode.Edition?.Label);
        Assert.Contains("conflicting", episode.Edition!.Explanation);
    }

    private static EpisodeDisplay Episode(string seasonId, string title) => new()
    {
        Id = seasonId,
        E = seasonId + "E1",
        Season = "1",
        SeasonTitle = title,
        Name = "First episode",
        EpisodeNum = "1"
    };
}
