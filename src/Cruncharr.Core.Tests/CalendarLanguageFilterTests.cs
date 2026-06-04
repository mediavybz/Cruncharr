using Cruncharr.Core.Utils;
using Xunit;

namespace Cruncharr.Core.Tests;

public class CalendarLanguageFilterTests
{
    [Theory]
    [InlineData("Witch Hat Atelier Season 1 (English)", "en-us", true)]
    [InlineData("Witch Hat Atelier Season 1 (Português (Brasil))", "en-us", false)]
    [InlineData("Witch Hat Atelier Season 1 (Español)", "en-us", false)]
    [InlineData("Witch Hat Atelier Season 1 (Français)", "en-us", false)]
    [InlineData("Season 1 (Subbed)", "en-us", true)]
    [InlineData("Season 1", "en-us", true)]
    [InlineData("Witch Hat Atelier Season 1 (English)", "fr", false)]
    [InlineData("Witch Hat Atelier Season 1 (Français)", "fr", true)]
    [InlineData("L'Atelier des Sorciers", "fr", false)]
    [InlineData("Witch Hat Atelier Season 1 (Español)", "es", true)]
    [InlineData("Witch Hat Atelier Season 1 (Español (España))", "es-es", true)]
    [InlineData("Witch Hat Atelier Season 1 (Português (Brasil))", "pt-br", true)]
    [InlineData("Witch Hat Atelier Season 1 (Deutsch)", "de", true)]
    [InlineData("Witch Hat Atelier Season 1 (Italiano)", "it", true)]
    [InlineData("Witch Hat Atelier Season 1 (Русский)", "ru", true)]
    [InlineData(null, "en-us", true)]
    [InlineData("", "en-us", true)]
    [InlineData("Season 1 (Uncut)", "en-us", true)]
    public void MatchesLanguage_ReturnsCorrectResult(string seasonName, string language, bool expected)
    {
        var result = CrSimulcastCalendarFilter.MatchesLanguage(seasonName, language);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatchesLanguage_EnglishShowsOnlyEnglishAndSubs()
    {
        var episodes = new[]
        {
            "Witch Hat Atelier Season 1 (English)",
            "Witch Hat Atelier Season 1 (Português (Brasil))",
            "Witch Hat Atelier Season 1 (Español)",
            "Witch Hat Atelier Season 1 (Français)",
            "Season 1 (Subbed)",
            "Season 1"
        };

        var filtered = episodes.Where(e => CrSimulcastCalendarFilter.MatchesLanguage(e, "en-us")).ToList();

        Assert.Equal(3, filtered.Count);
        Assert.Contains("Witch Hat Atelier Season 1 (English)", filtered);
        Assert.Contains("Season 1 (Subbed)", filtered);
        Assert.Contains("Season 1", filtered);
    }

    [Fact]
    public void MatchesLanguage_FrenchShowsOnlyFrench()
    {
        var episodes = new[]
        {
            "Witch Hat Atelier Season 1 (English)",
            "Witch Hat Atelier Season 1 (Português (Brasil))",
            "Witch Hat Atelier Season 1 (Français)",
            "L'Atelier des Sorciers"
        };

        var filtered = episodes.Where(e => CrSimulcastCalendarFilter.MatchesLanguage(e, "fr")).ToList();

        Assert.Single(filtered);
        Assert.Contains("Witch Hat Atelier Season 1 (Français)", filtered);
    }
}