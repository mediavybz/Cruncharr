using Newtonsoft.Json;

namespace Cruncharr.Core.Models;

public class CrunchyChapters{
    public List<CrunchyChapter> Chapters { get; set; } = [];
    public DateTime lastUpdate { get; set; }
    public string? mediaId { get; set; }
}

public class CrunchyChapter{
    public string approverId { get; set; } = "";
    public string distributionNumber { get; set; } = "";
    public double? end { get; set; }
    public double? start { get; set; }
    public string title { get; set; } = "";
    public string seriesId { get; set; } = "";
    [JsonProperty("new")]
    public string? New { get; set; }
    public string type { get; set; } = "";
}

