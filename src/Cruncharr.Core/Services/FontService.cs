using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IFontService{
    List<string> ExtractFontsFromAss(string assContent, bool includeTypesettingFonts = true);
    List<FontAttachment> ResolveFonts(List<string> fontNames, string? customFontsDir = null);
    string GetFontMimeType(string fontPath);
}

public class FontAttachment{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Mime { get; set; } = "";
}

public class FontService : IFontService{
    private readonly ILogger<FontService>? _logger;
    
    // Known Crunchyroll font mappings (font name -> filename)
    private static readonly Dictionary<string, string> KnownFonts = new(StringComparer.OrdinalIgnoreCase){
        { "Adobe Arabic", "AdobeArabic-Bold.otf" },
        { "Andale Mono", "andalemo.ttf" },
        { "Arial", "arial.ttf" },
        { "Arial Black", "ariblk.ttf" },
        { "Arial Bold", "arialbd.ttf" },
        { "Arial Bold Italic", "arialbi.ttf" },
        { "Arial Italic", "ariali.ttf" },
        { "Arial Unicode MS", "arialuni.ttf" },
        { "Comic Sans MS", "comic.ttf" },
        { "Comic Sans MS Bold", "comicbd.ttf" },
        { "Courier New", "cour.ttf" },
        { "Courier New Bold", "courbd.ttf" },
        { "Courier New Bold Italic", "courbi.ttf" },
        { "Courier New Italic", "couri.ttf" },
        { "DejaVu LGC Sans Mono", "DejaVuLGCSansMono.ttf" },
        { "DejaVu LGC Sans Mono Bold", "DejaVuLGCSansMono-Bold.ttf" },
        { "DejaVu LGC Sans Mono Bold Oblique", "DejaVuLGCSansMono-BoldOblique.ttf" },
        { "DejaVu LGC Sans Mono Oblique", "DejaVuLGCSansMono-Oblique.ttf" },
        { "DejaVu Sans", "DejaVuSans.ttf" },
        { "DejaVu Sans Bold", "DejaVuSans-Bold.ttf" },
        { "DejaVu Sans Bold Oblique", "DejaVuSans-BoldOblique.ttf" },
        { "DejaVu Sans Condensed", "DejaVuSansCondensed.ttf" },
        { "DejaVu Sans Condensed Bold", "DejaVuSansCondensed-Bold.ttf" },
        { "DejaVu Sans Condensed Bold Oblique", "DejaVuSansCondensed-BoldOblique.ttf" },
        { "DejaVu Sans Condensed Oblique", "DejaVuSansCondensed-Oblique.ttf" },
        { "DejaVu Sans ExtraLight", "DejaVuSans-ExtraLight.ttf" },
        { "DejaVu Sans Mono", "DejaVuSansMono.ttf" },
        { "DejaVu Sans Mono Bold", "DejaVuSansMono-Bold.ttf" },
        { "DejaVu Sans Mono Bold Oblique", "DejaVuSansMono-BoldOblique.ttf" },
        { "DejaVu Sans Mono Oblique", "DejaVuSansMono-Oblique.ttf" },
        { "DejaVu Sans Oblique", "DejaVuSans-Oblique.ttf" },
        { "Gautami", "gautami.ttf" },
        { "Georgia", "georgia.ttf" },
        { "Georgia Bold", "georgiab.ttf" },
        { "Georgia Bold Italic", "georgiaz.ttf" },
        { "Georgia Italic", "georgiai.ttf" },
        { "Impact", "impact.ttf" },
        { "Mangal", "MANGAL.woff2" },
        { "Meera Inimai", "MeeraInimai-Regular.ttf" },
        { "Noto Sans Tamil", "NotoSansTamilVariable.ttf" },
        { "Noto Sans Telugu", "NotoSansTeluguVariable.ttf" },
        { "Noto Sans Thai", "NotoSansThai.ttf" },
        { "Rubik", "Rubik-Regular.ttf" },
        { "Rubik Black", "Rubik-Black.ttf" },
        { "Rubik Black Italic", "Rubik-BlackItalic.ttf" },
        { "Rubik Bold", "Rubik-Bold.ttf" },
        { "Rubik Bold Italic", "Rubik-BoldItalic.ttf" },
        { "Rubik Italic", "Rubik-Italic.ttf" },
        { "Rubik Light", "Rubik-Light.ttf" },
        { "Rubik Light Italic", "Rubik-LightItalic.ttf" },
        { "Rubik Medium", "Rubik-Medium.ttf" },
        { "Rubik Medium Italic", "Rubik-MediumItalic.ttf" },
        { "Tahoma", "tahoma.ttf" },
        { "Times New Roman", "times.ttf" },
        { "Times New Roman Bold", "timesbd.ttf" },
        { "Times New Roman Bold Italic", "timesbi.ttf" },
        { "Times New Roman Italic", "timesi.ttf" },
        { "Trebuchet MS", "trebuc.ttf" },
        { "Trebuchet MS Bold", "trebucbd.ttf" },
        { "Trebuchet MS Bold Italic", "trebucbi.ttf" },
        { "Trebuchet MS Italic", "trebucit.ttf" },
        { "Verdana", "verdana.ttf" },
        { "Verdana Bold", "verdanab.ttf" },
        { "Verdana Bold Italic", "verdanaz.ttf" },
        { "Verdana Italic", "verdanai.ttf" },
        { "Vrinda", "vrinda.ttf" },
        { "Vrinda Bold", "vrindab.ttf" },
        { "Webdings", "webdings.ttf" }
    };

    public FontService(ILogger<FontService>? logger = null){
        _logger = logger;
    }

    public List<string> ExtractFontsFromAss(string assContent, bool includeTypesettingFonts = true){
        if (string.IsNullOrWhiteSpace(assContent))
            return [];

        assContent = assContent.Replace("\r", "");
        var lines = assContent.Split('\n');
        var fonts = new List<string>();

        foreach (var line in lines){
            if (line.StartsWith("Style: ", StringComparison.OrdinalIgnoreCase)){
                var parts = line.Substring(7).Split(',');
                if (parts.Length > 1){
                    var fontName = parts[1].Trim();
                    fonts.Add(NormalizeFontKey(fontName));
                }
            }
        }

        if (includeTypesettingFonts){
            var fontMatches = Regex.Matches(assContent, @"\\fn([^\\}]+)");
            foreach (Match match in fontMatches){
                if (match.Groups.Count > 1){
                    var fontName = match.Groups[1].Value.Trim();
                    if (Regex.IsMatch(fontName, @"^\d+$"))
                        continue;
                    fonts.Add(NormalizeFontKey(fontName));
                }
            }
        }

        return fonts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<FontAttachment> ResolveFonts(List<string> fontNames, string? customFontsDir = null){
        var attachments = new List<FontAttachment>();
        var missing = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fontName in fontNames){
            var normalized = NormalizeFontKey(fontName);
            if (string.IsNullOrEmpty(normalized)) continue;

            var resolved = FindFontFile(normalized, customFontsDir);
            
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)){
                if (seenPaths.Add(resolved)){
                    attachments.Add(new FontAttachment{
                        Name = MakeUniqueAttachmentName(resolved, attachments),
                        Path = resolved,
                        Mime = GetFontMimeType(resolved)
                    });
                }
            } else{
                missing.Add(normalized);
            }
        }

        if (missing.Count > 0){
            _logger?.LogWarning("Missing fonts: {Fonts}", string.Join(", ", missing));
        }

        return attachments;
    }

    public string GetFontMimeType(string fontPath){
        var ext = Path.GetExtension(fontPath).ToLowerInvariant();
        return ext switch{
            ".otf" => "application/vnd.ms-opentype",
            ".ttf" or ".ttc" or ".otc" => "application/x-truetype-font",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private string? FindFontFile(string fontName, string? customFontsDir){
        // Try known mappings first
        if (KnownFonts.TryGetValue(fontName, out var knownFile)){
            foreach (var dir in GetFontSearchDirectories(customFontsDir)){
                var path = Path.Combine(dir, knownFile);
                if (File.Exists(path)) return path;
            }
        }

        // Search in font directories for matching filename
        var searchName = NormalizeFontKey(fontName).Replace(" ", "").ToLowerInvariant();
        foreach (var dir in GetFontSearchDirectories(customFontsDir)){
            if (!Directory.Exists(dir)) continue;
            
            try{
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)){
                    var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                    if (fileName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith(searchName, StringComparison.OrdinalIgnoreCase)){
                        return file;
                    }
                }
            } catch (Exception ex){
                _logger?.LogDebug(ex, "Failed to search font directory {Dir}", dir);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetFontSearchDirectories(string? customFontsDir){
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        
        if (!string.IsNullOrEmpty(customFontsDir)){
            try{
                var fullPath = Path.GetFullPath(customFontsDir);
                if (Directory.Exists(fullPath) && seen.Add(fullPath))
                    dirs.Add(fullPath);
            } catch { }
        }

        // System font directories
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)){
            var winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(winFonts) && seen.Add(winFonts))
                dirs.Add(winFonts);
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)){
            AddUnixDir("/System/Library/Fonts", seen, dirs);
            AddUnixDir("/Library/Fonts", seen, dirs);
            var userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
            AddUnixDir(userFonts, seen, dirs);
        } else{
            AddUnixDir("/usr/share/fonts", seen, dirs);
            AddUnixDir("/usr/local/share/fonts", seen, dirs);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddUnixDir(Path.Combine(home, ".fonts"), seen, dirs);
            AddUnixDir(Path.Combine(home, ".local", "share", "fonts"), seen, dirs);
        }

        return dirs;
    }

    private static void AddUnixDir(string dir, HashSet<string> seen, List<string> dirs){
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && seen.Add(dir))
            dirs.Add(dir);
    }

    private static string MakeUniqueAttachmentName(string path, List<FontAttachment> existing){
        var baseName = Path.GetFileName(path);
        if (existing.All(e => !baseName.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path)))
            .Substring(0, 8)
            .ToLowerInvariant();
        return $"{hash}-{baseName}";
    }

    private static string NormalizeFontKey(string s){
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        s = s.Trim().Trim('"');
        if (s.StartsWith("@"))
            s = s.Substring(1);

        // Convert camel case (TimesNewRoman → Times New Roman)
        s = Regex.Replace(s, @"(?<=[a-z])([A-Z])", " $1");
        s = s.Replace('_', ' ').Replace('-', ' ');
        s = Regex.Replace(s, @"MT$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }
}