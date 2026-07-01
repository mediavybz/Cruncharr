using System.Net.Http.Headers;
using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;

#pragma warning disable IL2026

namespace Cruncharr.Core.Services;

public interface ISonarrService
{
    Task<bool> TestConnectionAsync(SonarrConfig config);
    Task<SonarrTestResult> TestConnectionDetailedAsync(SonarrConfig config);
    Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config);
    Task<SonarrSeries?> GetSeriesByTitleAsync(string title, SonarrConfig config);
    Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config);
}

/// <summary>Outcome of a Sonarr connection test, with a human-readable reason.</summary>
public record SonarrTestResult(bool Success, string Message);

/// <summary>
/// Outcome of a history↔Sonarr series match run. <see cref="SonarrSeriesCount"/> is how many
/// series Sonarr returned (0 usually means unreachable or a bad API key), letting the UI give
/// honest feedback instead of always reporting success.
/// </summary>
public record SonarrMatchResult(int HistoryTotal, int Matched, int SonarrSeriesCount);

public class SonarrService : ISonarrService
{
    private readonly ILogger<SonarrService>? _logger;
    private readonly HttpClient _httpClient;

    public SonarrService(IHttpClientFactory httpClientFactory, ILogger<SonarrService>? logger = null)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    private string BuildBaseUrl(SonarrConfig config)
    {
        var scheme = config.UseSsl ? "https" : "http";
        var baseUrl = $"{scheme}://{config.Host}:{config.Port}";
        if (!string.IsNullOrEmpty(config.UrlBase))
        {
            baseUrl = baseUrl.TrimEnd('/') + "/" + config.UrlBase.Trim('/');
        }
        return baseUrl + "/api/v3";
    }

    public virtual async Task<bool> TestConnectionAsync(SonarrConfig config)
    {
        try
        {
            var url = $"{BuildBaseUrl(config)}/system/status";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sonarr connection test failed");
            return false;
        }
    }

    public virtual async Task<SonarrTestResult> TestConnectionDetailedAsync(SonarrConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Host))
            return new SonarrTestResult(false, "Host is not configured.");
        if (config.Port <= 0)
            return new SonarrTestResult(false, "Port is not configured.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return new SonarrTestResult(false, "API key is not configured.");

        try
        {
            var url = $"{BuildBaseUrl(config)}/system/status";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string? version = null;
                try
                {
                    var body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                }
                catch { /* status is reachable; version is best-effort */ }

                return new SonarrTestResult(true,
                    version != null ? $"Connected to Sonarr {version}." : "Connected to Sonarr.");
            }

            var reason = (int)response.StatusCode switch
            {
                401 => "Invalid API key (401 Unauthorized).",
                403 => "API key rejected (403 Forbidden).",
                404 => "Endpoint not found (404). Check the URL Base setting.",
                _ => $"Sonarr returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}."
            };
            _logger?.LogWarning("Sonarr connection test failed: {Reason}", reason);
            return new SonarrTestResult(false, reason);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sonarr connection test failed");
            return new SonarrTestResult(false, $"Could not reach Sonarr: {ex.Message}");
        }
    }

    // /series returns Sonarr's ENTIRE library. GetSeriesByTitleAsync is called once per
    // queued download (Sonarr numbering) and per unmatched history series, so without a
    // cache a season add re-downloads the full library dozens of times back-to-back.
    // 60s is short enough that library edits show up promptly.
    private static readonly TimeSpan SeriesCacheTtl = TimeSpan.FromSeconds(60);
    private (string Key, DateTime FetchedUtc, List<SonarrSeries> Data)? _seriesCache;
    private readonly object _seriesCacheLock = new();

    public virtual async Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config)
    {
        var cacheKey = $"{config.UseSsl}|{config.Host}|{config.Port}|{config.UrlBase}|{config.ApiKey}";
        lock (_seriesCacheLock)
        {
            if (_seriesCache is { } c && c.Key == cacheKey && DateTime.UtcNow - c.FetchedUtc < SeriesCacheTtl)
            {
                return c.Data;
            }
        }

        try
        {
            var url = $"{BuildBaseUrl(config)}/series";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Sonarr GetSeries returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
                return new List<SonarrSeries>();
            }

            var content = await response.Content.ReadAsStringAsync();
            // Newtonsoft (not reflection-based System.Text.Json) so deserialization works
            // in the trimmed published build, which disables STJ reflection.
            var series = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SonarrSeries>>(content) ?? new List<SonarrSeries>();
            lock (_seriesCacheLock) { _seriesCache = (cacheKey, DateTime.UtcNow, series); }
            return series;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr series");
            return new List<SonarrSeries>();
        }
    }

    public virtual async Task<SonarrSeries?> GetSeriesByTitleAsync(string title, SonarrConfig config)
    {
        var series = await GetSeriesAsync(config);
        if (string.IsNullOrWhiteSpace(title) || series.Count == 0) return null;

        // 1) Exact (case-insensitive) on the primary title, clean title, or any alternate title.
        var exact = series.FirstOrDefault(s =>
            s.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
            s.CleanTitle?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
            (s.AlternateTitles?.Any(a => a.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true) ?? false));
        if (exact != null) return exact;

        // 2) Fuzzy fallback. CR titles frequently differ from Sonarr's (romaji vs english,
        //    punctuation, season/year suffixes). The old exact-only match silently failed for those,
        //    so UseSonarrNumbering fell back to Crunchyroll numbers. Score against the primary +
        //    alternate titles with the same StringSimilarity + 0.8 threshold as the history matcher.
        var needle = title.ToLowerInvariant();
        SonarrSeries? best = null;
        double bestSim = 0.0;
        foreach (var s in series)
        {
            double sim = s.Title != null ? StringSimilarity.CalculateSimilarity(s.Title.ToLowerInvariant(), needle) : 0.0;
            if (s.AlternateTitles != null)
            {
                foreach (var alt in s.AlternateTitles)
                {
                    if (string.IsNullOrEmpty(alt.Title)) continue;
                    var altSim = StringSimilarity.CalculateSimilarity(alt.Title.ToLowerInvariant(), needle);
                    if (altSim > sim) sim = altSim;
                }
            }
            if (sim > bestSim) { bestSim = sim; best = s; }
        }
        return bestSim >= 0.8 ? best : null;
    }

    public virtual async Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config)
    {
        try
        {
            var url = $"{BuildBaseUrl(config)}/episode?seriesId={seriesId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Sonarr GetEpisodes returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
                return new List<SonarrEpisode>();
            }

            var content = await response.Content.ReadAsStringAsync();
            // Newtonsoft (not reflection-based System.Text.Json) so deserialization works
            // in the trimmed published build, which disables STJ reflection.
            return Newtonsoft.Json.JsonConvert.DeserializeObject<List<SonarrEpisode>>(content) ?? new List<SonarrEpisode>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr episodes");
            return new List<SonarrEpisode>();
        }
    }
}

public class SonarrSeries
{
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
    [Newtonsoft.Json.JsonProperty("alternateTitles")]
    public List<SonarrAlternateTitle>? AlternateTitles { get; set; }
}

public class SonarrAlternateTitle
{
    public string? Title { get; set; }
}

public class SonarrSeason
{
    public int SeasonNumber { get; set; }
    public bool Monitored { get; set; }
    public int EpisodeCount { get; set; }
    public int TotalEpisodeCount { get; set; }
}

public class SonarrEpisode
{
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
