using System.Globalization;
using Cruncharr.Core.Services;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Tests;

// Sonarr's command "queued" time decides whether a retried search reuses the command Sonarr already
// accepted. It was read through the machine's date format, so outside the US the day and month
// swapped (a recent command looked months old or in the future) or the date did not parse at all.
public class SonarrCommandTimeTests
{
    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void QueuedTime_IsReadTheSameInEveryCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            // A day above 12 and one that a day/month swap would move.
            foreach (var (json, expected) in new[]
                     {
                         ("2026-10-13T14:05:00Z", new DateTime(2026, 10, 13, 14, 5, 0, DateTimeKind.Utc)),
                         ("2026-03-10T08:00:00Z", new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc))
                     })
            {
                var parsed = JObject.Parse($$"""{"queued":"{{json}}"}""")["queued"];
                Assert.Equal(JTokenType.Date, parsed!.Type);
                Assert.Equal(expected, SonarrService.QueuedUtc(parsed));
                Assert.Equal(expected, SonarrService.QueuedUtc(new JValue(json)));
            }
            Assert.Null(SonarrService.QueuedUtc(null));
            Assert.Null(SonarrService.QueuedUtc(new JValue("not a date")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
