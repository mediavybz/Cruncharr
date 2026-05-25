using System.Globalization;
using System.Text.Json;
using Cruncharr.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IHistoryService{
    Task AddAsync(DownloadHistory entry);
    Task<List<DownloadHistory>> GetAllAsync();
    Task<bool> IsDownloadedAsync(string episodeId, string audioLanguage);
    Task RemoveAsync(string episodeId);
    Task<List<DownloadHistory>> GetSeriesHistoryAsync(string seriesId);
    
    // New rich history methods
    Task<List<HistorySeries>> GetHistorySeriesAsync();
    Task UpdateWithSeasonDataAsync(List<EpisodeInfo> episodes);
    Task SetAsDownloadedAsync(string? seriesId, string? seasonId, string episodeId);
    Task<HistoryEpisode?> GetHistoryEpisodeAsync(string? seriesId, string? seasonId, string episodeId);
    Task RemoveUnavailableEpisodesAsync();
}

public class HistoryService : IHistoryService{
    private readonly string _historyPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<HistoryService>? _logger;
    private List<HistorySeries> _historyList = [];
    private bool _loaded = false;
    
    public HistoryService(string? historyPath = null, ILogger<HistoryService>? logger = null){
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "cruncharr",
            "history.json"
        );
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
    }
    
    public async Task AddAsync(DownloadHistory entry){
        await _lock.WaitAsync();
        try{
            var history = await LoadHistoryAsync();
            history.RemoveAll(h => h.EpisodeId == entry.EpisodeId && h.AudioLanguage == entry.AudioLanguage);
            history.Add(entry);
            await SaveHistoryAsync(history);
        } finally{
            _lock.Release();
        }
    }
    
    public async Task<List<DownloadHistory>> GetAllAsync(){
        await _lock.WaitAsync();
        try{
            return await LoadHistoryAsync();
        } finally{
            _lock.Release();
        }
    }
    
    public async Task<bool> IsDownloadedAsync(string episodeId, string audioLanguage){
        var history = await GetAllAsync();
        return history.Any(h => h.EpisodeId == episodeId && h.AudioLanguage == audioLanguage);
    }
    
    public async Task RemoveAsync(string episodeId){
        await _lock.WaitAsync();
        try{
            var history = await LoadHistoryAsync();
            history.RemoveAll(h => h.EpisodeId == episodeId);
            await SaveHistoryAsync(history);
        } finally{
            _lock.Release();
        }
    }
    
    public async Task<List<DownloadHistory>> GetSeriesHistoryAsync(string seriesId){
        var history = await GetAllAsync();
        return history.Where(h => h.SeriesId == seriesId).ToList();
    }

    // Rich history methods
    public async Task<List<HistorySeries>> GetHistorySeriesAsync(){
        await EnsureLoadedAsync();
        return _historyList;
    }

    public async Task UpdateWithSeasonDataAsync(List<EpisodeInfo> episodes){
        if (episodes == null || episodes.Count == 0) return;
        
        await _lock.WaitAsync();
        try{
            await EnsureLoadedAsync();
            
            var firstEpisode = episodes.First();
            var seriesId = firstEpisode.SeriesId;
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            
            if (historySeries != null){
                historySeries.HistorySeriesAddDate ??= DateTime.Now;
                var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == firstEpisode.SeasonId);
                
                if (historySeason != null){
                    // Update existing season
                    foreach (var episode in episodes){
                        if (episode.SeasonId != historySeason.SeasonId) continue;
                        
                        var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episode.Id);
                        if (historyEpisode == null){
                            historySeason.EpisodesList.Add(CreateHistoryEpisode(episode));
                        } else{
                            UpdateHistoryEpisode(historyEpisode, episode);
                        }
                    }
                    
                    historySeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                } else{
                    // Create new season
                    var newSeason = CreateHistorySeason(episodes, firstEpisode);
                    newSeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                    historySeries.Seasons.Add(newSeason);
                }
                
                historySeries.UpdateNewEpisodes();
            } else if (!string.IsNullOrEmpty(seriesId)){
                // Create new series
                historySeries = new HistorySeries{
                    SeriesTitle = firstEpisode.SeriesTitle,
                    SeriesId = seriesId,
                    Seasons = [],
                    HistorySeriesAddDate = DateTime.Now,
                    SeriesType = "Series",
                    SeriesStreamingService = "Crunchyroll"
                };
                _historyList.Add(historySeries);
                
                var newSeason = CreateHistorySeason(episodes, firstEpisode);
                newSeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                historySeries.Seasons.Add(newSeason);
                historySeries.UpdateNewEpisodes();
            }
            
            await SaveRichHistoryAsync();
        } finally{
            _lock.Release();
        }
    }

    public async Task SetAsDownloadedAsync(string? seriesId, string? seasonId, string episodeId){
        await _lock.WaitAsync();
        try{
            await EnsureLoadedAsync();
            
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            if (historySeries != null){
                var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
                if (historySeason != null){
                    var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episodeId);
                    if (historyEpisode != null){
                        historyEpisode.WasDownloaded = true;
                        historySeason.UpdateDownloaded();
                        historySeries.UpdateNewEpisodes();
                        await SaveRichHistoryAsync();
                        return;
                    }
                }
            }
            
            _logger?.LogWarning("Couldn't update download history for episode {EpisodeId}", episodeId);
        } finally{
            _lock.Release();
        }
    }

    public async Task<HistoryEpisode?> GetHistoryEpisodeAsync(string? seriesId, string? seasonId, string episodeId){
        await EnsureLoadedAsync();
        return _historyList
            .FirstOrDefault(s => s.SeriesId == seriesId)?
            .Seasons.FirstOrDefault(s => s.SeasonId == seasonId)?
            .EpisodesList.Find(e => e.EpisodeId == episodeId);
    }

    public async Task RemoveUnavailableEpisodesAsync(){
        await _lock.WaitAsync();
        try{
            await EnsureLoadedAsync();
            
            foreach (var historySeries in _historyList.ToList()){
                var seasonsToRemove = new List<HistorySeason>();
                
                foreach (var season in historySeries.Seasons){
                    var unavailableEpisodes = season.EpisodesList
                        .Where(episode => !episode.IsEpisodeAvailableOnStreamingService)
                        .ToList();
                    
                    foreach (var episode in unavailableEpisodes){
                        season.EpisodesList.Remove(episode);
                    }
                    
                    if (season.EpisodesList.Count == 0){
                        seasonsToRemove.Add(season);
                    } else{
                        season.EpisodesList.Sort(new NumericStringPropertyComparer());
                        season.UpdateDownloaded();
                    }
                }
                
                foreach (var season in seasonsToRemove){
                    historySeries.Seasons.Remove(season);
                }
                
                if (historySeries.Seasons.Count == 0){
                    _historyList.Remove(historySeries);
                } else{
                    historySeries.UpdateNewEpisodes();
                }
            }
            
            await SaveRichHistoryAsync();
        } finally{
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync(){
        if (!_loaded){
            await LoadRichHistoryAsync();
            _loaded = true;
        }
    }

    private static HistoryEpisode CreateHistoryEpisode(EpisodeInfo episode){
        return new HistoryEpisode{
            EpisodeTitle = episode.Title,
            EpisodeDescription = episode.Description,
            EpisodeId = episode.Id,
            Episode = episode.EpisodeNumber.ToString(),
            EpisodeSeasonNum = episode.SeasonNumber.ToString(),
            SpecialEpisode = false,
            IsEpisodeAvailableOnStreamingService = true,
            ThumbnailImageUrl = episode.ThumbnailUrl
        };
    }

    private static void UpdateHistoryEpisode(HistoryEpisode historyEpisode, EpisodeInfo episode){
        historyEpisode.EpisodeTitle = episode.Title;
        historyEpisode.EpisodeDescription = episode.Description;
        historyEpisode.EpisodeId = episode.Id;
        historyEpisode.Episode = episode.EpisodeNumber.ToString();
        historyEpisode.EpisodeSeasonNum = episode.SeasonNumber.ToString();
        historyEpisode.IsEpisodeAvailableOnStreamingService = true;
        historyEpisode.ThumbnailImageUrl = episode.ThumbnailUrl;
    }

    private static HistorySeason CreateHistorySeason(List<EpisodeInfo> episodes, EpisodeInfo firstEpisode){
        var season = new HistorySeason{
            SeasonTitle = firstEpisode.SeasonTitle,
            SeasonId = firstEpisode.SeasonId,
            SeasonNum = firstEpisode.SeasonNumber.ToString(),
            EpisodesList = [],
            SpecialSeason = false
        };

        foreach (var episode in episodes){
            if (episode.SeasonId != season.SeasonId) continue;
            season.EpisodesList.Add(CreateHistoryEpisode(episode));
        }

        return season;
    }

    // Persistence
    private async Task LoadRichHistoryAsync(){
        if (!File.Exists(_historyPath)){
            _historyList = [];
            return;
        }
        
        try{
            var content = await File.ReadAllTextAsync(_historyPath);
            _historyList = JsonSerializer.Deserialize(content, HistoryJsonContext.Default.ListHistorySeries) ?? [];
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to load rich history");
            _historyList = [];
        }
    }

    private async Task SaveRichHistoryAsync(){
        try{
            var content = JsonSerializer.Serialize(_historyList, HistoryJsonContext.Default.ListHistorySeries);
            await File.WriteAllTextAsync(_historyPath, content);
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to save rich history");
        }
    }

    private async Task<List<DownloadHistory>> LoadHistoryAsync(){
        if (!File.Exists(_historyPath)) return new List<DownloadHistory>();
        
        try{
            var content = await File.ReadAllTextAsync(_historyPath);
            return JsonSerializer.Deserialize(content, HistoryJsonContext.Default.ListDownloadHistory) ?? new List<DownloadHistory>();
        } catch{
            return new List<DownloadHistory>();
        }
    }

    private async Task SaveHistoryAsync(List<DownloadHistory> history){
        var content = JsonSerializer.Serialize(history, HistoryJsonContext.Default.ListDownloadHistory);
        await File.WriteAllTextAsync(_historyPath, content);
    }
}

public class NumericStringPropertyComparer : IComparer<HistoryEpisode>{
    public int Compare(HistoryEpisode? x, HistoryEpisode? y){
        if (double.TryParse(x?.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out double xDouble) &&
            double.TryParse(y?.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out double yDouble)){
            return xDouble.CompareTo(yDouble);
        }
        return string.Compare(x?.Episode, y?.Episode, StringComparison.Ordinal);
    }
}
