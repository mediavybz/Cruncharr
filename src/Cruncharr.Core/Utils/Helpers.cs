using System.Text.RegularExpressions;

namespace Cruncharr.Core.Utils;

public static class Helpers
{
    public static string? ExtractNumberAfterS(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return null;

        var match = Regex.Match(identifier, @"\|S(\d+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
    }
}
