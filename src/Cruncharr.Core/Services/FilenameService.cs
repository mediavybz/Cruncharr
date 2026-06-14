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
    public List<string>? Overrides { get; set; }
    public List<string>? SelectedDubs { get; set; }
}

public class FilenameService : IFilenameService
{
    public string FormatFilename(string template, EpisodeInfo episode, FilenameOptions? options = null)
    {
        options ??= new FilenameOptions();

        var variables = new List<Variable>();

        // Upstream variables
        variables.Add(new Variable("title", episode.Title, true));
        // Aliases so the names documented in the UI resolve.
        variables.Add(new Variable("episodeTitle", episode.Title, true));

        // Episode: try to parse as double for fractional episodes (e.g., 12.5), fallback to int
        object episodeValue;
        if (!string.IsNullOrEmpty(episode.Episode) && double.TryParse(episode.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out var epDouble))
        {
            episodeValue = Math.Round(epDouble, 1);
        }
        else
        {
            episodeValue = episode.EpisodeNumber;
        }
        variables.Add(new Variable("episode", episodeValue, false));

        variables.Add(new Variable("seriesTitle", episode.SeriesTitle, true));
        variables.Add(new Variable("seasonTitle", episode.SeasonTitle ?? string.Empty, true));
        variables.Add(new Variable("season", (double)episode.SeasonNumber, false));
        variables.Add(new Variable("dubs", string.Join(", ", options.SelectedDubs ?? new List<string>()), true));

        // Sonarr variables (ported from upstream FileNameManager)
        if (options.SonarrSeries != null)
        {
            variables.Add(new Variable("sonarrSeriesTitle", options.SonarrSeries.Title ?? string.Empty, true));
            variables.Add(new Variable("sonarrSeriesReleaseYear", options.SonarrSeries.Year, true));
        }
        if (options.SonarrEpisode != null)
        {
            variables.Add(new Variable("sonarrEpisodeTitle", options.SonarrEpisode.Title ?? string.Empty, true));
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
            variables.Add(new Variable("quality", options.Quality, false));
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
        joinedResult = Regex.Replace(joinedResult, @"\{([A-Za-z0-9]+)(?::([^}]+))?\}", m =>
        {
            var key = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;

            var variable = variables.FirstOrDefault(v => string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase));
            if (variable == null) return m.Value;

            var value = variable.ReplaceWith?.ToString() ?? string.Empty;

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

    public string SanitizeFilename(string filename)
    {
        return FileNameManager.CleanupFilename(filename);
    }
}
