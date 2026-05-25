using System.Collections.Generic;
using Newtonsoft.Json;

namespace Cruncharr.Core.Utils;

public class SubtitleInfo{
    public string? Format{ get; set; }
    public Locale? Locale{ get; set; }
    public string? Url{ get; set; }
}

public class Subtitles : Dictionary<string, SubtitleInfo>;

public class Caption{
    public Locale? Language{ get; set; }
    public Locale? Locale{ get; set; }
    public string? Url{ get; set; }
    public string? Format{ get; set; }
}

public class PlaybackVersion{
    [JsonProperty("audio_locale")]
    public Locale AudioLocale{ get; set; }

    public string? Guid{ get; set; }

    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly{ get; set; }

    [JsonProperty("media_guid")]
    public string? MediaGuid{ get; set; }

    public bool Original{ get; set; }

    [JsonProperty("season_guid")]
    public string? SeasonGuid{ get; set; }

    public string? Variant{ get; set; }
}
