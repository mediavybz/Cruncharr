using System.Net.Http.Headers;
using System.Text.Json;
using Cruncharr.Core.Configuration;
using Microsoft.Extensions.Logging;

#pragma warning disable IL2026

namespace Cruncharr.Core.Services;

public interface ISonarrService{
    Task<bool> TestConnectionAsync(SonarrConfig config);
    Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config);
    Task<SonarrSeries?> GetSeriesByTitleAsync(string title, SonarrConfig config);
    Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config);
}

public class SonarrService : ISonarrService{
    private readonly ILogger<SonarrService>? _logger;
    private readonly HttpClient _httpClient;

    public SonarrService(IHttpClientFactory httpClientFactory, ILogger<SonarrService>? logger = null){
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    private string BuildBaseUrl(SonarrConfig config){
        var scheme = config.UseSsl ? "https" : "http";
        var baseUrl = $"{scheme}://{config.Host}:{config.Port}";
        if (!string.IsNullOrEmpty(config.UrlBase)){
            baseUrl = baseUrl.TrimEnd('/') + "/" + config.UrlBase.Trim('/');
        }
        return baseUrl + "/api/v3";
    }

    public virtual async Task<bool> TestConnectionAsync(SonarrConfig config){
        try{
            var url = $"{BuildBaseUrl(config)}/system/status";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);
            
            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        } catch (Exception ex){
            _logger?.LogError(ex, "Sonarr connection test failed");
            return false;
        }
    }

    public virtual async Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config){
        try{
            var url = $"{BuildBaseUrl(config)}/series";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);
            
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<SonarrSeries>();
            
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<SonarrSeries>>(content, new JsonSerializerOptions{ PropertyNameCaseInsensitive = true }) ?? new List<SonarrSeries>();
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to get Sonarr series");
            return new List<SonarrSeries>();
        }
    }

    public virtual async Task<SonarrSeries?> GetSeriesByTitleAsync(string title, SonarrConfig config){
        var series = await GetSeriesAsync(config);
        return series.FirstOrDefault(s => 
            s.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
            s.CleanTitle?.Equals(title, StringComparison.OrdinalIgnoreCase) == true);
    }

    public virtual async Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config){
        try{
            var url = $"{BuildBaseUrl(config)}/episode?seriesId={seriesId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);
            
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<SonarrEpisode>();
            
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<SonarrEpisode>>(content, new JsonSerializerOptions{ PropertyNameCaseInsensitive = true }) ?? new List<SonarrEpisode>();
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to get Sonarr episodes");
            return new List<SonarrEpisode>();
        }
    }
}

public class SonarrSeries{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? CleanTitle { get; set; }
    public string? SortTitle { get; set; }
    public string? Status { get; set; }
    public string? Overview { get; set; }
    public List<SonarrSeason>? Seasons { get; set; }
    public int Year { get; set; }
    public string? Path { get; set; }
    public int TvdbId { get; set; }
    public string? TitleSlug { get; set; }
}

public class SonarrSeason{
    public int SeasonNumber { get; set; }
    public bool Monitored { get; set; }
    public int EpisodeCount { get; set; }
    public int TotalEpisodeCount { get; set; }
}

public class SonarrEpisode{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public int SeasonNumber { get; set; }
    public string? Title { get; set; }
    public bool HasFile { get; set; }
    public bool Monitored { get; set; }
    public int AbsoluteEpisodeNumber { get; set; }
    public string? Overview { get; set; }
    public DateTimeOffset AirDateUtc { get; set; }
}

#pragma warning restore IL2026
