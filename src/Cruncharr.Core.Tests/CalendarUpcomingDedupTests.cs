using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// GUARD — when Crunchyroll releases an episode, its AniList "Upcoming" placeholder for the
// same show/day must be dropped (IsSameShow), so the real downloadable card REPLACES the
// placeholder instead of showing next to it. Episode numbers are deliberately NOT compared:
// AniList absolute numbering vs CR per-season numbering (and CR "1-2" merges) make them
// unreliable; the match is series-level (CR id, else fuzzy title).
public class CalendarUpcomingDedupTests
{
    private static CalendarEpisode Ep(string? seriesId, string? seasonName) =>
        new() { CrSeriesID = seriesId, SeasonName = seasonName };

    [Fact]
    public void SameCrSeriesId_IsSameShow()
    {
        Assert.True(CalendarService.IsSameShow(
            Ep("G6DQDD3WR", "Fairy Tail"),
            Ep("g6dqdd3wr", "Fairy Tail (2018)")));
    }

    [Fact]
    public void DifferentCrSeriesId_IsNotSameShow_EvenWithSimilarTitles()
    {
        Assert.False(CalendarService.IsSameShow(
            Ep("AAAA1", "Fairy Tail"),
            Ep("BBBB2", "Fairy Tail")));
    }

    [Fact]
    public void MissingSeriesId_FuzzyTitleMatch()
    {
        // AniList entry with no parseable CR series id still dedups against the CR card
        // when the titles normalize to the same show (punctuation/case differences).
        Assert.True(CalendarService.IsSameShow(
            Ep(null, "Saga of Tanya the Evil: Season 2"),
            Ep(null, "Saga of Tanya the Evil Season 2")));
    }

    [Fact]
    public void MissingSeriesId_DifferentShows_NoMatch()
    {
        Assert.False(CalendarService.IsSameShow(
            Ep(null, "One Piece"),
            Ep(null, "Detective Conan")));
    }

    [Fact]
    public void EmptyTitles_NeverMatch()
    {
        Assert.False(CalendarService.IsSameShow(Ep(null, null), Ep(null, "")));
    }
}
