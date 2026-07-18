using System.Globalization;
using System.Text.RegularExpressions;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils.Files;

namespace Cruncharr.Core.Services;

public interface IFilenameService
{
    string FormatFilename(string template, EpisodeInfo episode, FilenameOptions? options = null);
    string SanitizeFilename(string filename);
}

public class FilenameOptions
{
    public int NumberPadding { get; set; } = 2;
    public string? WhitespaceReplace { get; set; }
    public string? Quality { get; set; }
    public string? AudioLanguage { get; set; }
    public SonarrSeries? SonarrSeries { get; set; }
    public SonarrEpisode? SonarrEpisode { get; set; }
    public SonarrNamingConfig? SonarrNamingConfig { get; set; }
    public List<string>? Overrides { get; set; }
    public List<string>? SelectedDubs { get; set; }
    // When true and SonarrEpisode is set, the {episode}/{season} variables use Sonarr's
    // numbering instead of Crunchyroll's (upstream UseSonarrNumbering).
    public bool UseSonarrNumbering { get; set; }
}

public class FilenameService : IFilenameService
{
    private static readonly Regex SonarrDuplicateSeparatorRegex = new(@"([- ._])\1+", RegexOptions.Compiled);

    public string FormatFilename(string template, EpisodeInfo episode, FilenameOptions? options = null)
    {
        options ??= new FilenameOptions();

        var variables = new List<Variable>();

        // UseSonarrNumbering: when on AND an episode matched, the file mirrors Sonarr/TVDB
        // identity — season/episode NUMBERS *and* the episode TITLE (user intent: "name it
        // like Sonarr"). Off or unmatched, it keeps Crunchyroll's own title. The always-CR
        // aliases below let a Sonarr-numbered template still opt into the Crunchyroll title.
        bool useSonarrNumbering = options.UseSonarrNumbering && options.SonarrEpisode != null;
        bool useSonarrSeries = options.UseSonarrNumbering &&
                               !string.IsNullOrWhiteSpace(options.SonarrSeries?.Title);
        var sonarrNaming = options.SonarrNamingConfig ?? SonarrNamingConfig.Default;
        var effectiveTitle = (useSonarrNumbering && !string.IsNullOrEmpty(options.SonarrEpisode!.Title))
            ? ApplySonarrFilenameRules(options.SonarrEpisode!.Title!, sonarrNaming)
            : episode.Title;
        var effectiveSeriesTitle = useSonarrSeries
            ? ApplySonarrFilenameRules(options.SonarrSeries!.Title!, sonarrNaming)
            : episode.SeriesTitle;

        variables.Add(new Variable("title", effectiveTitle, true));
        // Aliases so the names documented in the UI resolve.
        variables.Add(new Variable("episodeTitle", effectiveTitle, true));
        // Always the Crunchyroll title, regardless of Sonarr numbering.
        variables.Add(new Variable("crTitle", episode.Title, true));
        variables.Add(new Variable("crEpisodeTitle", episode.Title, true));
        variables.Add(new Variable("seriesTitle", effectiveSeriesTitle, true));
        variables.Add(new Variable("crSeriesTitle", episode.SeriesTitle, true));

        // Episode: try to parse as double for fractional episodes (e.g., 12.5), fallback to int
        object episodeValue;
        if (useSonarrNumbering)
        {
            episodeValue = (double)options.SonarrEpisode!.EpisodeNumber;
        }
        else if (!string.IsNullOrEmpty(episode.Episode) && double.TryParse(episode.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out var epDouble))
        {
            episodeValue = Math.Round(epDouble, 1);
        }
        else
        {
            episodeValue = episode.EpisodeNumber;
        }
        variables.Add(new Variable("episode", episodeValue, false));

        variables.Add(new Variable("seasonTitle", episode.SeasonTitle ?? string.Empty, true));
        variables.Add(new Variable("season", useSonarrNumbering ? (double)options.SonarrEpisode!.SeasonNumber : (double)episode.SeasonNumber, false));
        variables.Add(new Variable("dubs", string.Join(", ", options.SelectedDubs ?? new List<string>()), true));

        // Sonarr variables (ported from upstream FileNameManager)
        if (options.SonarrSeries != null)
        {
            variables.Add(new Variable(
                "sonarrSeriesTitle",
                ApplySonarrFilenameRules(options.SonarrSeries.Title ?? string.Empty, sonarrNaming),
                true));
            variables.Add(new Variable("sonarrSeriesReleaseYear", options.SonarrSeries.Year, true));
        }
        if (options.SonarrEpisode != null)
        {
            variables.Add(new Variable(
                "sonarrEpisodeTitle",
                ApplySonarrFilenameRules(options.SonarrEpisode.Title ?? string.Empty, sonarrNaming),
                true));
        }

        // Height/width from quality config when available
        int height = 0;
        int width = 0;
        if (!string.IsNullOrEmpty(options.Quality))
        {
            var qualityStr = options.Quality.Replace("p", "").Trim();
            if (int.TryParse(qualityStr, out height))
            {
                width = (int)Math.Round(height * 16.0 / 9.0);
            }
        }
        variables.Add(new Variable("height", height, false));
        variables.Add(new Variable("width", width, false));

        // Backward compatibility variables (not in upstream but supported by previous implementation)
        if (!string.IsNullOrEmpty(options.Quality))
        {
            // A bare height ("1080", from the post-download resolution probe) renders as
            // "1080p"; values that already carry a suffix ("1080p", "best") pass through.
            // `height` above already parsed the resolution (strips a trailing "p"), so both
            // "1080" and "1080p" resolve to 1080; "best"/"worst" leave height 0.
            var qualityDisplay = height > 0 ? $"{height}p" : options.Quality;
            variables.Add(new Variable("quality", qualityDisplay, false));
            // Sonarr's {Quality Full}/{Quality Title} are SOURCE-qualified, e.g. "WEBDL-1080p".
            // Crunchyroll delivers via web streaming, so the source is WEBDL — this is what
            // Sonarr itself assigns to Crunchyroll grabs, so the filename matches Sonarr. When
            // the resolution is not yet numeric (the pre-probe "best"/"worst" temp name) there is
            // nothing to source-qualify, so it passes through unprefixed.
            var qualityFull = height > 0 ? $"WEBDL-{height}p" : qualityDisplay;
            variables.Add(new Variable("qualityFull", qualityFull, false));
            variables.Add(new Variable("qualityTitle", qualityFull, false));
        }
        if (!string.IsNullOrEmpty(options.AudioLanguage))
        {
            variables.Add(new Variable("audioLang", options.AudioLanguage, false));
            variables.Add(new Variable("audioLanguage", options.AudioLanguage, false));
        }
        variables.Add(new Variable("id", episode.Id, false));
        variables.Add(new Variable("episodeId", episode.Id, false));
        variables.Add(new Variable("guid", episode.Guid, false));
        variables.Add(new Variable("seriesId", episode.SeriesId ?? string.Empty, false));
        variables.Add(new Variable("seasonId", episode.SeasonId ?? string.Empty, false));

        // Use upstream FileNameManager for ${var} syntax
        var result = FileNameManager.ParseFileName(
            template,
            variables,
            options.NumberPadding,
            options.WhitespaceReplace ?? string.Empty,
            options.Overrides ?? new List<string>()
        );

        // Join path segments
        var joinedResult = string.Join(Path.DirectorySeparatorChar, result);

        // Also support legacy {var} syntax with optional formatting (e.g., {season:00})
        // Match {var} / {var:00} tokens. Allow SPACES in the name so Sonarr/Plex-style tokens work
        // ({Series Title}, {Episode Title}, {Quality Full}); match by stripping spaces + case so
        // "Series Title" resolves to the seriesTitle variable. Unknown tokens are left untouched.
        joinedResult = Regex.Replace(joinedResult, @"\{([A-Za-z0-9 ]+?)(?::([^}]+))?\}", m =>
        {
            var key = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;

            var normKey = key.Replace(" ", string.Empty);
            var variable = variables.FirstOrDefault(v => string.Equals(v.Name.Replace(" ", string.Empty), normKey, StringComparison.OrdinalIgnoreCase));
            if (variable == null) return m.Value;

            var value = variable.ReplaceWith?.ToString() ?? string.Empty;

            // The desktop ${var} path sanitizes every Variable marked Sanitize before inserting
            // it. Apply that same rule to the legacy/web {Var} adapter so title text such as "/"
            // cannot become an unintended directory separator.
            if (variable.Sanitize)
            {
                value = FileNameManager.CleanupFilename(value);
                if (variable.Type == "string" && !string.IsNullOrEmpty(options.WhitespaceReplace))
                {
                    value = value.Replace(" ", options.WhitespaceReplace);
                }
            }

            if (format != null && int.TryParse(value, out var num))
            {
                if (format == "00" || format == "D2") return num.ToString("D2");
                if (format == "000" || format == "D3") return num.ToString("D3");
                if (format == "D4") return num.ToString("D4");
                if (int.TryParse(format, out var pad)) return num.ToString($"D{pad}");
            }

            return value;
        });

        return joinedResult;
    }

    internal static string ApplySonarrFilenameRules(string value, SonarrNamingConfig namingConfig)
    {
        var result = value;
        if (namingConfig.ReplaceIllegalCharacters)
        {
            if (namingConfig.ColonReplacementFormat == SonarrColonReplacementFormat.Smart)
            {
                result = result.Replace(": ", " - ", StringComparison.Ordinal);
                result = result.Replace(":", "-", StringComparison.Ordinal);
            }
            else
            {
                var replacement = namingConfig.ColonReplacementFormat switch
                {
                    SonarrColonReplacementFormat.Dash => "-",
                    SonarrColonReplacementFormat.SpaceDash => " -",
                    SonarrColonReplacementFormat.SpaceDashSpace => " - ",
                    SonarrColonReplacementFormat.Custom => namingConfig.CustomColonReplacementFormat,
                    _ => string.Empty
                };
                result = result.Replace(":", replacement, StringComparison.Ordinal);
            }
        }
        else
        {
            result = result.Replace(":", string.Empty, StringComparison.Ordinal);
        }

        var badCharacters = new[] { "\\", "/", "<", ">", "?", "*", "|", "\"" };
        var goodCharacters = new[] { "+", "+", string.Empty, string.Empty, "!", "-", string.Empty, string.Empty };
        for (var index = 0; index < badCharacters.Length; index++)
        {
            result = result.Replace(
                badCharacters[index],
                namingConfig.ReplaceIllegalCharacters ? goodCharacters[index] : string.Empty,
                StringComparison.Ordinal);
        }

        result = SonarrDuplicateSeparatorRegex.Replace(
            result,
            match => match.Captures[0].Value[0].ToString());
        return result.TrimStart(' ', '.').TrimEnd(' ');
    }

    public string SanitizeFilename(string filename)
    {
        return FileNameManager.CleanupFilename(filename);
    }
}
