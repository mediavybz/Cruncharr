using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

public class SonarrTitleIndexTests
{
    [Fact]
    public void ArcLabelsAndCompoundChapterNamesCanBeVerifiedWithoutHardcodedSeriesIds()
    {
        var series = new SonarrSeries { Id = 1, Title = "Monogatari" };
        Assert.Single(new SonarrTitleIndex([series]).Candidates("Tsukimonogatari"));
        Assert.Single(new SonarrTitleIndex([series]).Candidates("Nekomonogatari Black"));
        Assert.True(SonarrTitleIndex.EpisodesConfirmIdentity(["Distant Memories", "Haruka's Heart"],
            ["Migiwa Kazuha Arc I: Distant Memories", "Migiwa Kazuha Arc IV: Haruka's Heart"]));
        Assert.False(SonarrTitleIndex.EpisodesConfirmIdentity(["Distant Memories"],
            ["First Arc I: Distant Memories", "Second Arc I: Distant Memories"]));
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

    // Crunchyroll lists a franchise's films as their own show; TVDB files them under the
    // franchise series. Only an exact franchise name, held by exactly one series, qualifies.
    // Real TVDB season 0 entries (2026-11-13). Crunchyroll lists each film as its own show.
    private static readonly SonarrEpisode[] OverlordSpecials =
    [
        new() { Id = 10, SeasonNumber = 0, EpisodeNumber = 10, Title = "Movie: The Undead King", Runtime = 102 },
        new() { Id = 11, SeasonNumber = 0, EpisodeNumber = 11, Title = "Movie: The Dark Hero", Runtime = 93 },
        new() { Id = 14, SeasonNumber = 0, EpisodeNumber = 14, Title = "The Undead King - Theater Manners", Runtime = 2 },
        new() { Id = 15, SeasonNumber = 0, EpisodeNumber = 15, Title = "The Dark Hero - Theater Manners", Runtime = 2 },
        new() { Id = 99, SeasonNumber = 1, EpisodeNumber = 1, Title = "The Dark Hero", Runtime = 24 }
    ];

    [Theory]
    [InlineData(93)]
    [InlineData(0)] // running time unknown: the closer title still decides
    public void FilmMatchesItsFranchiseSpecialNotTheClipsSharingItsName(int minutes)
    {
        Assert.Equal(11, SonarrTitleIndex.FindFilmSpecial("Overlord: The Dark Hero", minutes, "Overlord", OverlordSpecials)?.Id);
        Assert.Equal(10, SonarrTitleIndex.FindFilmSpecial("Overlord: The Undead King", minutes, "Overlord", OverlordSpecials)?.Id);
    }

    [Fact]
    public void FilmRunningTimeRulesOutAShortEpisodeWithTheSameTitle()
    {
        SonarrEpisode[] specials =
        [
            new() { Id = 3, SeasonNumber = 0, EpisodeNumber = 3, Title = "Heaven's Lost Property Final - The Movie: Eternally My Master", Runtime = 50 },
            new() { Id = 4, SeasonNumber = 0, EpisodeNumber = 4, Title = "Heaven’s Lost Property Final: The Movie - Eternally My Master", Runtime = 24 }
        ];
        // Unknown running time: two equally close titles are left unmatched.
        Assert.Null(SonarrTitleIndex.FindFilmSpecial("Heaven's Lost Property Final – The Movie: Eternally My Master", 0, "Heaven's Lost Property", specials));
        Assert.Equal(3, SonarrTitleIndex.FindFilmSpecial("Heaven's Lost Property Final – The Movie: Eternally My Master", 49, "Heaven's Lost Property", specials)?.Id);
    }

    [Theory]
    [InlineData("Kabaneri of the Iron Fortress: Life That Burns", "Kabaneri of the Iron Fortress", 2)]
    [InlineData("Kabaneri of the Iron Fortress: Light That Gathers", "Kabaneri of the Iron Fortress", 1)]
    [InlineData("Date A Live The Movie: Mayuri Judgement", "Date A Live", 3)]
    [InlineData("One Piece Film: The Desert Princess and the Pirates, Adventures in Alabasta", "One Piece", 16)]
    [InlineData("Gintama: The Very Final", "Gintama", 17)]
    [InlineData("PSYCHO-PASS: The Movie", "Psycho-Pass", 12)]
    [InlineData("SPY x FAMILY CODE: White", "SPY x FAMILY", 5)]
    public void FilmTitlesMatchTheirTranslatedSpecial(string film, string series, int expected)
    {
        SonarrEpisode[] specials =
        [
            new() { Id = 1, SeasonNumber = 0, EpisodeNumber = 1, Title = "Kabaneri of the Iron Fortress Recap 1: Gathering Light", Runtime = 105 },
            new() { Id = 2, SeasonNumber = 0, EpisodeNumber = 2, Title = "Kabaneri of the Iron Fortress Recap 2: Burning Life", Runtime = 105 },
            new() { Id = 3, SeasonNumber = 0, EpisodeNumber = 3, Title = "Date A Live Movie: Mayuri Judgment", Runtime = 75 },
            new() { Id = 16, SeasonNumber = 0, EpisodeNumber = 16, Title = "Episode of Arabasta: The Desert Princess and the Pirates", Runtime = 90 },
            new() { Id = 17, SeasonNumber = 0, EpisodeNumber = 17, Title = "Gintama: The Final", Runtime = 104 },
            new() { Id = 7, SeasonNumber = 0, EpisodeNumber = 7, Title = "Gintama: The Movie: The Final Chapter: Be Forever Yorozuya", Runtime = 110 },
            new() { Id = 12, SeasonNumber = 0, EpisodeNumber = 12, Title = "Psycho-Pass: The Movie", Runtime = 110 },
            new() { Id = 20, SeasonNumber = 0, EpisodeNumber = 20, Title = "Psycho-Pass: Providence", Runtime = 120 },
            new() { Id = 5, SeasonNumber = 0, EpisodeNumber = 3, Title = "CODE: White", Runtime = 110 },
            new() { Id = 6, SeasonNumber = 0, EpisodeNumber = 4, Title = "SPY×FAMILY CODE: White - Movie Etiquette Course", Runtime = 2 }
        ];
        Assert.Equal(expected, SonarrTitleIndex.FindFilmSpecial(film, 0, series, specials)?.Id);
    }

    [Theory]
    [InlineData("Monster Strike The Movie", "Monster")] // a different franchise sharing a word
    [InlineData("Assassination Classroom the Movie: Our Time", "Assassination Classroom")] // a different film of the franchise
    [InlineData("Tokyo Revengers (Live-Action Movie)", "Tokyo Revengers")] // TVDB has no special for it
    public void FilmWithoutASpecialHasNoMatch(string film, string series)
    {
        SonarrEpisode[] specials =
        [
            new() { Id = 1, SeasonNumber = 0, EpisodeNumber = 1, Title = "Monster - A Monster Without A Name by Franz Bonaparta", Runtime = 0 },
            new() { Id = 2, SeasonNumber = 0, EpisodeNumber = 2, Title = "Assassination Classroom the Movie: 365 Days' Time", Runtime = 92 },
            new() { Id = 3, SeasonNumber = 0, EpisodeNumber = 3, Title = "Fairy Tale Revengers - Snow White #3", Runtime = 4 }
        ];
        Assert.Null(SonarrTitleIndex.FindFilmSpecial(film, 0, series, specials));
    }

    [Theory]
    [InlineData("Puella Magi Madoka Magica Movies", 965)]
    [InlineData("Attack on Titan OAD", 7)]
    [InlineData("Attack on Titan - The Movie", 7)]
    [InlineData("Puella Magi Madoka Magica", null)] // no collection suffix
    [InlineData("Magia Record Movies", null)]       // the franchise is only part of a series title
    [InlineData("Movies", null)]
    public void FilmCollectionsResolveToTheirFranchiseSeries(string title, int? expected)
    {
        var index = new SonarrTitleIndex(
        [
            new SonarrSeries { Id = 965, TvdbId = 219181, Title = "Puella Magi Madoka Magica" },
            new SonarrSeries { Id = 433, TvdbId = 370931, Title = "Magia Record: Puella Magi Madoka Magica Side Story" },
            new SonarrSeries { Id = 7, TvdbId = 267440, Title = "Attack on Titan" }
        ]);
        Assert.Equal(expected, index.FindCollectionParent(title)?.Id);
    }

    [Fact]
    public void FilmCollectionNamedLikeTwoSeriesIsLeftToTheUser()
    {
        var index = new SonarrTitleIndex(
        [
            new SonarrSeries { Id = 1, TvdbId = 1, Title = "Witchblade" },
            new SonarrSeries { Id = 2, TvdbId = 2, Title = "Witchblade (2006)", AlternateTitles = [new() { Title = "Witchblade" }] }
        ]);
        Assert.Null(index.FindCollectionParent("Witchblade Movies"));
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

    [Theory]
    [InlineData("Clannad After Story", "Clannad")]
    [InlineData("Overlord (Spanish Dub)", "Overlord")]
    [InlineData("Gintama Season 2", "Gintama")]
    [InlineData("Symphogear GX", "Senki Zesshou Symphogear")]
    [InlineData("Attack on Titan Final Season", "Attack on Titan")]
    public void UnseparatedSuffixesStillNominateTheFranchiseForEpisodeVerification(string providerTitle, string sonarrTitle)
    {
        var owned = new SonarrSeries { Id = 7, TvdbId = 100, Title = sonarrTitle };
        if (sonarrTitle == "Senki Zesshou Symphogear")
            owned.AlternateTitles = [new() { Title = "Senki Zesshou Symphogear GX" }];
        var index = new SonarrTitleIndex([owned]);
        Assert.Null(index.FindExact(providerTitle));
        Assert.Same(owned, Assert.Single(index.Candidates(providerTitle)));
    }

    [Fact]
    public void ShortOrUnrelatedNamesAreNotNominatedByContainment()
    {
        var index = new SonarrTitleIndex([new SonarrSeries { Id = 1, TvdbId = 1, Title = "Blue" }, new SonarrSeries { Id = 2, TvdbId = 2, Title = "Noragami" }]);
        Assert.Empty(index.Candidates("Blue Lock"));
        Assert.Empty(index.Candidates("Dr. Stone"));
    }

    [Fact]
    public void MultiplicationSignMatchesTheLetterX()
    {
        var owned = new SonarrSeries { Id = 3, TvdbId = 3, Title = "SPY×FAMILY" };
        Assert.Same(owned, new SonarrTitleIndex([owned]).FindExact("SPY x FAMILY"));
        Assert.Equal("spyxfamily", SonarrTitleIndex.Normalize("SPY×FAMILY"));
    }

    [Fact]
    public void SeasonAliasCanLinkMultipleCrunchyrollListingsToOneSonarrSeries()
    {
        var owned = new SonarrSeries
        {
            Id = 49,
            TvdbId = 264053,
            Title = "Senran Kagura",
            AlternateTitles = [new() { Title = "Senran Kagura Shinovi Master - Tokyo Youma-hen" }]
        };
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
