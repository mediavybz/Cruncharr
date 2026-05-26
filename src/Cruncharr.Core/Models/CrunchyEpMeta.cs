using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Newtonsoft.Json;

namespace Cruncharr.Core.Models;

public class CrunchyEpMeta{
    public List<CrunchyEpMetaData> Data{ get; set; } = [];

    public string? SeriesTitle{ get; set; }
    public string? SeasonTitle{ get; set; }
    public string? EpisodeNumber{ get; set; }
    public string? EpisodeTitle{ get; set; }
    public string? Description{ get; set; }
    public string? EpisodeId{ get; set; }
    public string? SeasonId{ get; set; }
    public string? Season{ get; set; }
    public string? SeriesId{ get; set; }
    public string? AbsolutEpisodeNumberE{ get; set; }
    public string? Image{ get; set; }
    public string? ImageBig{ get; set; }
    public DownloadProgress DownloadProgress{ get; set; } = new();

    public List<string>? SelectedDubs{ get; set; }

    public string Hslang{ get; set; } = "none";

    public List<string>? AvailableSubs{ get; set; }

    public string? DownloadPath{ get; set; }
    public string? VideoQuality{ get; set; }
    public List<string> DownloadSubs{ get; set; } = [];
    public string? TempFileSuffix{ get; set; }
    public bool Music{ get; set; }

    public string Resolution{ get; set; } = "";
    
    public string AvailableQualities{ get; set; } = "";

    public List<string> DownloadedFiles{ get; set; } = [];

    public bool OnlySubs{ get; set; }

    public DownloadConfig? DownloadSettings;

    public bool HighlightAllAvailable{ get; set; }
    
    [JsonIgnore]
    public CancellationTokenSource Cts { get; private set; } = new();

    public void RenewCancellationToken(){
        if (!Cts.IsCancellationRequested){
            return;
        }

        Cts.Dispose();
        Cts = new CancellationTokenSource();
    }

    public void CancelDownload(){
        if (Cts.IsCancellationRequested){
            return;
        }

        Cts.Cancel();
    }
}

public class CrunchyEpMetaData{
    public string MediaId{ get; set; } = "";
    public LanguageItem? Lang{ get; set; }
    public string? Playback{ get; set; }
    public List<EpisodeVersion>? Versions{ get; set; }
    public bool IsSubbed{ get; set; }
    public bool IsDubbed{ get; set; }

    public bool IsAudioRoleDescription{ get; set; }
    
    public (string? seasonID, string? guid) GetOriginalIds(){
        var version = Versions?.FirstOrDefault(a => a.Original);
        if (version != null && !string.IsNullOrEmpty(version.Guid) && !string.IsNullOrEmpty(version.SeasonGuid)){
            return (version.SeasonGuid, version.Guid);
        }

        return (null, null);
    }
}

public class CrunchyRollEpisodeData{
    public string Key{ get; set; } = "";
    public EpisodeAndLanguage EpisodeAndLanguages{ get; set; } = new();
}

public class EpisodeAndLanguage{
    public List<EpisodeVariant> Variants{ get; set; } = new();
    
    public bool AddUnique(CrEpisodeDetail item, LanguageItem lang){
        if (Variants.Any(v => v.Lang.CrLocale == lang.CrLocale))
            return false;

        Variants.Add(new EpisodeVariant(item, lang));
        return true;
    }
}

public readonly record struct EpisodeVariant(CrEpisodeDetail Item, LanguageItem Lang);

public class CrunchyMultiDownload{
    public List<string> DubLang{ get; set; }
    public bool? AllEpisodes{ get; set; }
    public bool? But{ get; set; }
    public List<string>? E{ get; set; }
    public string? S{ get; set; }

    public CrunchyMultiDownload(List<string> dubLang, bool? all = null, bool? but = null, List<string>? e = null, string? s = null){
        DubLang = dubLang;
        AllEpisodes = all;
        But = but;
        E = e;
        S = s;
    }
}

public class CrunchySeriesList{
    public List<EpisodeDisplay> List{ get; set; } = []; 
    public Dictionary<string, EpisodeAndLanguage> Data{ get; set; } = [];
}

public class EpisodeDisplay{
    public string E{ get; set; } = "";
    public List<string> Lang{ get; set; } = [];
    public string Name{ get; set; } = "";
    public string Season{ get; set; } = "";
    public string SeasonTitle{ get; set; } = "";
    public string SeriesTitle{ get; set; } = "";
    public string EpisodeNum{ get; set; } = "";
    public string Id{ get; set; } = "";
    public string Img{ get; set; } = "";
    public string Description{ get; set; } = "";
    public string Time{ get; set; } = "";
    public EpisodeType EpisodeType{ get; set; } = EpisodeType.Unknown;
}

public enum StreamingService{
    Crunchyroll
}
