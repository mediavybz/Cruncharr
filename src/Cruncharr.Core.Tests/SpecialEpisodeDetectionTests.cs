using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// Guard (upstream CRD v1.6.14: "Fixed special season detection incorrectly identifying some regular
// seasons as specials"). Verified live against One Piece: the "Fish-Man Island Saga" recap season
// has episodes whose text label is a saga code ("FMI1".."FMI21") but a valid sequential
// episode_number (1..21). Those are regular episodes and must NOT be flagged as specials.
public class SpecialEpisodeDetectionTests
{
    [Theory]
    // Real regular episodes (plain numeric label) — never special.
    [InlineData("1", 1, false)]
    [InlineData("517", 517, false)]
    // Multi-episode ranges are regular.
    [InlineData("11-12", 11, false)]
    // The regression case: non-numeric saga-code label but a valid positive episode_number.
    [InlineData("FMI1", 1, false)]
    [InlineData("FMI21", 21, false)]
    // True specials/OVAs: non-numeric label AND no valid positive episode_number.
    [InlineData("OVA", 0, true)]
    [InlineData("SP", null, true)]
    [InlineData("Special", 0, true)]
    // Empty/missing label is not a special (avoids false positives).
    [InlineData("", 0, false)]
    [InlineData(null, null, false)]
    public void IsSpecialEpisode_classifies_correctly(string? label, int? episodeNumber, bool expected)
    {
        Assert.Equal(expected, CrunchyrollApiService.IsSpecialEpisode(label, episodeNumber));
    }
}
