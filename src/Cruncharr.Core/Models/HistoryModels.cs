using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Models;

public class HistorySeries{
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? SeriesDescription { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public List<HistorySeason> Seasons { get; set; } = [];
    public DateTime? HistorySeriesAddDate { get; set; }
    public SeriesType SeriesType { get; set; } = SeriesType.Unknown;
    public string SeriesStreamingService { get; set; } = "Crunchyroll";
    public bool HasNewEpisodes { get; set; }
    public int DownloadedEpisodes { get; set; }
    public int TotalEpisodes { get; set; }
    
    [JsonIgnore]
    public List<string> HistorySeriesAvailableDubLang { get; set; } = [];
    
    [JsonIgnore]
    public List<string> HistorySeriesAvailableSoftSubs { get; set; } = [];
    
    // Override fields for per-series settings
    public List<string> HistorySeriesDubLangOverride { get; set; } = [];
    public List<string> HistorySeriesSoftSubsOverride { get; set; } = [];
    public string HistorySeriesVideoQualityOverride { get; set; } = "";
    public string SeriesDownloadPath { get; set; } = "";
    
    // Sonarr integration fields
    public string? SonarrSeriesId { get; set; }
    public string? SonarrTvDbId { get; set; }
    public string? SonarrSlugTitle { get; set; }
    public string? SonarrNextAirDate { get; set; }
    
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
    
    // Override fields for per-season settings
    public List<string> HistorySeasonDubLangOverride { get; set; } = [];
    public List<string> HistorySeasonSoftSubsOverride { get; set; } = [];
    public string HistorySeasonVideoQualityOverride { get; set; } = "";
    public string SeasonDownloadPath { get; set; } = "";
    
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
    public EpisodeType EpisodeType { get; set; } = EpisodeType.Episode;
    public SeriesType EpisodeSeriesType { get; set; } = SeriesType.Unknown;
    public bool IsEpisodeAvailableOnStreamingService { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public bool WasDownloaded { get; set; }
    
    // Sonarr integration fields
    public string? SonarrEpisodeId { get; set; }
    public string? SonarrEpisodeNumber { get; set; }
    public bool SonarrHasFile { get; set; }
    public bool SonarrIsMonitored { get; set; }
    public string? SonarrAbsolutNumber { get; set; }
    public string? SonarrSeasonNumber { get; set; }
    
    [JsonIgnore]
    public string SonarrSeasonEpisodeText {
        get {
            if (int.TryParse(SonarrSeasonNumber, out int season) &&
                int.TryParse(SonarrEpisodeNumber, out int episode)) {
                return $"S{season:D2}E{episode:D2}";
            }
            return $"S{SonarrSeasonNumber}E{SonarrEpisodeNumber}";
        }
    }
    
    public void AssignSonarrEpisodeData(SonarrEpisode episode) {
        SonarrEpisodeId = episode.Id.ToString();
        SonarrEpisodeNumber = episode.EpisodeNumber.ToString();
        SonarrHasFile = episode.HasFile;
        SonarrIsMonitored = episode.Monitored;
        SonarrAbsolutNumber = episode.AbsoluteEpisodeNumber.ToString();
        SonarrSeasonNumber = episode.SeasonNumber.ToString();
    }
    
    public void ClearSonarrEpisodeData() {
        SonarrEpisodeId = null;
        SonarrEpisodeNumber = null;
        SonarrHasFile = false;
        SonarrIsMonitored = false;
        SonarrAbsolutNumber = null;
        SonarrSeasonNumber = null;
    }
    
    public void UpdateAvailableMedia(List<string> availableDubs, List<string> availableSubs){
        HistoryEpisodeAvailableDubLang = availableDubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        HistoryEpisodeAvailableSoftSubs = availableSubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    
    public void SetDownloadedMedia(List<string> downloadedDubs, List<string> downloadedSubs){
        DownloadedDubLang = downloadedDubs;
        DownloadedSoftSubs = downloadedSubs;
    }
}

public enum EpisodeType{
    Episode,
    Concert,
    MusicVideo
}

public enum SeriesType{
    Unknown,
    Series,
    Movie,
    Artist
}

public enum SortingType{
    SeriesTitle,
    NextAirDate,
    HistorySeriesAddDate
}

public class HistoryPageProperties{
	public SortingType SelectedSorting { get; set; } = SortingType.SeriesTitle;
	public bool Ascending { get; set; } = false;
}

public class SeasonsPageProperties{
	public SortingType SelectedSorting { get; set; } = SortingType.SeriesTitle;
	public bool Ascending { get; set; } = false;
}

public class SeriesDataCache{
    public string? SeriesDescription { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public List<string> HistorySeriesAvailableDubLang { get; set; } = [];
    public List<string> HistorySeriesAvailableSoftSubs { get; set; } = [];
}
