using System.Globalization;
using System.Text.RegularExpressions;
using Cruncharr.Core.Models;

namespace Cruncharr.Core.Services;

public interface IFilenameService{
    string FormatFilename(string template, EpisodeInfo episode, FilenameOptions? options = null);
    string SanitizeFilename(string filename);
}

public class FilenameOptions{
    public int NumberPadding { get; set; } = 2;
    public string? WhitespaceReplace { get; set; }
    public string? Quality { get; set; }
    public string? AudioLanguage { get; set; }
}

public class FilenameService : IFilenameService{
    public string FormatFilename(string template, EpisodeInfo episode, FilenameOptions? options = null){
        options ??= new FilenameOptions();
        
        // Support both {var} and ${var} syntax
        var result = template;
        
        // Build variable replacements
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase){
            ["seriesTitle"] = SanitizeFilename(episode.SeriesTitle),
            ["episodeTitle"] = SanitizeFilename(episode.Title),
            ["season"] = episode.SeasonNumber.ToString(),
            ["season00"] = episode.SeasonNumber.ToString($"D{options.NumberPadding}"),
            ["episode"] = episode.EpisodeNumber.ToString(),
            ["episode00"] = episode.EpisodeNumber.ToString($"D{options.NumberPadding}"),
            ["id"] = episode.Id ?? "",
            ["guid"] = episode.Guid ?? "",
            ["seriesId"] = episode.SeriesId ?? "",
            ["seasonId"] = episode.SeasonId ?? "",
            ["seasonTitle"] = SanitizeFilename(episode.SeasonTitle ?? ""),
        };
        
        if (!string.IsNullOrEmpty(options.Quality)){
            replacements["height"] = options.Quality;
            replacements["quality"] = options.Quality;
        }
        
        if (!string.IsNullOrEmpty(options.AudioLanguage)){
            replacements["audioLang"] = options.AudioLanguage;
            replacements["audioLanguage"] = options.AudioLanguage;
        }
        
        // Replace ${var} syntax
        result = Regex.Replace(result, @"\$\{([A-Za-z0-9]+)\}", m =>{
            var key = m.Groups[1].Value;
            return replacements.TryGetValue(key, out var value) ? value : m.Value;
        });
        
        // Replace {var} syntax (with optional format like {season:00})
        result = Regex.Replace(result, @"\{([A-Za-z0-9]+)(?::([^}]+))?\}", m =>{
            var key = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            
            if (!replacements.TryGetValue(key, out var value)) return m.Value;
            
            if (format != null && int.TryParse(value, out var num)){
                if (format == "00" || format == "D2") return num.ToString("D2");
                if (format == "000" || format == "D3") return num.ToString("D3");
                if (format == "D4") return num.ToString("D4");
                if (int.TryParse(format, out var pad)) return num.ToString($"D{pad}");
            }
            
            return value;
        });
        
        // Apply whitespace replacement if configured
        if (!string.IsNullOrEmpty(options.WhitespaceReplace)){
            result = result.Replace(" ", options.WhitespaceReplace);
        }
        
        return SanitizeFilename(result);
    }
    
    public string SanitizeFilename(string filename){
        if (string.IsNullOrEmpty(filename)) return "unknown";
        
        // Remove illegal characters
        var illegal = new Regex(@"[\/\?<>\\:\*\|"":]");
        var control = new Regex(@"[\x00-\x1f\x80-\x9f]");
        var reserved = new Regex(@"^\.\.?$");
        var windowsReserved = new Regex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$", RegexOptions.IgnoreCase);
        var trailing = new Regex(@"[\. ]+$");
        
        filename = illegal.Replace(filename, "");
        filename = control.Replace(filename, "");
        filename = reserved.Replace(filename, "");
        filename = windowsReserved.Replace(filename, "");
        filename = trailing.Replace(filename, "");
        
        // Trim and limit length
        filename = filename.Trim();
        if (filename.Length > 200){
            filename = filename.Substring(0, 200);
        }
        
        return string.IsNullOrEmpty(filename) ? "unknown" : filename;
    }
}