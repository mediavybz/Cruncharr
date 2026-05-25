using System.Collections.ObjectModel;
using System.Linq;

namespace CRD.Utils.Structs.History;

public static class HistoryOverrideOptions{
    public static ObservableCollection<string> GetVideoQualityList(StreamingService streamingService) =>
        streamingService switch{
            StreamingService.Crunchyroll => CrunchyrollVideoQualityList,
            _ => CrunchyrollVideoQualityList,
        };

    public static ObservableCollection<string> GetDubLangList(StreamingService streamingService) =>
        streamingService switch{
            StreamingService.Crunchyroll => CrunchyrollDubLangList,
            _ => CrunchyrollDubLangList,
        };

    public static ObservableCollection<string> GetSubLangList(StreamingService streamingService) =>
        streamingService switch{
            StreamingService.Crunchyroll => CrunchyrollSubLangList,
            _ => CrunchyrollSubLangList,
        };

    private static ObservableCollection<string> CrunchyrollVideoQualityList{ get; } = new(){
        "best",
        "1080p",
        "720p",
        "480p",
        "360p",
        "240p",
        "worst",
    };

    private static ObservableCollection<string> CrunchyrollDubLangList{ get; } = new(
        Languages.languages.Select(languageItem => languageItem.CrLocale)
    );

    private static ObservableCollection<string> CrunchyrollSubLangList{ get; } = new(
        new[]{ "all", "none" }.Concat(Languages.languages.Select(languageItem => languageItem.CrLocale))
    );
}
