using System.Linq;
using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Services;

public static class QualitySelector{
    // Ported from CRD.Utils.Helpers
    public static int ToKbps(int bps) => (int)Math.Round(bps / 1000.0);
    
    public static int SnapToAudioBucket(int kbps){
        int[] buckets = { 64, 96, 128, 192, 256 };
        return buckets.OrderBy(b => Math.Abs(b - kbps)).First();
    }
    
    public static int WidthBucket(int width, int height){
        int expected = (int)Math.Round(height * 16 / 9.0);
        int tol = Math.Max(8, (int)(expected * 0.02)); // ~2% or >=8 px
        return Math.Abs(width - expected) <= tol ? expected : width;
    }
    
    /// <summary>
    /// Selects the best video track based on quality preference.
    /// Ported from CrunchyrollManager.cs quality selection logic.
    /// </summary>
    public static DashTrack? SelectVideoTrack(List<DashTrack> videos, string qualityPreference){
        if (videos.Count == 0) return null;
        
        // Deduplicate by (height, widthBucket) and pick highest bandwidth within each group
        var deduped = videos
            .GroupBy(v => new{ v.Height, WB = WidthBucket(v.Width ?? 0, v.Height ?? 0) })
            .Select(g => g.OrderByDescending(v => v.Bandwidth).First())
            .OrderBy(v => v.Height)
            .ThenBy(v => v.Bandwidth)
            .ToList();
        
        if (string.IsNullOrWhiteSpace(qualityPreference)){
            qualityPreference = "best";
        }
        
        int chosenIndex;
        if (qualityPreference == "best"){
            chosenIndex = deduped.Count;
        } else if (qualityPreference == "worst"){
            chosenIndex = 1;
        } else{
            // Try to match specific height like "1080p" or "1080"
            var heightStr = qualityPreference.Replace("p", "").Trim();
            if (int.TryParse(heightStr, out var targetHeight)){
                var matchIndex = deduped.FindIndex(v => v.Height == targetHeight);
                if (matchIndex >= 0){
                    chosenIndex = matchIndex + 1;
                } else{
                    chosenIndex = deduped.Count;
                }
            } else{
                chosenIndex = deduped.Count;
            }
        }
        
        if (chosenIndex > deduped.Count){
            chosenIndex = deduped.Count;
        }
        
        return deduped[chosenIndex - 1];
    }
    
    /// <summary>
    /// Selects the best audio track. Currently picks highest bandwidth.
    /// Ported from CrunchyrollManager.cs audio selection logic.
    /// </summary>
    public static DashTrack? SelectAudioTrack(List<DashTrack> audioTracks){
        if (audioTracks.Count == 0) return null;
        
        return audioTracks
            .OrderByDescending(a => a.Bandwidth)
            .FirstOrDefault();
    }
    
    /// <summary>
    /// Selects audio tracks matching the specified languages.
    /// Returns one track per language (highest bandwidth per language).
    /// </summary>
    public static List<(DashTrack Track, string Language)> SelectAudioTracks(List<DashTrack> audioTracks, List<string> languages){
        if (audioTracks.Count == 0 || languages.Count == 0) return [];
        
        var result = new List<(DashTrack, string)>();
        var normalizedLangs = languages.Select(l => l.ToLowerInvariant().Replace("-", "")).ToList();
        
        // Group by language and pick best per language
        var byLanguage = audioTracks
            .GroupBy(a => a.Language?.ToLowerInvariant().Replace("-", "") ?? "unknown")
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Bandwidth).First());
        
        foreach (var lang in normalizedLangs){
            if (byLanguage.TryGetValue(lang, out var track)){
                var originalLang = languages[normalizedLangs.IndexOf(lang)];
                result.Add((track, originalLang));
            }
        }
        
        return result;
    }
}
