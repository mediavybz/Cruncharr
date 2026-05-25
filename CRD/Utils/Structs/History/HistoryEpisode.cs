using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CRD.Downloader;
using CRD.Downloader.Crunchyroll;
using CRD.Utils.Files;
using CRD.Utils.Sonarr.Models;
using Newtonsoft.Json;

namespace CRD.Utils.Structs.History;

public class HistoryEpisode : INotifyPropertyChanged{
    [JsonProperty("episode_title")]
    public string? EpisodeTitle{ get; set; }

    [JsonProperty("episode_id")]
    public string? EpisodeId{ get; set; }

    [JsonProperty("episode_cr_episode_number")]
    public string? Episode{ get; set; }

    [JsonProperty("episode_cr_episode_description")]
    public string? EpisodeDescription{ get; set; }

    [JsonProperty("episode_cr_season_number")]
    public string? EpisodeSeasonNum{ get; set; }

    [JsonProperty("episode_cr_premium_air_date")]
    public DateTime? EpisodeCrPremiumAirDate{ get; set; }

    [JsonProperty("episode_was_downloaded")]
    public bool WasDownloaded{ get; set; }

    [JsonProperty("episode_downloaded_dub_lang")]
    public List<string> DownloadedDubLang{ get; set; } = [];

    [JsonProperty("episode_downloaded_soft_subs")]
    public List<string> DownloadedSoftSubs{ get; set; } = [];

    [JsonProperty("episode_tracked_series_release_notified")]
    public bool TrackedSeriesReleaseNotified{ get; set; }

    [JsonProperty("episode_special_episode")]
    public bool SpecialEpisode{ get; set; }

    [JsonProperty("episode_available_on_streaming_service")]
    public bool IsEpisodeAvailableOnStreamingService{ get; set; }

    [JsonProperty("episode_type")]
    public EpisodeType EpisodeType{ get; set; } = EpisodeType.Unknown;

    [JsonProperty("episode_series_type")]
    public SeriesType EpisodeSeriesType{ get; set; } = SeriesType.Unknown;

    [JsonProperty("episode_thumbnail_url")]
    public string? ThumbnailImageUrl{ get; set; }

    [JsonProperty("sonarr_episode_id")]
    public string? SonarrEpisodeId{ get; set; }

    [JsonProperty("sonarr_has_file")]
    public bool SonarrHasFile{ get; set; }

    [JsonProperty("sonarr_is_monitored")]
    public bool SonarrIsMonitored{ get; set; }

    [JsonProperty("sonarr_episode_number")]
    public string? SonarrEpisodeNumber{ get; set; }

    [JsonProperty("sonarr_season_number")]
    public string? SonarrSeasonNumber{ get; set; }

    [JsonProperty("sonarr_absolut_number")]
    public string? SonarrAbsolutNumber{ get; set; }

    [JsonIgnore]
    public string SonarrSeasonEpisodeText{
        get{
            if (int.TryParse(SonarrSeasonNumber, out int season) &&
                int.TryParse(SonarrEpisodeNumber, out int episode)){
                return $"S{season:D2}E{episode:D2}";
            }

            return $"S{SonarrSeasonNumber}E{SonarrEpisodeNumber}";
        }
    }

    [JsonProperty("history_episode_available_soft_subs")]
    public List<string> HistoryEpisodeAvailableSoftSubs{ get; set; } =[];

    [JsonProperty("history_episode_available_dub_lang")]
    public List<string> HistoryEpisodeAvailableDubLang{ get; set; } =[];
    
    [JsonIgnore]
    public Bitmap? ThumbnailImage{ get; set; }

    [JsonIgnore]
    public bool IsImageLoaded{ get; private set; }

    public async Task LoadImage(){
        if (IsImageLoaded || string.IsNullOrEmpty(ThumbnailImageUrl))
            return;

        try{
            ThumbnailImage = await Helpers.LoadImage(ThumbnailImageUrl);
            IsImageLoaded = true;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailImage)));
        } catch (Exception ex){
            Console.Error.WriteLine("Failed to load image: " + ex.Message);
        }
    }

    [JsonIgnore]
    public string ReleaseDateFormated{
        get{
            if (!EpisodeCrPremiumAirDate.HasValue ||
                EpisodeCrPremiumAirDate.Value == DateTime.MinValue ||
                EpisodeCrPremiumAirDate.Value.Date == new DateTime(1970, 1, 1))
                return string.Empty;


            var cultureInfo = System.Globalization.CultureInfo.InvariantCulture;
            string monthAbbreviation = cultureInfo.DateTimeFormat.GetAbbreviatedMonthName(EpisodeCrPremiumAirDate.Value.Month);

            return string.Format("{0:00}.{1}.{2}", EpisodeCrPremiumAirDate.Value.Day, monthAbbreviation, EpisodeCrPremiumAirDate.Value.Year);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPartiallyDownloaded(IEnumerable<string> requestedDubs, IEnumerable<string> requestedSoftSubs){
        return WasDownloaded &&
               (IsMissingAny(requestedDubs, DownloadedDubLang) ||
                IsMissingAny(requestedSoftSubs, DownloadedSoftSubs));
    }

    public bool HasAvailableMissingDownloadedMedia(IEnumerable<string> requestedDubs, IEnumerable<string> requestedSoftSubs){
        return WasDownloaded &&
               (HasMissingAvailableItem(requestedDubs, DownloadedDubLang, HistoryEpisodeAvailableDubLang) ||
                HasMissingAvailableItem(requestedSoftSubs, DownloadedSoftSubs, HistoryEpisodeAvailableSoftSubs));
    }

    private static bool IsMissingAny(IEnumerable<string> requested, List<string> downloaded){
        var requestedList = requested
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedList.Count == 0 || downloaded.Count == 0){
            return false;
        }

        var downloadedSet = downloaded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requestedList.Any(item => !downloadedSet.Contains(item));
    }

    private static bool HasMissingAvailableItem(IEnumerable<string> requested, List<string> downloaded, List<string> available){
        var requestedList = requested
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedList.Count == 0 || downloaded.Count == 0 || available.Count == 0){
            return false;
        }

        var downloadedSet = downloaded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableSet = available.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requestedList.Any(item => !downloadedSet.Contains(item) && availableSet.Contains(item));
    }

    private void NotifyDownloadStateChanged(){
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WasDownloaded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadedDubLang)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadedSoftSubs)));
    }

    private void NotifyAvailableMediaChanged(){
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryEpisodeAvailableDubLang)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryEpisodeAvailableSoftSubs)));
    }

    public void SetDownloadedMedia(List<string> downloadedDubs, List<string> downloadedSoftSubs){
        WasDownloaded = true;
        DownloadedDubLang = downloadedDubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DownloadedSoftSubs = downloadedSoftSubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        NotifyDownloadStateChanged();
    }

    public void UpdateAvailableMedia(List<string> availableDubs, List<string> availableSoftSubs){
        HistoryEpisodeAvailableDubLang = availableDubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        HistoryEpisodeAvailableSoftSubs = availableSoftSubs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        NotifyAvailableMediaChanged();
    }

    public void ToggleWasDownloaded(){
        WasDownloaded = !WasDownloaded;
        NotifyDownloadStateChanged();
    }

    public void ToggleWasDownloadedSeries(HistorySeries? series){
        WasDownloaded = !WasDownloaded;
        NotifyDownloadStateChanged();

        if (series?.Seasons != null){
            foreach (var historySeason in series.Seasons){
                historySeason.UpdateDownloadedSilent();
            }

            series.UpdateNewEpisodes();
        }

        CfgManager.UpdateHistoryFile();
    }

    public async Task DownloadEpisodeDefault(){
        await DownloadEpisode(EpisodeDownloadMode.Default,"",false);
    }

    public async Task DownloadEpisode(EpisodeDownloadMode episodeDownloadMode, string overrideDownloadPath,bool chekQueueForId){
        
        if (chekQueueForId && QueueManager.Instance.Queue.Any(item => item.Data.Any(epmeta => epmeta.MediaId == EpisodeId))){
            Console.Error.WriteLine($"Episode already in queue! E{EpisodeSeasonNum}-{EpisodeTitle}");
            return;
        }
        
        switch (EpisodeType){
            case EpisodeType.MusicVideo:
                await CrunchyrollManager.Instance.CrQueue.CrAddMusicVideoToQueue(EpisodeId ?? string.Empty, overrideDownloadPath);
                break;
            case EpisodeType.Concert:
                await CrunchyrollManager.Instance.CrQueue.CrAddConcertToQueue(EpisodeId ?? string.Empty, overrideDownloadPath);
                break;
            case EpisodeType.Episode:
            case EpisodeType.Unknown:
            default:
                await CrunchyrollManager.Instance.CrQueue.CrAddEpisodeToQueue(EpisodeId ?? string.Empty,
                    string.IsNullOrEmpty(CrunchyrollManager.Instance.CrunOptions.HistoryLang) ? CrunchyrollManager.Instance.DefaultLocale : CrunchyrollManager.Instance.CrunOptions.HistoryLang,
                    CrunchyrollManager.Instance.CrunOptions.DubLang, false, episodeDownloadMode);
                break;
        }
    }

    public void AssignSonarrEpisodeData(SonarrEpisode episode){
        SonarrEpisodeId = episode.Id.ToString();
        SonarrEpisodeNumber = episode.EpisodeNumber.ToString();
        SonarrHasFile = episode.HasFile;
        SonarrIsMonitored = episode.Monitored;
        SonarrAbsolutNumber = episode.AbsoluteEpisodeNumber.ToString();
        SonarrSeasonNumber = episode.SeasonNumber.ToString();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SonarrSeasonEpisodeText)));
    }

    public void ClearSonarrEpisodeData(){
        SonarrEpisodeId = null;
        SonarrEpisodeNumber = null;
        SonarrHasFile = false;
        SonarrIsMonitored = false;
        SonarrAbsolutNumber = null;
        SonarrSeasonNumber = null;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SonarrSeasonEpisodeText)));
    }
}
