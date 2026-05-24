using System.Collections.Specialized;
using System.Web;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Services;

public interface ICrunchyrollApiService{
    Task<List<SeriesInfo>> SearchAsync(string query, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<EpisodeInfo?> GetEpisodeAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken = default);
}

public class CrunchyrollApiService : ICrunchyrollApiService{
    private readonly ILogger<CrunchyrollApiService>? _logger;
    private readonly ICrunchyrollAuthService _authService;
    private readonly HttpClientWrapper _httpClient;
    
    public CrunchyrollApiService(ICrunchyrollAuthService authService, ILogger<CrunchyrollApiService>? logger = null){
        _authService = authService;
        _logger = logger;
        _httpClient = new HttpClientWrapper();
    }
    
    public async Task<List<SeriesInfo>> SearchAsync(string query, bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Searching for: {Query}", query);
        
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return new List<SeriesInfo>();
        }
        
        var queryParams = new NameValueCollection{
            { "q", query },
            { "n", "20" },
            { "type", "series" }
        };
        
        var uriBuilder = new UriBuilder(ApiUrls.Search(useBetaApi)){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("Search failed: {Error}", error);
            return new List<SeriesInfo>();
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrSearchResult>(content);
            if (result?.Data == null) return new List<SeriesInfo>();
            
            var seriesList = new List<SeriesInfo>();
            foreach (var group in result.Data){
                if (group.Items == null) continue;
                foreach (var item in group.Items){
                    seriesList.Add(new SeriesInfo{
                        Id = item.Id,
                        Title = item.Title,
                        Description = item.Description,
                        Images = ExtractImageUrls(item.Images),
                        CoverArtUrl = ExtractBestImage(item.Images, "poster_tall"),
                        ThumbnailUrl = ExtractBestImage(item.Images, "poster_wide")
                    });
                }
            }
            return seriesList;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse search results");
            return new List<SeriesInfo>();
        }
    }
    
    public async Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Getting series: {SeriesId}", seriesId);
        
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return null;
        }
        
        // Extract series ID from URL if needed
        var id = ExtractIdFromUrl(seriesId);
        
        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/series/{id}", 
            HttpMethod.Get, 
            true, 
            _authService.Token?.access_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("Get series failed: {Error}", error);
            return null;
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrCmsResponse<CrSeriesDetail>>(content);
            if (result?.Data == null) return null;
            
            var series = new SeriesInfo{
                Id = result.Data.Id,
                Title = result.Data.Title,
                Description = result.Data.Description,
                Images = ExtractImageUrls(result.Data.Images),
                CoverArtUrl = ExtractBestImage(result.Data.Images, "poster_tall"),
                ThumbnailUrl = ExtractBestImage(result.Data.Images, "poster_wide")
            };
            
            // Get seasons
            var seasons = await GetSeasonsAsync(id, useBetaApi, cancellationToken);
            series.Seasons = seasons;
            
            return series;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse series data");
            return null;
        }
    }
    
    public async Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default){
        var id = ExtractIdFromUrl(seriesId);
        var seasons = await GetSeasonsAsync(id, useBetaApi, cancellationToken);
        
        var allEpisodes = new List<EpisodeInfo>();
        foreach (var season in seasons){
            var episodes = await GetSeasonEpisodesAsync(season.Id, useBetaApi, cancellationToken);
            allEpisodes.AddRange(episodes);
        }
        
        return allEpisodes;
    }
    
    public async Task<EpisodeInfo?> GetEpisodeAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Getting episode: {EpisodeId}", episodeId);
        
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return null;
        }
        
        var id = ExtractIdFromUrl(episodeId);
        
        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/episodes/{id}", 
            HttpMethod.Get, 
            true, 
            _authService.Token?.access_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("Get episode failed: {Error}", error);
            return null;
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            if (result?.Data == null || result.Data.Count == 0) return null;
            
            var ep = result.Data[0];
            var originalVersion = ep.Versions?.FirstOrDefault(v => v.Original);
            var guid = originalVersion?.Guid ?? ep.Id;
            return new EpisodeInfo{
                Id = ep.Id,
                Guid = guid,
                Title = ep.Title,
                EpisodeNumber = ep.EpisodeNumber ?? 0,
                SeasonNumber = ep.SeasonNumber,
                Description = ep.Description,
                SeriesTitle = ep.SeriesTitle,
                Locale = ep.AudioLocale ?? "ja-JP",
                AudioLocale = ep.AudioLocale,
                IsPremium = ep.IsPremiumOnly,
                Versions = ep.Versions?.Select(v => new EpisodeVersion{
                    AudioLocale = v.AudioLocale,
                    Guid = v.Guid,
                    MediaGuid = v.MediaGuid,
                    Original = v.Original,
                    SeasonGuid = v.SeasonGuid
                }).ToList(),
                Images = ExtractImageUrls(ep.Images),
                ThumbnailUrl = ExtractBestImage(ep.Images, "thumbnail") ?? ExtractBestImage(ep.Images, "episode_thumbnail"),
                SubtitleLocales = ep.SubtitleLocales ?? new List<string>()
            };
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse episode data");
            return null;
        }
    }
    
    private async Task<List<SeasonInfo>> GetSeasonsAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default){
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return new List<SeasonInfo>();
        }
        
        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/series/{seriesId}/seasons", 
            HttpMethod.Get, 
            true, 
            _authService.Token?.access_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk) return new List<SeasonInfo>();
        
        try{
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrSeasonDetail>>(content);
            if (result?.Data == null) return new List<SeasonInfo>();
            
            return result.Data.Select(s => new SeasonInfo{
                Id = s.Id,
                Title = s.Title,
                SeasonNumber = s.SeasonNumber
            }).ToList();
        } catch{
            return new List<SeasonInfo>();
        }
    }
    
    private async Task<List<EpisodeInfo>> GetSeasonEpisodesAsync(string seasonId, bool useBetaApi, CancellationToken cancellationToken = default){
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return new List<EpisodeInfo>();
        }
        
        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/seasons/{seasonId}/episodes", 
            HttpMethod.Get, 
            true, 
            _authService.Token?.access_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk) return new List<EpisodeInfo>();
        
        try{
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            if (result?.Data == null) return new List<EpisodeInfo>();
            
            return result.Data.Select(e => {
                var originalVersion = e.Versions?.FirstOrDefault(v => v.Original);
                var guid = originalVersion?.Guid ?? e.Id;
                return new EpisodeInfo{
                    Id = e.Id,
                    Guid = guid,
                    Title = e.Title,
                    EpisodeNumber = e.EpisodeNumber ?? 0,
                    SeasonNumber = e.SeasonNumber,
                    SeasonTitle = e.SeasonTitle,
                    SeasonId = e.SeasonId,
                    Description = e.Description,
                    SeriesTitle = e.SeriesTitle,
                    Locale = e.AudioLocale ?? "ja-JP",
                    AudioLocale = e.AudioLocale,
                    IsPremium = e.IsPremiumOnly,
                    Images = ExtractImageUrls(e.Images),
                    ThumbnailUrl = ExtractBestImage(e.Images, "thumbnail") ?? ExtractBestImage(e.Images, "episode_thumbnail"),
                    CoverArtUrl = ExtractBestImage(e.Images, "poster_tall"),
                    Versions = e.Versions?.Select(v => new EpisodeVersion{
                        AudioLocale = v.AudioLocale,
                        Guid = v.Guid,
                        Original = v.Original,
                        SeasonGuid = v.SeasonGuid
                    }).ToList(),
                    SubtitleLocales = e.SubtitleLocales ?? new List<string>()
                };
            }).ToList();
        } catch{
            return new List<EpisodeInfo>();
        }
    }
    
    private async Task<bool> EnsureAuthenticatedAsync(bool useBetaApi, CancellationToken cancellationToken){
        if (!_authService.IsAuthenticated){
            return await _authService.AuthenticateAsync(useBetaApi, cancellationToken);
        }
        return true;
    }
    
    private static string ExtractIdFromUrl(string input){
        if (input.StartsWith("http")){
            var parts = input.Split('/');
            return parts.Last().Split('?')[0];
        }
        return input;
    }

    private static List<string> ExtractImageUrls(Dictionary<string, List<List<object>>>? images){
        var urls = new List<string>();
        if (images == null) return urls;

        foreach (var kvp in images){
            if (kvp.Value != null){
                foreach (var list in kvp.Value){
                    if (list != null && list.Count > 0){
                        var url = ExtractImageSource(list[0]);
                        if (!string.IsNullOrEmpty(url)){
                            urls.Add(url);
                        }
                    }
                }
            }
        }
        return urls;
    }

    private static string? ExtractBestImage(Dictionary<string, List<List<object>>>? images, string type){
        if (images == null || !images.ContainsKey(type)) return null;
        
        var imageList = images[type];
        if (imageList == null || imageList.Count == 0) return null;

        // Try to get the best quality (usually the last one or one with height 360/720)
        foreach (var img in imageList){
            if (img != null && img.Count > 0){
                var url = ExtractImageSource(img[0]);
                if (!string.IsNullOrEmpty(url)){
                    return url;
                }
            }
        }
        return null;
    }

    private static string? ExtractImageSource(object? imageObj){
        if (imageObj == null) return null;
        
        // If it's already a string, return it
        if (imageObj is string str) return str;
        
        // If it's a JObject, extract the source property
        if (imageObj is JObject jObj){
            return jObj["source"]?.ToString();
        }
        
        // Try to parse as JSON
        try{
            var json = imageObj.ToString();
            if (!string.IsNullOrEmpty(json) && json.StartsWith("{")){
                var jObj2 = JObject.Parse(json);
                return jObj2["source"]?.ToString();
            }
        } catch{
            // Ignore parse errors
        }
        
        return imageObj.ToString();
    }
}

// API Response Models
public class CrSearchResult{
    public int Total{ get; set; }
    public List<CrSearchGroup>? Data{ get; set; }
}

public class CrSearchGroup{
    public string? Type{ get; set; }
    public int Count{ get; set; }
    public List<CrSearchItem>? Items{ get; set; }
}

public class CrSearchItem{
    public string Id{ get; set; } = "";
    public string Title{ get; set; } = "";
    public string Description{ get; set; } = "";
    public Dictionary<string, List<List<object>>>? Images{ get; set; }
}

public class CrCmsResponse<T>{
    public T? Data{ get; set; }
}

public class CrCmsListResponse<T>{
    public List<T>? Data{ get; set; }
    public int Total{ get; set; }
}

public class CrSeriesDetail{
    public string Id{ get; set; } = "";
    public string Title{ get; set; } = "";
    public string Description{ get; set; } = "";
    public Dictionary<string, List<List<object>>>? Images{ get; set; }
}

public class CrSeasonDetail{
    public string Id{ get; set; } = "";
    public string Title{ get; set; } = "";
    [JsonProperty("season_number")]
    public int SeasonNumber{ get; set; }
}

public class CrEpisodeVersion{
    [JsonProperty("audio_locale")]
    public string AudioLocale{ get; set; } = "";
    public string Guid{ get; set; } = "";
    [JsonProperty("media_guid")]
    public string? MediaGuid{ get; set; }
    public bool Original{ get; set; }
    [JsonProperty("season_guid")]
    public string SeasonGuid{ get; set; } = "";
}

public class CrEpisodeDetail{
    public string Id{ get; set; } = "";
    public string Guid{ get; set; } = "";
    public string Title{ get; set; } = "";
    public string Description{ get; set; } = "";
    [JsonProperty("episode_number")]
    public int? EpisodeNumber{ get; set; }
    [JsonProperty("season_number")]
    public int SeasonNumber{ get; set; }
    [JsonProperty("series_title")]
    public string SeriesTitle{ get; set; } = "";
    [JsonProperty("season_title")]
    public string? SeasonTitle{ get; set; }
    [JsonProperty("season_id")]
    public string? SeasonId{ get; set; }
    [JsonProperty("audio_locale")]
    public string? AudioLocale{ get; set; }
    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly{ get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales{ get; set; }
    public List<CrEpisodeVersion>? Versions{ get; set; }
    public Dictionary<string, List<List<object>>>? Images{ get; set; }
}


