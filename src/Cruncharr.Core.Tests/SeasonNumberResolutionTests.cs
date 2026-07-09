using Cruncharr.Core.Utils;
using Xunit;

namespace Cruncharr.Core.Tests;

// GUARD — Crunchyroll changed its identifier format from the legacy "...|S5|E10" (where the
// "|S" segment WAS the season number) to "...|S00364555|E4" (where it is a season RESOURCE ID).
// ExtractNumberAfterS must NOT return the resource id, or it wins over the correct season_number
// field and a Season 5 episode gets filed under Season 364555 / 1. Live-verified against the CR
// API for "The Daily Life of the Immortal King" (series GZJH3DJ8E). See DownloadService season
// resolution + QueueController placeholder default.
public class SeasonNumberResolutionTests
{
    [Theory]
    [InlineData("GZJH3DJ8E|S00364555|E4")]   // modern resource-id form (Season 5) -> must NOT extract
    [InlineData("GZJH3DJ8E|S00194481|E1")]   // modern resource-id form (Season 1)
    public void ResourceIdIdentifier_IsRejected(string identifier)
    {
        // null => callers fall back to the authoritative season_number field
        Assert.Null(Helpers.ExtractNumberAfterS(identifier));
    }

    [Theory]
    [InlineData("GXXXXXXXX|S5|E10", "5")]     // legacy form still works
    [InlineData("GXXXXXXXX|S12|E1", "12")]
    [InlineData("GXXXXXXXX|S0|E1", "0")]      // specials season
    public void LegacyShortIdentifier_ExtractsSeason(string identifier, string expected)
    {
        Assert.Equal(expected, Helpers.ExtractNumberAfterS(identifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-season-here")]
    public void MissingOrMalformed_ReturnsNull(string? identifier)
    {
        Assert.Null(Helpers.ExtractNumberAfterS(identifier));
    }
}
