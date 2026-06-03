using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Utils.Muxing.Structs;

public class SubtitleInput{
    public LanguageItem Language{ get; set; } = new();
    public string File{ get; set; } = "";
    public bool ClosedCaption{ get; set; }
    public bool Signs{ get; set; }
    public int? Delay{ get; set; }

    public DownloadedMedia? RelatedVideoDownloadMedia;
}
