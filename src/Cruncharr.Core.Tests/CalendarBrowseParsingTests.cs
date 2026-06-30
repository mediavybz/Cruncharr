using Cruncharr.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace Cruncharr.Core.Tests;

// Regression guard: Crunchyroll's browse "newly_added" feed returns episode_metadata.episode_number
// = null for specials / movies / recaps. The model maps that to a non-nullable int, so a single null
// made Newtonsoft throw on the WHOLE page -> GetNewEpisodesAsync returned null -> the calendar showed
// nothing. Diagnosed live: "Error converting value {null} to type 'System.Int32'. Path
// 'data[32].episode_metadata.episode_number'". The fix is NullValueHandling.Ignore on that property.
public class CalendarBrowseParsingTests
{
    [Fact]
    public void NullEpisodeNumber_doesNotBreakBrowsePageParse()
    {
        // A page where one entry (a special) has a null episode_number, like the live CR response.
        var json = @"{
            ""total"": 2,
            ""data"": [
                { ""id"": ""G1"", ""title"": ""Ep 1"", ""episode_metadata"": { ""series_id"": ""GSER"", ""episode_number"": 1, ""episode"": ""1"" } },
                { ""id"": ""G2"", ""title"": ""Special"", ""episode_metadata"": { ""series_id"": ""GSER"", ""episode_number"": null, ""episode"": ""SP"" } }
            ]
        }";

        var ex = Record.Exception(() => JsonConvert.DeserializeObject<CrBrowseEpisodeBase>(json));
        Assert.Null(ex); // must not throw on the null episode_number

        var parsed = JsonConvert.DeserializeObject<CrBrowseEpisodeBase>(json);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Data);
        Assert.Equal(2, parsed.Data!.Count); // the whole page survives, not just the dropped one
        Assert.Equal(1, parsed.Data[0].EpisodeMetadata.EpisodeCount);
        Assert.Equal(0, parsed.Data[1].EpisodeMetadata.EpisodeCount); // null -> default 0, no crash
    }
}
