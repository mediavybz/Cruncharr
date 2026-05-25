using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Cruncharr.Core.Models;

public class HistorySeries{
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? SeriesDescription { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public List<HistorySeason> Seasons { get; set; } = [];
    public DateTime? HistorySeriesAddDate { get; set; }
    public string SeriesType { get; set; } = "Unknown";
    public string SeriesStreamingService { get; set; } = "Crunchyroll";
    public bool HasNewEpisodes { get; set; }
    public int DownloadedEpisodes { get; set; }
    public int TotalEpisodes { get; set; }
    
    [JsonIgnore]
    public List<string> HistorySeriesAvailableDubLang { get; set; } = [];
    
    [JsonIgnore]
    public List<string> HistorySeriesAvailableSoftSubs { get; set; } = [];
    
    public void UpdateNewEpisodes(List<string>? selectedDubs = null, List<string>? selectedSubs = null){
        TotalEpisodes = Seasons.Sum(s => s.EpisodesList.Count);
        DownloadedEpisodes = Seasons.Sum(s => s.EpisodesList.Count(e => e.WasDownloaded));
        
        // Check for new episodes or episodes with newly available selected dubs/subs
        HasNewEpisodes = Seasons.Any(s => s.EpisodesList.Any(e => {
            if (!e.IsEpisodeAvailableOnStreamingService) return false;
            
            // Episode not downloaded at all
            if (!e.WasDownloaded) return true;
            
            // Episode downloaded but selected dubs/subs may be missing
            // Check if any selected dubs are newly available but not downloaded
            var missingDubs = selectedDubs
                .Where(dub => e.HistoryEpisodeAvailableDubLang.Contains(dub) && !e.DownloadedDubLang.Contains(dub))
                .Any();
            
            var missingSubs = selectedSubs
                .Where(sub => e.HistoryEpisodeAvailableSoftSubs.Contains(sub) && !e.DownloadedSoftSubs.Contains(sub))
                .Any();
            
            return missingDubs || missingSubs;
        }));
    }
}

public class HistorySeason{
    public string? SeasonId { get; set; }
    public string? SeasonTitle { get; set; }
    public string? SeasonNum { get; set; }
    public List<HistoryEpisode> EpisodesList { get; set; } = [];
    public bool SpecialSeason { get; set; }
    public int DownloadedEpisodes { get; set; }
    
    public void UpdateDownloaded(){
        DownloadedEpisodes = EpisodesList.Count(e => e.WasDownloaded);
    }
}

public class HistoryEpisode{
    public string? EpisodeTitle { get; set; }
    public string? EpisodeDescription { get; set; }
    public string? EpisodeId { get; set; }
    public string? Episode { get; set; }
    public string? EpisodeSeasonNum { get; set; }
    public bool SpecialEpisode { get; set; }
    public List<string> HistoryEpisodeAvailableDubLang { get; set; } = [];
    public List<string> HistoryEpisodeAvailableSoftSubs { get; set; } = [];
    
    // Track which dubs/subs were actually downloaded (for partial download detection)
    public List<string> DownloadedDubLang { get; set; } = [];
    public List<string> DownloadedSoftSubs { get; set; } = [];
    
    public DateTime? EpisodeCrPremiumAirDate { get; set; }
    public string EpisodeType { get; set; } = "Episode";
    public string EpisodeSeriesType { get; set; } = "Unknown";
    public bool IsEpisodeAvailableOnStreamingService { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public bool WasDownloaded { get; set; }
}

public class SeriesDataCache{
    public string? SeriesDescription { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public List<string> HistorySeriesAvailableDubLang { get; set; } = [];
    public List<string> HistorySeriesAvailableSoftSubs { get; set; } = [];
}
