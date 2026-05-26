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
    Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, string? crLocale = null, bool forced = false, CancellationToken cancellationToken = default);
    Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<EpisodeInfo?> GetEpisodeAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, bool forcedLang = false, CancellationToken cancellationToken = default);
    
    // Methods ported from upstream CrSeries for HistoryService
    Task<List<SeasonInfo>> ParseSeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default);
    Task<List<EpisodeInfo>> GetSeasonDataByIdAsync(string seasonId, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<SeriesInfo?> SeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default);
    
    // Methods ported from upstream CrEpisode
    Task<EpisodeInfo?> ParseEpisodeByIdAsync(string id, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default);
    Task MarkAsWatchedAsync(string episodeId, CancellationToken cancellationToken = default);
    
    // Methods ported from upstream CrSeries
    Task<List<SeriesInfo>> GetAllSeriesAsync(string? crLocale = null, CancellationToken cancellationToken = default);
    Task<List<SeriesInfo>> GetSeasonalSeriesAsync(string season, string year, string? crLocale = null, CancellationToken cancellationToken = default);
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
    
    public async Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, string? crLocale = null, bool forced = false, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Getting series: {SeriesId}", seriesId);
        
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return null;
        }
        
        // Extract series ID from URL if needed
        var id = ExtractIdFromUrl(seriesId);
        
        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale)){
            queryParams["locale"] = crLocale;
            if (forced){
                queryParams["force_locale"] = crLocale;
            }
        }
        
        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(useBetaApi)}/series/{id}"){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        
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
            var episodes = await GetSeasonEpisodesAsync(season.Id, useBetaApi, cancellationToken: cancellationToken);
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
    
    public async Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, bool forcedLang = false, CancellationToken cancellationToken = default){
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            return null;
        }
        
        if (string.IsNullOrEmpty(crLocale)){
            crLocale = "en-US";
        }
        
        var queryParams = new NameValueCollection();
        queryParams["locale"] = crLocale;
        if (forcedLang){
            queryParams["force_locale"] = crLocale;
        }
        queryParams["n"] = requestAmount.ToString();
        queryParams["sort_by"] = "newly_added";
        queryParams["type"] = "episode";
        
        var uriBuilder = new UriBuilder(ApiUrls.Browse(true)){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("Get new episodes failed: {Error}", error);
            return null;
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrBrowseEpisodeBase>(content);
            result?.Data?.Sort((a, b) => b.EpisodeMetadata.PremiumAvailableDate.CompareTo(a.EpisodeMetadata.PremiumAvailableDate));
            return result;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse new episodes data");
            return null;
        }
    }
    
    /// <summary>
    /// Parses episode by ID with version deduplication.
    /// Ported from upstream CrEpisode.ParseEpisodeById.
    /// Handles duplicate audio locale versions by validating each one.
    /// </summary>
    public async Task<EpisodeInfo?> ParseEpisodeByIdAsync(string id, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Parsing episode by ID: {EpisodeId}, locale: {Locale}", id, crLocale);
        
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            return null;
        }
        
        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale)){
            queryParams["locale"] = crLocale;
            if (forcedLang){
                queryParams["force_locale"] = crLocale;
            }
        }
        
        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/episodes/{id}"){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("ParseEpisodeById failed: {Error}", error);
            return null;
        }
        
        try{
            var episodeList = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            
            if (episodeList?.Data == null || episodeList.Total < 1){
                _logger?.LogWarning("Episode not found: {EpisodeId}", id);
                return null;
            }
            
            var episode = episodeList.Data.First();
            
            // [PT] Ported from upstream: handle duplicate audio locale versions
            if (episodeList.Total == 1 && episode.Versions != null){
                var duplicateGroups = episode.Versions
                    .GroupBy(v => v.AudioLocale)
                    .Where(g => g.Count() > 1)
                    .ToList();
                
                if (duplicateGroups.Count > 0){
                    _logger?.LogWarning("Episode {EpisodeId} has duplicate audio locales, validating versions...", id);
                    
                    foreach (var group in duplicateGroups){
                        foreach (var version in group.ToList()){
                            var checkUriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/episodes/{version.Guid}"){
                                Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
                            };
                            
                            var checkRequest = HttpClientWrapper.CreateRequest(checkUriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
                            var (checkOk, _, _) = await _httpClient.SendRequestAsync(checkRequest);
                            
                            if (!checkOk){
                                _logger?.LogWarning("Removing invalid version {VersionGuid} for locale {Locale}", version.Guid, version.AudioLocale);
                                episode.Versions.Remove(version);
                            }
                        }
                    }
                }
            }
            
            var originalVersion = episode.Versions?.FirstOrDefault(v => v.Original);
            var guid = originalVersion?.Guid ?? episode.Id;
            
            return new EpisodeInfo{
                Id = episode.Id,
                Guid = guid,
                Title = episode.Title,
                EpisodeNumber = episode.EpisodeNumber ?? 0,
                SeasonNumber = episode.SeasonNumber,
                Description = episode.Description,
                SeriesTitle = episode.SeriesTitle,
                Locale = episode.AudioLocale ?? "ja-JP",
                AudioLocale = episode.AudioLocale,
                IsPremium = episode.IsPremiumOnly,
                Versions = episode.Versions?.Select(v => new EpisodeVersion{
                    AudioLocale = v.AudioLocale,
                    Guid = v.Guid,
                    MediaGuid = v.MediaGuid,
                    Original = v.Original,
                    SeasonGuid = v.SeasonGuid
                }).ToList(),
                Images = ExtractImageUrls(episode.Images),
                ThumbnailUrl = ExtractBestImage(episode.Images, "thumbnail") ?? ExtractBestImage(episode.Images, "episode_thumbnail"),
                CoverArtUrl = ExtractBestImage(episode.Images, "poster_tall"),
                SubtitleLocales = episode.SubtitleLocales ?? new List<string>()
            };
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse episode data for {EpisodeId}", id);
            return null;
        }
    }
    
    /// <summary>
    /// Marks an episode as watched on Crunchyroll.
    /// Ported from upstream CrEpisode.MarkAsWatched.
    /// </summary>
    public async Task MarkAsWatchedAsync(string episodeId, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Marking episode as watched: {EpisodeId}", episodeId);
        
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            _logger?.LogWarning("Cannot mark as watched: not authenticated");
            return;
        }
        
        if (_authService.Token?.account_id == null){
            _logger?.LogWarning("Cannot mark as watched: no account ID");
            return;
        }
        
        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Content}/discover/{_authService.Token.account_id}/mark_as_watched/{episodeId}",
            HttpMethod.Post,
            true,
            _authService.Token.access_token);
        
        var (isOk, _, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("MarkAsWatched failed: {Error}", error);
        } else{
            _logger?.LogInformation("Marked episode {EpisodeId} as watched", episodeId);
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
    
    private async Task<List<EpisodeInfo>> GetSeasonEpisodesAsync(string seasonId, bool useBetaApi, string? crLocale = null, bool forcedLang = false, CancellationToken cancellationToken = default){
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken)){
            return new List<EpisodeInfo>();
        }
        
        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale)){
            queryParams["locale"] = crLocale;
            if (forcedLang){
                queryParams["force_locale"] = crLocale;
            }
        }
        
        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(useBetaApi)}/seasons/{seasonId}/episodes"){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        
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
    
    public async Task<List<SeasonInfo>> ParseSeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default){
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            return new List<SeasonInfo>();
        }
        
        var queryParams = new NameValueCollection{
            { "preferred_audio_language", "ja-JP" }
        };
        if (!string.IsNullOrEmpty(crLocale)){
            queryParams["locale"] = crLocale;
            if (forced){
                queryParams["force_locale"] = crLocale;
            }
        }
        
        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/series/{id}/seasons"){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("ParseSeriesById failed: {Error}", error);
            return new List<SeasonInfo>();
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrSeasonDetail>>(content);
            if (result?.Data == null) return new List<SeasonInfo>();
            
            return result.Data.Select(s => new SeasonInfo{
                Id = s.Id,
                Title = s.Title,
                SeasonNumber = s.SeasonNumber
            }).ToList();
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse series seasons");
            return new List<SeasonInfo>();
        }
    }
    
    public async Task<List<EpisodeInfo>> GetSeasonDataByIdAsync(string seasonId, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default){
        return await GetSeasonEpisodesAsync(seasonId, true, crLocale, forcedLang, cancellationToken);
    }
    
    public async Task<SeriesInfo?> SeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default){
        return await GetSeriesAsync(id, true, crLocale, forced, cancellationToken);
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
    
    // Ported from upstream CrSeries.GetAllSeries
    public async Task<List<SeriesInfo>> GetAllSeriesAsync(string? crLocale = null, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Getting all series");
        
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            return new List<SeriesInfo>();
        }
        
        var complete = new List<SeriesInfo>();
        var total = 0;
        var i = 0;
        
        do{
            var queryParams = new NameValueCollection{
                { "start", i.ToString() },
                { "n", "50" },
                { "sort_by", "alphabetical" }
            };
            
            if (!string.IsNullOrEmpty(crLocale)){
                queryParams["locale"] = crLocale;
            }
            
            var uriBuilder = new UriBuilder(ApiUrls.Browse(true)){
                Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
            };
            
            var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
            
            if (!isOk){
                _logger?.LogError("GetAllSeries request failed: {Error}", error);
                return complete;
            }
            
            try{
                var result = JsonConvert.DeserializeObject<CrBrowseSeriesBase>(content);
                if (result?.Data == null) break;
                
                total = result.Total;
                foreach (var item in result.Data){
                    complete.Add(new SeriesInfo{
                        Id = item.Id,
                        Title = item.Title,
                        Description = item.Description,
                        Images = ExtractImageUrls(item.Images),
                        CoverArtUrl = ExtractBestImage(item.Images, "poster_tall"),
                        ThumbnailUrl = ExtractBestImage(item.Images, "poster_wide")
                    });
                }
            } catch (Exception ex){
                _logger?.LogError(ex, "Failed to parse GetAllSeries results");
                break;
            }
            
            i += 50;
        } while (i < total);
        
        return complete;
    }
    
    // Ported from upstream CrSeries.GetSeasonalSeries
    public async Task<List<SeriesInfo>> GetSeasonalSeriesAsync(string season, string year, string? crLocale = null, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Getting seasonal series: {Season} {Year}", season, year);
        
        if (!await EnsureAuthenticatedAsync(true, cancellationToken)){
            return new List<SeriesInfo>();
        }
        
        var queryParams = new NameValueCollection{
            { "seasonal_tag", $"{season.ToLower()}-{year}" },
            { "n", "100" }
        };
        
        if (!string.IsNullOrEmpty(crLocale)){
            queryParams["locale"] = crLocale;
        }
        
        var uriBuilder = new UriBuilder(ApiUrls.Browse(true)){
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };
        
        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (!isOk){
            _logger?.LogError("GetSeasonalSeries request failed: {Error}", error);
            return new List<SeriesInfo>();
        }
        
        try{
            var result = JsonConvert.DeserializeObject<CrBrowseSeriesBase>(content);
            if (result?.Data == null) return new List<SeriesInfo>();
            
            return result.Data.Select(item => new SeriesInfo{
                Id = item.Id,
                Title = item.Title,
                Description = item.Description,
                Images = ExtractImageUrls(item.Images),
                CoverArtUrl = ExtractBestImage(item.Images, "poster_tall"),
                ThumbnailUrl = ExtractBestImage(item.Images, "poster_wide")
            }).ToList();
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse GetSeasonalSeries results");
            return new List<SeriesInfo>();
        }
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

// Browse series models for GetAllSeries/GetSeasonalSeries
public class CrBrowseSeriesBase{
	public int Total{ get; set; }
	public List<CrBrowseSeriesItem>? Data{ get; set; }
}

public class CrBrowseSeriesItem{
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

// Browse Episode models for GetNewEpisodes
public class CrBrowseEpisodeBase{
    public int Total{ get; set; }
    public List<CrBrowseEpisode>? Data{ get; set; }
    public CrBrowseMeta? Meta{ get; set; }
}

public class CrBrowseEpisode{
    [JsonProperty("external_id")]
    public string? ExternalId{ get; set; }
    [JsonProperty("last_public")]
    public DateTime LastPublic{ get; set; }
    public string? Description{ get; set; }
    public bool New{ get; set; }
    [JsonProperty("linked_resource_key")]
    public string? LinkedResourceKey{ get; set; }
    [JsonProperty("slug_title")]
    public string? SlugTitle{ get; set; }
    public string? Title{ get; set; }
    [JsonProperty("promo_title")]
    public string? PromoTitle{ get; set; }
    [JsonProperty("episode_metadata")]
    public CrBrowseEpisodeMetaData EpisodeMetadata{ get; set; } = new();
    public string? Id{ get; set; }
    public CrBrowseImages? Images{ get; set; }
    [JsonProperty("promo_description")]
    public string? PromoDescription{ get; set; }
    public string? Slug{ get; set; }
    public string? Type{ get; set; }
    [JsonProperty("channel_id")]
    public string? ChannelId{ get; set; }
    [JsonProperty("streams_link")]
    public string? StreamsLink{ get; set; }
}

public class CrBrowseEpisodeMetaData{
    [JsonProperty("audio_locale")]
    public string? AudioLocale{ get; set; }
    [JsonProperty("content_descriptors")]
    public List<string>? ContentDescriptors{ get; set; }
    [JsonProperty("availability_notes")]
    public string? AvailabilityNotes{ get; set; }
    public string? Episode{ get; set; }
    [JsonProperty("episode_air_date")]
    public DateTime EpisodeAirDate{ get; set; }
    [JsonProperty("episode_number")]
    public int EpisodeCount{ get; set; }
    [JsonProperty("duration_ms")]
    public int DurationMs{ get; set; }
    [JsonProperty("extended_maturity_rating")]
    public Dictionary<object, object>? ExtendedMaturityRating{ get; set; }
    [JsonProperty("is_dubbed")]
    public bool IsDubbed{ get; set; }
    [JsonProperty("is_mature")]
    public bool IsMature{ get; set; }
    [JsonProperty("is_subbed")]
    public bool IsSubbed{ get; set; }
    [JsonProperty("mature_blocked")]
    public bool MatureBlocked{ get; set; }
    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly{ get; set; }
    [JsonProperty("is_clip")]
    public bool IsClip{ get; set; }
    [JsonProperty("maturity_ratings")]
    public List<string>? MaturityRatings{ get; set; }
    [JsonProperty("season_number")]
    public double SeasonNumber{ get; set; }
    [JsonProperty("season_sequence_number")]
    public double SeasonSequenceNumber{ get; set; }
    [JsonProperty("sequence_number")]
    public double SequenceNumber{ get; set; }
    [JsonProperty("upload_date")]
    public DateTime UploadDate{ get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales{ get; set; }
    [JsonProperty("premium_available_date")]
    public DateTime PremiumAvailableDate{ get; set; }
    [JsonProperty("availability_ends")]
    public DateTime AvailabilityEnds{ get; set; }
    [JsonProperty("availability_starts")]
    public DateTime AvailabilityStarts{ get; set; }
    [JsonProperty("free_available_date")]
    public DateTime FreeAvailableDate{ get; set; }
    [JsonProperty("identifier")]
    public string? Identifier{ get; set; }
    [JsonProperty("season_id")]
    public string? SeasonId{ get; set; }
    [JsonProperty("series_id")]
    public string? SeriesId{ get; set; }
    [JsonProperty("season_display_number")]
    public string? SeasonDisplayNumber{ get; set; }
    [JsonProperty("eligible_region")]
    public string? EligibleRegion{ get; set; }
    [JsonProperty("available_date")]
    public DateTime AvailableDate{ get; set; }
    [JsonProperty("premium_date")]
    public DateTime PremiumDate{ get; set; }
    [JsonProperty("available_offline")]
    public bool AvailableOffline{ get; set; }
    [JsonProperty("closed_captions_available")]
    public bool ClosedCaptionsAvailable{ get; set; }
    [JsonProperty("season_slug_title")]
    public string? SeasonSlugTitle{ get; set; }
    [JsonProperty("season_title")]
    public string? SeasonTitle{ get; set; }
    [JsonProperty("series_slug_title")]
    public string? SeriesSlugTitle{ get; set; }
    [JsonProperty("series_title")]
    public string? SeriesTitle{ get; set; }
    [JsonProperty("versions")]
    public List<CrBrowseEpisodeVersion>? Versions{ get; set; }
}

public class CrBrowseEpisodeVersion{
    [JsonProperty("audio_locale")]
    public string? AudioLocale{ get; set; }
    public string? Guid{ get; set; }
    public bool Original{ get; set; }
    public string? Variant{ get; set; }
    [JsonProperty("season_guid")]
    public string? SeasonGuid{ get; set; }
    [JsonProperty("media_guid")]
    public string? MediaGuid{ get; set; }
}

public class CrBrowseImages{
    public List<List<CrBrowseThumbnail>>? Thumbnail{ get; set; }
}

public class CrBrowseThumbnail{
    public string? Source{ get; set; }
}

public class CrBrowseMeta{
    public int TotalBeforeFilter{ get; set; }
    public int TotalAfterFilter{ get; set; }
}

