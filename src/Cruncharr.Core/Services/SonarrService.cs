using System.Net;
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
    Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config, bool forceRefresh);
    Task<SonarrEpisode?> GetEpisodeAsync(int episodeId, SonarrConfig config);
    Task<SonarrNamingConfig?> GetNamingConfigAsync(SonarrConfig config);
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

    // Sonarr identity is used to choose the final library path. A transient read failure must not
    // change that identity, so recent successful responses remain available as a stale fallback.
    // The request gate also coalesces concurrent batch-download cache misses instead of bursting
    // identical reads at Sonarr (the live failure was simultaneous connections reset by peer).
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxTransientAttempts = 3;
    private (string Key, DateTime FetchedUtc, List<SonarrSeries> Data)? _seriesCache;
    private (string Key, DateTime FetchedUtc, SonarrNamingConfig Data)? _namingCache;
    private readonly Dictionary<string, (DateTime FetchedUtc, List<SonarrEpisode> Data)> _episodeListCache = new();
    private readonly Dictionary<string, (DateTime FetchedUtc, SonarrEpisode Data)> _episodeCache = new();
    private readonly object _metadataCacheLock = new();
    private readonly SemaphoreSlim _metadataRequestGate = new(1, 1);

    private static string BuildCacheKey(SonarrConfig config) =>
        $"{config.UseSsl}|{config.Host}|{config.Port}|{config.UrlBase}|{config.ApiKey}";

    private static bool IsFresh(DateTime fetchedUtc) =>
        DateTime.UtcNow - fetchedUtc < MetadataCacheTtl;

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private async Task<HttpResponseMessage?> SendGetWithRetryAsync(
        string url,
        SonarrConfig config,
        string operation)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", config.ApiKey);

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode ||
                    !IsTransient(response.StatusCode) ||
                    attempt == MaxTransientAttempts)
                {
                    return response;
                }

                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(200 * attempt);
                _logger?.LogWarning(
                    "Sonarr {Operation} returned transient HTTP {Status}; retrying attempt {NextAttempt}/{MaxAttempts}",
                    operation, (int)response.StatusCode, attempt + 1, MaxTransientAttempts);
                response.Dispose();
                await Task.Delay(delay);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                if (attempt == MaxTransientAttempts) break;

                _logger?.LogWarning(
                    ex,
                    "Sonarr {Operation} transport failure; retrying attempt {NextAttempt}/{MaxAttempts}",
                    operation, attempt + 1, MaxTransientAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }

        _logger?.LogError(lastException, "Sonarr {Operation} failed after {Attempts} attempts", operation, MaxTransientAttempts);
        return null;
    }

    public virtual async Task<List<SonarrSeries>> GetSeriesAsync(SonarrConfig config)
    {
        var cacheKey = BuildCacheKey(config);
        lock (_metadataCacheLock)
        {
            if (_seriesCache is { } cached && cached.Key == cacheKey && IsFresh(cached.FetchedUtc))
            {
                return cached.Data;
            }
        }

        await _metadataRequestGate.WaitAsync();
        try
        {
            lock (_metadataCacheLock)
            {
                if (_seriesCache is { } cached && cached.Key == cacheKey && IsFresh(cached.FetchedUtc))
                {
                    return cached.Data;
                }
            }

            var url = $"{BuildBaseUrl(config)}/series";
            using var response = await SendGetWithRetryAsync(url, config, "series read");
            if (response?.IsSuccessStatusCode == true)
            {
                var content = await response.Content.ReadAsStringAsync();
                // Newtonsoft (not reflection-based System.Text.Json) so deserialization works
                // in the trimmed published build, which disables STJ reflection.
                var series = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SonarrSeries>>(content) ?? new List<SonarrSeries>();
                lock (_metadataCacheLock) { _seriesCache = (cacheKey, DateTime.UtcNow, series); }
                return series;
            }

            if (response != null)
            {
                _logger?.LogWarning("Sonarr GetSeries returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr series");
        }
        finally
        {
            _metadataRequestGate.Release();
        }

        lock (_metadataCacheLock)
        {
            if (_seriesCache is { } stale && stale.Key == cacheKey)
            {
                _logger?.LogWarning("Using last-known-good Sonarr series metadata after a failed refresh");
                return stale.Data;
            }
        }

        return new List<SonarrSeries>();
    }

    public virtual async Task<SonarrSeries?> GetSeriesByTitleAsync(string title, SonarrConfig config)
    {
        var series = await GetSeriesAsync(config);
        if (string.IsNullOrWhiteSpace(title) || series.Count == 0) return null;

        // 1) Exact (case-insensitive) on primary/clean/alternate identity after removing display
        // punctuation. Sonarr's CleanTitle is punctuation-free, while CR commonly changes ':' to
        // '-' or omits it entirely.
        var normalizedTitle = NormalizeTitleForMatch(title);
        var exact = series.FirstOrDefault(s =>
            s.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
            s.CleanTitle?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
            NormalizeTitleForMatch(s.Title) == normalizedTitle ||
            NormalizeTitleForMatch(s.CleanTitle) == normalizedTitle ||
            (s.AlternateTitles?.Any(a =>
                a.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true ||
                NormalizeTitleForMatch(a.Title) == normalizedTitle) ?? false));
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

    private static string NormalizeTitleForMatch(string? title) =>
        string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public virtual Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config) =>
        GetEpisodesAsync(seriesId, config, forceRefresh: false);

    public virtual async Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, SonarrConfig config, bool forceRefresh)
    {
        var configKey = BuildCacheKey(config);
        var listKey = $"{configKey}|series:{seriesId}";
        lock (_metadataCacheLock)
        {
            if (!forceRefresh && _episodeListCache.TryGetValue(listKey, out var cached) && IsFresh(cached.FetchedUtc))
            {
                return cached.Data;
            }
        }

        await _metadataRequestGate.WaitAsync();
        try
        {
            lock (_metadataCacheLock)
            {
                if (!forceRefresh && _episodeListCache.TryGetValue(listKey, out var cached) && IsFresh(cached.FetchedUtc))
                {
                    return cached.Data;
                }
            }

            var url = $"{BuildBaseUrl(config)}/episode?seriesId={seriesId}";
            using var response = await SendGetWithRetryAsync(url, config, $"episode-list read for series {seriesId}");
            if (response?.IsSuccessStatusCode == true)
            {
                var content = await response.Content.ReadAsStringAsync();
                var episodes = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SonarrEpisode>>(content) ?? new List<SonarrEpisode>();
                var fetchedUtc = DateTime.UtcNow;
                lock (_metadataCacheLock)
                {
                    _episodeListCache[listKey] = (fetchedUtc, episodes);
                    foreach (var episode in episodes)
                    {
                        _episodeCache[$"{configKey}|episode:{episode.Id}"] = (fetchedUtc, episode);
                    }
                }
                return episodes;
            }

            if (response != null)
            {
                _logger?.LogWarning("Sonarr GetEpisodes returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr episodes");
        }
        finally
        {
            _metadataRequestGate.Release();
        }

        lock (_metadataCacheLock)
        {
            if (_episodeListCache.TryGetValue(listKey, out var stale))
            {
                _logger?.LogWarning("Using last-known-good Sonarr episode metadata for series {SeriesId}", seriesId);
                return stale.Data;
            }
        }

        return new List<SonarrEpisode>();
    }

    public virtual async Task<SonarrEpisode?> GetEpisodeAsync(int episodeId, SonarrConfig config)
    {
        var configKey = BuildCacheKey(config);
        var episodeKey = $"{configKey}|episode:{episodeId}";
        lock (_metadataCacheLock)
        {
            if (_episodeCache.TryGetValue(episodeKey, out var cached) && IsFresh(cached.FetchedUtc))
            {
                return cached.Data;
            }
        }

        await _metadataRequestGate.WaitAsync();
        try
        {
            lock (_metadataCacheLock)
            {
                if (_episodeCache.TryGetValue(episodeKey, out var cached) && IsFresh(cached.FetchedUtc))
                {
                    return cached.Data;
                }
            }

            var url = $"{BuildBaseUrl(config)}/episode/{episodeId}";
            using var response = await SendGetWithRetryAsync(url, config, $"episode read {episodeId}");
            if (response?.IsSuccessStatusCode == true)
            {
                var content = await response.Content.ReadAsStringAsync();
                var episode = Newtonsoft.Json.JsonConvert.DeserializeObject<SonarrEpisode>(content);
                if (episode != null)
                {
                    lock (_metadataCacheLock) { _episodeCache[episodeKey] = (DateTime.UtcNow, episode); }
                }
                return episode;
            }

            if (response != null)
            {
                _logger?.LogWarning("Sonarr GetEpisode returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr episode {EpisodeId}", episodeId);
        }
        finally
        {
            _metadataRequestGate.Release();
        }

        lock (_metadataCacheLock)
        {
            if (_episodeCache.TryGetValue(episodeKey, out var stale))
            {
                _logger?.LogWarning("Using last-known-good Sonarr episode metadata for episode {EpisodeId}", episodeId);
                return stale.Data;
            }
        }

        return null;
    }

    public virtual async Task<SonarrNamingConfig?> GetNamingConfigAsync(SonarrConfig config)
    {
        var cacheKey = BuildCacheKey(config);
        lock (_metadataCacheLock)
        {
            if (_namingCache is { } cached && cached.Key == cacheKey && IsFresh(cached.FetchedUtc))
            {
                return cached.Data;
            }
        }

        await _metadataRequestGate.WaitAsync();
        try
        {
            lock (_metadataCacheLock)
            {
                if (_namingCache is { } cached && cached.Key == cacheKey && IsFresh(cached.FetchedUtc))
                {
                    return cached.Data;
                }
            }

            var url = $"{BuildBaseUrl(config)}/config/naming";
            using var response = await SendGetWithRetryAsync(url, config, "naming-config read");
            if (response?.IsSuccessStatusCode == true)
            {
                var content = await response.Content.ReadAsStringAsync();
                var naming = Newtonsoft.Json.JsonConvert.DeserializeObject<SonarrNamingConfig>(content);
                if (naming != null)
                {
                    lock (_metadataCacheLock) { _namingCache = (cacheKey, DateTime.UtcNow, naming); }
                }
                return naming;
            }

            if (response != null)
            {
                _logger?.LogWarning("Sonarr GetNamingConfig returned HTTP {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Sonarr naming configuration");
        }
        finally
        {
            _metadataRequestGate.Release();
        }

        lock (_metadataCacheLock)
        {
            if (_namingCache is { } stale && stale.Key == cacheKey)
            {
                _logger?.LogWarning("Using last-known-good Sonarr naming configuration after a failed refresh");
                return stale.Data;
            }
        }

        return null;
    }
}

public enum SonarrColonReplacementFormat
{
    Delete = 0,
    Dash = 1,
    SpaceDash = 2,
    SpaceDashSpace = 3,
    Smart = 4,
    Custom = 5
}

public class SonarrNamingConfig
{
    public static SonarrNamingConfig Default => new()
    {
        ReplaceIllegalCharacters = true,
        ColonReplacementFormat = SonarrColonReplacementFormat.Smart,
        StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}",
        SeriesFolderFormat = "{Series Title}",
        SeasonFolderFormat = "Season {season:00}",
        SpecialsFolderFormat = "Specials"
    };

    public bool RenameEpisodes { get; set; }
    public bool ReplaceIllegalCharacters { get; set; } = true;
    public SonarrColonReplacementFormat ColonReplacementFormat { get; set; } = SonarrColonReplacementFormat.Smart;
    public string CustomColonReplacementFormat { get; set; } = string.Empty;
    public int MultiEpisodeStyle { get; set; }
    public string? StandardEpisodeFormat { get; set; }
    public string? DailyEpisodeFormat { get; set; }
    public string? AnimeEpisodeFormat { get; set; }
    public string? SeriesFolderFormat { get; set; }
    public string? SeasonFolderFormat { get; set; }
    public string? SpecialsFolderFormat { get; set; }
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
