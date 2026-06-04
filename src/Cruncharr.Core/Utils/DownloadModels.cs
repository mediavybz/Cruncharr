namespace Cruncharr.Core.Utils;

public enum DownloadMediaType
{
    Video,
    Audio,
    Subtitle,
    Chapters,
    Font,
    SyncVideo
}

public class SxItem
{
    public string Path { get; set; } = "";
    public LanguageItem? Lang { get; set; }
    public DownloadMediaType Type { get; set; }
    public int? Delay { get; set; }
}

public class DownloadedMedia : SxItem
{
    public DownloadedMedia? RelatedVideoDownloadMedia { get; set; }
    public bool IsSigns { get; set; }
    public bool IsCC { get; set; }
}

public class DownloadResponse
{
    public List<DownloadedMedia> Data { get; set; } = new();
    public bool Error { get; set; }
    public string FileName { get; set; } = "./unknown";
    public string ErrorText { get; set; } = "";
}
