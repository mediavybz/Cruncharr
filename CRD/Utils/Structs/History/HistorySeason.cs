using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CRD.Utils.Files;
using Newtonsoft.Json;

namespace CRD.Utils.Structs.History;

public class HistorySeason : INotifyPropertyChanged{
    [JsonProperty("season_title")]
    public string? SeasonTitle{ get; set; }

    [JsonProperty("season_id")]
    public string? SeasonId{ get; set; }

    [JsonProperty("season_cr_season_number")]
    public string? SeasonNum{ get; set; }

    [JsonProperty("season_special_season")]
    public bool SpecialSeason{ get; set; }

    [JsonProperty("season_downloaded_episodes")]
    public int DownloadedEpisodes{ get; set; }

    [JsonProperty("season_episode_list")]
    public required List<HistoryEpisode> EpisodesList{ get; set; }

    [JsonProperty("series_download_path")]
    public string? SeasonDownloadPath{ get; set; }

    [JsonProperty("history_season_video_quality_override")]
    public string HistorySeasonVideoQualityOverride{ get; set; } = "";

    [JsonProperty("history_season_soft_subs_override")]
    public ObservableCollection<string> HistorySeasonSoftSubsOverride{ get; set; } =[];

    [JsonProperty("history_season_dub_lang_override")]
    public ObservableCollection<string> HistorySeasonDubLangOverride{ get; set; } =[];

    [JsonIgnore]
    public string CombinedProperty => SpecialSeason ? $"Specials {SeasonNum}" : $"Season {SeasonNum}";

    [JsonIgnore]
    public bool IsExpanded{ get; set; }

    [JsonIgnore]
    public StreamingService StreamingService{ get; set; } = StreamingService.Unknown;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName){
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #region Settings Override

    [JsonIgnore]
    public string _selectedVideoQualityItem = "";

    [JsonIgnore]
    private bool Loading;

    [JsonIgnore]
    public string SelectedVideoQualityItem{
        get => _selectedVideoQualityItem;
        set{
            _selectedVideoQualityItem = value ?? "";

            HistorySeasonVideoQualityOverride = _selectedVideoQualityItem;
            OnPropertyChanged(nameof(SelectedVideoQualityItem));
            if (!Loading){
                CfgManager.UpdateHistoryFile();
            }
        }
    }

    [JsonIgnore]
    public string SelectedSubs{ get; set; } = "";

    [JsonIgnore]
    public string SelectedDubs{ get; set; } = "";

    [JsonIgnore]
    public ObservableCollection<string> SelectedSubLang{ get; set; } = new();

    [JsonIgnore]
    public ObservableCollection<string> SelectedDubLang{ get; set; } = new();

    [JsonIgnore]
    public ObservableCollection<string> DubLangList => HistoryOverrideOptions.GetDubLangList(StreamingService);

    [JsonIgnore]
    public ObservableCollection<string> SubLangList => HistoryOverrideOptions.GetSubLangList(StreamingService);

    [JsonIgnore]
    public ObservableCollection<string> VideoQualityList => HistoryOverrideOptions.GetVideoQualityList(StreamingService);

    private void UpdateSubAndDubString(){
        HistorySeasonSoftSubsOverride.Clear();
        HistorySeasonDubLangOverride.Clear();

        if (SelectedSubLang.Count != 0){
            for (var i = 0; i < SelectedSubLang.Count; i++){
                HistorySeasonSoftSubsOverride.Add(SelectedSubLang[i]);
            }
        }

        if (SelectedDubLang.Count != 0){
            for (var i = 0; i < SelectedDubLang.Count; i++){
                HistorySeasonDubLangOverride.Add(SelectedDubLang[i]);
            }
        }

        SelectedDubs = string.Join(", ", HistorySeasonDubLangOverride) ?? "";
        SelectedSubs = string.Join(", ", HistorySeasonSoftSubsOverride) ?? "";


        OnPropertyChanged(nameof(SelectedSubs));
        OnPropertyChanged(nameof(SelectedDubs));

        CfgManager.UpdateHistoryFile();
    }

    private void Changes(object? sender, NotifyCollectionChangedEventArgs e){
        UpdateSubAndDubString();
    }

    public void Init(){
        SelectedSubLang.CollectionChanged -= Changes;
        SelectedDubLang.CollectionChanged -= Changes;

        Loading = true;
        SelectedVideoQualityItem = VideoQualityList.FirstOrDefault(HistorySeasonVideoQualityOverride.Equals) ?? "";

        var softSubLang = SubLangList.Where(HistorySeasonSoftSubsOverride.Contains).ToList();
        var dubLang = DubLangList.Where(HistorySeasonDubLangOverride.Contains).ToList();

        SelectedSubLang.Clear();
        foreach (var listBoxItem in softSubLang){
            SelectedSubLang.Add(listBoxItem);
        }

        SelectedDubLang.Clear();
        foreach (var listBoxItem in dubLang){
            SelectedDubLang.Add(listBoxItem);
        }

        SelectedDubs = string.Join(", ", HistorySeasonDubLangOverride) ?? "";
        SelectedSubs = string.Join(", ", HistorySeasonSoftSubsOverride) ?? "";

        SelectedSubLang.CollectionChanged += Changes;
        SelectedDubLang.CollectionChanged += Changes;
        Loading = false;
    }

    #endregion

    public void UpdateDownloaded(string? EpisodeId){
        if (!string.IsNullOrEmpty(EpisodeId)){
            var episode = EpisodesList.First(e => e.EpisodeId == EpisodeId);
            episode.ToggleWasDownloaded();
        }

        DownloadedEpisodes = EpisodesList.FindAll(e => e.WasDownloaded).Count;
        OnPropertyChanged(nameof(DownloadedEpisodes));
        CfgManager.UpdateHistoryFile();
    }

    public void UpdateDownloaded(){
        DownloadedEpisodes = EpisodesList.FindAll(e => e.WasDownloaded).Count;
        OnPropertyChanged(nameof(DownloadedEpisodes));
        CfgManager.UpdateHistoryFile();
    }

    public void UpdateDownloadedSilent(){
        DownloadedEpisodes = EpisodesList.FindAll(e => e.WasDownloaded).Count;
        OnPropertyChanged(nameof(DownloadedEpisodes));
    }
}
