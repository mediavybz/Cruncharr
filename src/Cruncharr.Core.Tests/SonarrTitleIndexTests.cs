using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

public class SonarrTitleIndexTests
{
    [Fact]
    public void ArcLabelsAndCompoundChapterNamesCanBeVerifiedWithoutHardcodedSeriesIds()
    {
        var series=new SonarrSeries {Id=1,Title="Monogatari"};
        Assert.Single(new SonarrTitleIndex([series]).Candidates("Tsukimonogatari"));
        Assert.Single(new SonarrTitleIndex([series]).Candidates("Nekomonogatari Black"));
        Assert.True(SonarrTitleIndex.EpisodesConfirmIdentity(["Distant Memories","Haruka's Heart"],
            ["Migiwa Kazuha Arc I: Distant Memories","Migiwa Kazuha Arc IV: Haruka's Heart"]));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(["Distant Memories"],
            ["First Arc I: Distant Memories","Second Arc I: Distant Memories"]));
    }

    [Fact]
    public void TranslatedEpisodeTitlesNeedAnExactAnchorAndMultipleDistinctSupportingEpisodes()
    {
        string[] provider = ["My Favorite Animal is Pegasus", "Is the busty cream girl here yet?", "Please, Always Be a Fan, Okay?"];
        string[] sonarr = ["The Animal I Like is the Pegasus", "No Big-Busted Cream Girl Yet?!", "Please, Always Be a Fan, Okay?"];
        Assert.True(SonarrTitleIndex.EpisodesConfirmIdentity(provider, sonarr));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(provider.Take(2), sonarr));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity([provider[0], provider[0], provider[2]], sonarr));
    }

    [Fact]
    public void EpisodeConfirmationRejectsGenericOrRepeatedTitlesAndUnrelatedContent()
    {
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(["Episode 1", "Episode 2"], ["Episode 1", "Episode 2"]));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(["The Beginning", "The Beginning"], ["The Beginning"]));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(["Orchestra Concert"], ["The Beginning", "The Journey"]));
        Assert.True(SonarrTitleIndex.EpisodesConfirmIdentity(["A New Friend!", "The Journey"], ["A new friend", "The Journey", "Other"]));
    }

    [Theory]
    [InlineData("Haganai", "Haganai: I Don't Have Many Friends")]
    [InlineData("We, Without Wings - under the innocent sky", "We Without Wings")]
    [InlineData("Sankarea", "Sankarea: Undying Love")]
    [InlineData("Witchblade", "Witchblade (2006)")]
    [InlineData("WorldEnd: What do you do at the end of the world? Are you busy? Will you save us", "WorldEnd: What are you doing at the end of the world? Are you busy? Will you save us?")]
    public void SubtitlesRequireLookupConfirmation(string providerTitle, string sonarrTitle)
    {
        var owned = new SonarrSeries { Id = 7, TvdbId = 100, Title = sonarrTitle };
        var index = new SonarrTitleIndex([owned]);
        Assert.Null(index.FindExact(providerTitle));
        Assert.Single(index.Candidates(providerTitle));
        Assert.Null(SonarrTitleIndex.ResolveLookup(providerTitle, [owned], []));
        Assert.Same(owned, SonarrTitleIndex.ResolveLookup(providerTitle, [owned],
            [new SonarrSeries { TvdbId = 100, Title = sonarrTitle }]));
    }

    [Fact]
    public void SeasonAliasCanLinkMultipleCrunchyrollListingsToOneSonarrSeries()
    {
        var owned = new SonarrSeries { Id = 49, TvdbId = 264053, Title = "Senran Kagura",
            AlternateTitles = [new() { Title = "Senran Kagura Shinovi Master - Tokyo Youma-hen" }] };
        Assert.Same(owned, new SonarrTitleIndex([owned]).FindExact("Senran Kagura"));
        Assert.Same(owned, SonarrTitleIndex.ResolveLookup("SENRAN KAGURA SHINOVI MASTER", [owned],
            [new SonarrSeries { TvdbId = 264053, Title = "Senran Kagura" }]));
    }

    [Fact]
    public void UnownedSpinoffTakesPriorityOverOwnedParent()
    {
        var owned = new SonarrSeries { Id = 1, TvdbId = 267440, Title = "Attack on Titan" };
        Assert.Null(SonarrTitleIndex.ResolveLookup("Attack on Titan: Junior High", [owned],
            [owned, new SonarrSeries { TvdbId = 299882, Title = "Attack on Titan: Junior High" }]));
    }

    [Fact]
    public void AmbiguousFranchiseIsNotAssignedToTheOnlyOwnedSpinoff()
    {
        var owned = new SonarrSeries { Id = 12, TvdbId = 443905, Title = "Code Geass: Rozé of the Recapture" };
        Assert.Null(SonarrTitleIndex.ResolveLookup("Code Geass", [owned],
            [owned, new SonarrSeries { TvdbId = 79525, Title = "Code Geass: Lelouch of the Rebellion" }]));
    }

    [Fact]
    public void NormalizationHandlesAccentsAmpersandsAndAmbiguity()
    {
        var owned = new SonarrSeries { Id = 1, Title = "Knights & Magic" };
        Assert.Same(owned, new SonarrTitleIndex([owned]).FindExact("Knights and Magic"));
        Assert.Equal(SonarrTitleIndex.Normalize("Rozé"), SonarrTitleIndex.Normalize("Roze"));
        Assert.Null(new SonarrTitleIndex([owned, new SonarrSeries { Id = 2, Title = owned.Title }]).FindExact(owned.Title!));
        Assert.Null(new SonarrTitleIndex([owned]).FindExact(""));
    }

    [Fact]
    public void CandidateDiscoveryKeepsRemakesDistinctAndFindsTranslatedSubtitleVariants()
    {
        var original = new SonarrSeries { Id = 1, Title = "Witchblade" };
        var anime = new SonarrSeries { Id = 2, Title = "Witchblade (2006)" };
        var index = new SonarrTitleIndex([original, anime]);
        Assert.Null(index.FindExact("Witchblade"));
        Assert.Equal(2, index.Candidates("Witchblade").Count);
        Assert.Single(new SonarrTitleIndex([anime]).Candidates("Witchblade"));
        Assert.Empty(new SonarrTitleIndex([anime]).Candidates("Unrelated series"));
    }
}
