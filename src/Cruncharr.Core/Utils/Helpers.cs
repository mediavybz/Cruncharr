using System.Text.RegularExpressions;

namespace Cruncharr.Core.Utils;

public static class Helpers
{
    public static string? ExtractNumberAfterS(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return null;

        var match = Regex.Match(identifier, @"\|S(\d+)");
        if (!match.Success)
            return null;

        var value = match.Groups[1].Value;

        // Legacy CR identifiers encoded the real season number here ("...|S5|E10"). Modern ones
        // embed a SEASON RESOURCE ID instead ("...|S00364555|E4"), so extracting it yields garbage
        // like 364555 that then wins over the correct season_number field (a Season 5 episode filed
        // under Season 364555 / 1). Only the short legacy form is a real season number; reject the
        // long resource-id form so callers fall back to the authoritative season_number.
        if (value.Length > 2)
            return null;

        return value;
    }
}
