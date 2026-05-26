using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cruncharr.Core.Models;

namespace Cruncharr.Core.Utils;

public static class DownloadQueueItemFactory{
    private static readonly Regex DubSuffix = new(@"\(\w+ Dub\)", RegexOptions.Compiled);

    public static bool HasDubSuffix(string? s)
        => !string.IsNullOrWhiteSpace(s) && DubSuffix.IsMatch(s);

    public static string StripDubSuffix(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : DubSuffix.Replace(s, "").TrimEnd();

    public static string CanonicalTitle(IEnumerable<string?> candidates){
        var noDub = candidates.FirstOrDefault(t => !HasDubSuffix(t));
        return !string.IsNullOrWhiteSpace(noDub)
            ? noDub!
            : StripDubSuffix(candidates.FirstOrDefault());
    }

    public static (string small, string big) GetThumbSmallBig(Dictionary<string, List<List<object>>>? images){
        var urls = new List<string>();
        if (images != null && images.ContainsKey("thumbnail")){
            var thumbList = images["thumbnail"];
            if (thumbList != null && thumbList.Count > 0){
                var firstRow = thumbList[0];
                if (firstRow != null && firstRow.Count > 0){
                    var small = ExtractImageSource(firstRow[0]) ?? "/notFound.jpg";
                    var big = firstRow.Count > 1 ? ExtractImageSource(firstRow[^1]) ?? small : small;
                    return (small, big);
                }
            }
        }
        return ("/notFound.jpg", "/notFound.jpg");
    }

    private static string? ExtractImageSource(object? imageObj){
        if (imageObj == null) return null;
        if (imageObj is string str) return str;
        if (imageObj is Newtonsoft.Json.Linq.JObject jObj){
            return jObj["source"]?.ToString();
        }
        try{
            var json = imageObj.ToString();
            if (!string.IsNullOrEmpty(json) && json.StartsWith("{")){
                var jObj2 = Newtonsoft.Json.Linq.JObject.Parse(json);
                return jObj2["source"]?.ToString();
            }
        } catch{
            // Ignore
        }
        return null;
    }

    public static CrunchyEpMeta CreateShell(
        StreamingService service,
        string? seriesTitle,
        string? seasonTitle,
        string? episodeNumber,
        string? episodeTitle,
        string? description,
        string? episodeId,
        string? seriesId,
        string? seasonId,
        string? season,
        string? absolutEpisodeNumberE,
        string? image,
        string? imageBig,
        string hslang,
        List<string>? availableSubs = null,
        List<string>? selectedDubs = null,
        bool music = false){
        return new CrunchyEpMeta(){
            SeriesTitle = seriesTitle,
            SeasonTitle = seasonTitle,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episodeTitle,
            Description = description,
            EpisodeId = episodeId,
            SeriesId = seriesId,
            SeasonId = seasonId,
            Season = season,
            AbsolutEpisodeNumberE = absolutEpisodeNumberE,
            Image = image,
            ImageBig = imageBig,
            Hslang = hslang,
            AvailableSubs = availableSubs,
            SelectedDubs = selectedDubs,
            Music = music
        };
    }

    public static CrunchyEpMetaData CreateVariant(
        string mediaId,
        LanguageItem? lang,
        string? playback,
        List<EpisodeVersion>? versions,
        bool isSubbed,
        bool isDubbed,
        bool isAudioRoleDescription = false){
        return new CrunchyEpMetaData{
            MediaId = mediaId,
            Lang = lang,
            Playback = playback,
            Versions = versions,
            IsSubbed = isSubbed,
            IsDubbed = isDubbed,
            IsAudioRoleDescription = isAudioRoleDescription
        };
    }
}
