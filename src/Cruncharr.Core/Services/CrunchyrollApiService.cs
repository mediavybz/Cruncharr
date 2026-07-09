using System.Collections.Specialized;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Services;

public interface ICrunchyrollApiService
{
    Task<List<SeriesInfo>> SearchAsync(string query, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, string? crLocale = null, bool forced = false, CancellationToken cancellationToken = default);
    Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<EpisodeInfo?> GetEpisodeAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, DateTime? firstWeekDay, bool forcedLang = false, CancellationToken cancellationToken = default);

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

    // Methods ported from upstream CrEpisode
    Task<CrunchyRollEpisodeData> EpisodeDataAsync(EpisodeInfo episode, bool updateHistory = false, CancellationToken cancellationToken = default);
    CrunchyEpMeta EpisodeMeta(CrunchyRollEpisodeData episodeData, List<string> dubLang);

    // CRITICAL: Ported from upstream CrSeries.cs
    Dictionary<string, CrunchyEpMeta> ItemSelectMultiDub(Dictionary<string, EpisodeAndLanguage> eps, List<string> dubLang, bool? all, List<string>? e);
    Task<CrunchySeriesList?> ListSeriesIdAsync(string id, string crLocale, CrunchyMultiDownload? data, bool forcedLocale = false, CancellationToken cancellationToken = default);
}

public class CrunchyrollApiService : ICrunchyrollApiService, IDisposable
{
    private readonly ILogger<CrunchyrollApiService>? _logger;
    private readonly ICrunchyrollAuthService _authService;
    private readonly HttpClientWrapper _httpClient;

    public CrunchyrollApiService(ICrunchyrollAuthService authService, CruncharrConfig? config = null, ILogger<CrunchyrollApiService>? logger = null)
    {
        _authService = authService;
        _logger = logger;
        // Pass config so a configured proxy / FlareSolverr covers the Crunchyroll browse/episode
        // API calls, not just login. Without it these fetches went direct and bypassed the proxy.
        _httpClient = new HttpClientWrapper(config);
    }

    public async Task<List<SeriesInfo>> SearchAsync(string query, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Searching for: {Query}", query);

        // Discover endpoints must hit the beta-api host. The www host (ApiN) returns 403 for
        // the bearer token, so search is forced to beta exactly like GetAllSeries/Browse —
        // the incoming useBetaApi flag (the controller's "premium" default = false) would
        // otherwise route to the broken www host and every search came back empty.
        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return new List<SeriesInfo>();
        }

        var queryParams = new NameValueCollection{
            { "q", query },
            { "n", "20" },
            { "type", "series" }
        };

        var uriBuilder = new UriBuilder(ApiUrls.Search(true))
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("Search failed: {Error}", error);
            return new List<SeriesInfo>();
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrSearchResult>(content);
            if (result?.Data == null) return new List<SeriesInfo>();

            var seriesList = new List<SeriesInfo>();
            foreach (var group in result.Data)
            {
                if (group.Items == null) continue;
                foreach (var item in group.Items)
                {
                    seriesList.Add(new SeriesInfo
                    {
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse search results");
            return new List<SeriesInfo>();
        }
    }

    public async Task<SeriesInfo?> GetSeriesAsync(string seriesId, bool useBetaApi, string? crLocale = null, bool forced = false, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting series: {SeriesId}", seriesId);

        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken))
        {
            return null;
        }

        // Extract series ID from URL if needed
        var id = ExtractIdFromUrl(seriesId);

        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
            if (forced)
            {
                queryParams["force_locale"] = crLocale;
            }
        }

        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(useBetaApi)}/series/{id}")
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("Get series failed: {Error}", error);
            return null;
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsResponse<CrSeriesDetail>>(content);
            if (result?.Data == null) return null;

            var series = new SeriesInfo
            {
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse series data");
            return null;
        }
    }

    public async Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        var id = ExtractIdFromUrl(seriesId);
        var seasons = await GetSeasonsAsync(id, useBetaApi, cancellationToken);

        var allEpisodes = new List<EpisodeInfo>();
        foreach (var season in seasons)
        {
            var episodes = await GetSeasonEpisodesAsync(season.Id, useBetaApi, cancellationToken: cancellationToken);
            allEpisodes.AddRange(episodes);
        }

        return allEpisodes;
    }

    public async Task<EpisodeInfo?> GetEpisodeAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting episode: {EpisodeId}", episodeId);

        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken))
        {
            return null;
        }

        var id = ExtractIdFromUrl(episodeId);

        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/episodes/{id}",
            HttpMethod.Get,
            true,
            _authService.Token?.access_token);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("Get episode failed: {Error}", error);
            return null;
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            if (result?.Data == null || result.Data.Count == 0) return null;

            var ep = result.Data[0];
            var originalVersion = ep.Versions?.FirstOrDefault(v => v.Original);
            var guid = originalVersion?.Guid ?? ep.Id;
            return new EpisodeInfo
            {
                Id = ep.Id,
                Guid = guid,
                Title = ep.Title,
                Episode = ep.Episode,
                EpisodeNumber = ep.EpisodeNumber ?? 0,
                SeasonNumber = ep.SeasonNumber,
                Description = ep.Description,
                SeriesTitle = ep.SeriesTitle,
                SeriesId = ep.SeriesId,
                SeasonId = ep.SeasonId,
                SeasonTitle = ep.SeasonTitle,
                Locale = ep.AudioLocale ?? "ja-JP",
                AudioLocale = ep.AudioLocale ?? "ja-JP",
                IsPremium = ep.IsPremiumOnly,
                Versions = ep.Versions?.Select(v => new EpisodeVersion
                {
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse episode data");
            return null;
        }
    }

    public async Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        return await GetNewEpisodesAsync(crLocale, requestAmount, null, forcedLang, cancellationToken);
    }

    // [PT] Ported from upstream CrEpisode.GetNewEpisodes: page through results 100 at a time and
    // stop early once pages no longer contain episodes inside the requested calendar week
    public async Task<CrBrowseEpisodeBase?> GetNewEpisodesAsync(string? crLocale, int requestAmount, DateTime? firstWeekDay, bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        // Browse works with an anonymous/guest token (upstream uses CrAuthGuest here).
        // IsAuthenticated requires a logged-in account, so don't gate on it - any
        // access token is enough.
        if (!await EnsureTokenAsync(cancellationToken))
        {
            _logger?.LogError("Cannot fetch new episodes: no Crunchyroll access token available");
            return null;
        }

        if (string.IsNullOrEmpty(crLocale))
        {
            crLocale = "en-US";
        }
        else if (crLocale.Contains('-'))
        {
            // CR rejects a lowercase region (e.g. "en-us") with "Invalid request parameters";
            // normalize to xx-XX (lower language, UPPER region) - upstream always sends en-US.
            var parts = crLocale.Split('-');
            if (parts.Length == 2) crLocale = $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}";
        }

        var queryParams = new NameValueCollection();
        queryParams["locale"] = crLocale;
        if (forcedLang)
        {
            queryParams["force_locale"] = crLocale;
        }
        queryParams["sort_by"] = "newly_added";
        queryParams["type"] = "episode";

        if (requestAmount <= 0)
        {
            return new CrBrowseEpisodeBase { Data = new List<CrBrowseEpisode>() };
        }

        const int maxPageSize = 100;
        const int stalePageTolerance = 3;
        CrBrowseEpisodeBase? series = null;
        var episodes = new List<CrBrowseEpisode>();
        var stalePageCount = 0;
        var firstWeekDayDate = firstWeekDay?.Date;

        for (var start = 0; start < requestAmount; start += maxPageSize)
        {
            var pageSize = Math.Min(maxPageSize, requestAmount - start);
            queryParams["start"] = start.ToString();
            queryParams["n"] = pageSize.ToString();

            var uriBuilder = new UriBuilder(ApiUrls.Browse(true))
            {
                Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
            };

            var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

            if (!isOk)
            {
                _logger?.LogError("New episodes request failed for start '{Start}' and n '{PageSize}': {Error} | url={Url} | body={Body}",
                    start, pageSize, error, uriBuilder.Uri.PathAndQuery, (content ?? "").Length > 400 ? content!.Substring(0, 400) : content);
                return null;
            }

            CrBrowseEpisodeBase? page;
            try
            {
                page = JsonConvert.DeserializeObject<CrBrowseEpisodeBase>(content);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to parse new episodes data");
                return null;
            }

            series ??= page;

            if (page?.Data is not { Count: > 0 } pageData)
            {
                break;
            }

            episodes.AddRange(pageData);

            if (firstWeekDayDate.HasValue)
            {
                if (pageData.Any(episode => GetCalendarTargetDate(episode).Date >= firstWeekDayDate.Value))
                {
                    stalePageCount = 0;
                }
                else
                {
                    stalePageCount++;

                    if (stalePageCount >= stalePageTolerance)
                    {
                        break;
                    }
                }
            }

            if (pageData.Count < pageSize)
            {
                break;
            }
        }

        if (series == null)
        {
            return null;
        }

        series.Data = episodes;
        series.Data?.Sort((a, b) => b.EpisodeMetadata.PremiumAvailableDate.CompareTo(a.EpisodeMetadata.PremiumAvailableDate));
        return series;
    }

    // [PT] Ported from upstream CrEpisode.GetCalendarTargetDate
    private static DateTime GetCalendarTargetDate(CrBrowseEpisode episode)
    {
        DateTime episodeAirDate = episode.EpisodeMetadata.EpisodeAirDate.Kind == DateTimeKind.Utc
            ? episode.EpisodeMetadata.EpisodeAirDate.ToLocalTime()
            : episode.EpisodeMetadata.EpisodeAirDate;

        DateTime premiumAvailableStart = episode.EpisodeMetadata.PremiumAvailableDate.Kind == DateTimeKind.Utc
            ? episode.EpisodeMetadata.PremiumAvailableDate.ToLocalTime()
            : episode.EpisodeMetadata.PremiumAvailableDate;

        DateTime targetDate = premiumAvailableStart;
        DateTime oneYearFromNow = DateTime.Now.AddYears(1);

        if (targetDate >= oneYearFromNow)
        {
            DateTime freeAvailableStart = episode.EpisodeMetadata.FreeAvailableDate.Kind == DateTimeKind.Utc
                ? episode.EpisodeMetadata.FreeAvailableDate.ToLocalTime()
                : episode.EpisodeMetadata.FreeAvailableDate;

            targetDate = freeAvailableStart <= oneYearFromNow
                ? freeAvailableStart
                : episodeAirDate;
        }

        return targetDate;
    }

    /// <summary>
    /// Parses episode by ID with version deduplication.
    /// Ported from upstream CrEpisode.ParseEpisodeById.
    /// Handles duplicate audio locale versions by validating each one.
    /// </summary>
    public async Task<EpisodeInfo?> ParseEpisodeByIdAsync(string id, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Parsing episode by ID: {EpisodeId}, locale: {Locale}", id, crLocale);

        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return null;
        }

        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
            if (forcedLang)
            {
                queryParams["force_locale"] = crLocale;
            }
        }

        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/episodes/{id}")
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("ParseEpisodeById failed: {Error}", error);
            return null;
        }

        try
        {
            var episodeList = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);

            if (episodeList?.Data == null || episodeList.Total < 1)
            {
                _logger?.LogWarning("Episode not found: {EpisodeId}", id);
                return null;
            }

            var episode = episodeList.Data.First();

            // [PT] Ported from upstream: handle duplicate audio locale versions
            if (episodeList.Total == 1 && episode.Versions != null)
            {
                var duplicateGroups = episode.Versions
                    .GroupBy(v => v.AudioLocale)
                    .Where(g => g.Count() > 1)
                    .ToList();

                if (duplicateGroups.Count > 0)
                {
                    _logger?.LogWarning("Episode {EpisodeId} has duplicate audio locales, validating versions...", id);

                    foreach (var group in duplicateGroups)
                    {
                        foreach (var version in group.ToList())
                        {
                            var checkUriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/episodes/{version.Guid}")
                            {
                                Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
                            };

                            var checkRequest = HttpClientWrapper.CreateRequest(checkUriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
                            var (checkOk, _, _) = await _httpClient.SendRequestAsync(checkRequest);

                            if (!checkOk)
                            {
                                _logger?.LogWarning("Removing invalid version {VersionGuid} for locale {Locale}", version.Guid, version.AudioLocale);
                                episode.Versions.Remove(version);
                            }
                        }
                    }
                }
            }

            var originalVersion = episode.Versions?.FirstOrDefault(v => v.Original);
            var guid = originalVersion?.Guid ?? episode.Id;

            return new EpisodeInfo
            {
                Id = episode.Id,
                Guid = guid,
                Title = episode.Title,
                Episode = episode.Episode,
                EpisodeNumber = episode.EpisodeNumber ?? 0,
                SeasonNumber = episode.SeasonNumber,
                Description = episode.Description,
                SeriesTitle = episode.SeriesTitle,
                // [FIX] Carry the series/season identity through. Without these the download flow
                // could never get the real CR series_id, so History keyed every download by the
                // series TITLE -> the full-series populate (ParseSeriesById) and per-episode Sonarr
                // match both failed. CMS /episodes/{id} returns these at top level.
                SeriesId = episode.SeriesId,
                SeasonId = episode.SeasonId,
                SeasonTitle = episode.SeasonTitle,
                Locale = episode.AudioLocale ?? "ja-JP",
                AudioLocale = episode.AudioLocale ?? "ja-JP",
                IsPremium = episode.IsPremiumOnly,
                Versions = episode.Versions?.Select(v => new EpisodeVersion
                {
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse episode data for {EpisodeId}", id);
            return null;
        }
    }

    /// <summary>
    /// Marks an episode as watched on Crunchyroll.
    /// Ported from upstream CrEpisode.MarkAsWatched.
    /// </summary>
    public async Task MarkAsWatchedAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Marking episode as watched: {EpisodeId}", episodeId);

        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            _logger?.LogWarning("Cannot mark as watched: not authenticated");
            return;
        }

        if (_authService.Token?.account_id == null)
        {
            _logger?.LogWarning("Cannot mark as watched: no account ID");
            return;
        }

        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Content(true)}/discover/{_authService.Token.account_id}/mark_as_watched/{episodeId}",
            HttpMethod.Post,
            true,
            _authService.Token.access_token);

        var (isOk, _, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("MarkAsWatched failed: {Error}", error);
        }
        else
        {
            _logger?.LogInformation("Marked episode {EpisodeId} as watched", episodeId);
        }
    }

    private async Task<List<SeasonInfo>> GetSeasonsAsync(string seriesId, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken))
        {
            return new List<SeasonInfo>();
        }

        var request = HttpClientWrapper.CreateRequest(
            $"{ApiUrls.Cms(useBetaApi)}/series/{seriesId}/seasons",
            HttpMethod.Get,
            true,
            _authService.Token?.access_token);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk) return new List<SeasonInfo>();

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrSeasonDetail>>(content);
            if (result?.Data == null) return new List<SeasonInfo>();

            return result.Data.Select(s => new SeasonInfo
            {
                Id = s.Id,
                Title = s.Title,
                SeasonNumber = s.SeasonNumber
            }).ToList();
        }
        catch
        {
            return new List<SeasonInfo>();
        }
    }

    private async Task<List<EpisodeInfo>> GetSeasonEpisodesAsync(string seasonId, bool useBetaApi, string? crLocale = null, bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(useBetaApi, cancellationToken))
        {
            return new List<EpisodeInfo>();
        }

        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
            if (forcedLang)
            {
                queryParams["force_locale"] = crLocale;
            }
        }

        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(useBetaApi)}/seasons/{seasonId}/episodes")
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk) return new List<EpisodeInfo>();

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            if (result?.Data == null) return new List<EpisodeInfo>();

            return result.Data.Select(e =>
            {
                var originalVersion = e.Versions?.FirstOrDefault(v => v.Original);
                var guid = originalVersion?.Guid ?? e.Id;
                return new EpisodeInfo
                {
                    Id = e.Id,
                    Guid = guid,
                    Title = e.Title,
                    Episode = e.Episode,
                    EpisodeNumber = e.EpisodeNumber ?? 0,
                    SeasonNumber = e.SeasonNumber,
                    SeasonTitle = e.SeasonTitle,
                    SeasonId = e.SeasonId,
                    Description = e.Description,
                    SeriesTitle = e.SeriesTitle,
                    // [FIX] Without SeriesId the full-series populate produced episodes with a null
                    // series id, so UpdateWithSeasonData (which groups by series id) silently dropped
                    // every episode -> History never showed the full season, only the downloaded one.
                    SeriesId = e.SeriesId,
                    Locale = e.AudioLocale ?? "ja-JP",
                    AudioLocale = e.AudioLocale ?? "ja-JP",
                    IsPremium = e.IsPremiumOnly,
                    Images = ExtractImageUrls(e.Images),
                    ThumbnailUrl = ExtractBestImage(e.Images, "thumbnail") ?? ExtractBestImage(e.Images, "episode_thumbnail"),
                    CoverArtUrl = ExtractBestImage(e.Images, "poster_tall"),
                    Versions = e.Versions?.Select(v => new EpisodeVersion
                    {
                        AudioLocale = v.AudioLocale,
                        Guid = v.Guid,
                        MediaGuid = v.MediaGuid,
                        Original = v.Original,
                        SeasonGuid = v.SeasonGuid
                    }).ToList(),
                    SubtitleLocales = e.SubtitleLocales ?? new List<string>()
                };
            }).ToList();
        }
        catch
        {
            return new List<EpisodeInfo>();
        }
    }

    public async Task<List<SeasonInfo>> ParseSeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return new List<SeasonInfo>();
        }

        var queryParams = new NameValueCollection{
            { "preferred_audio_language", "ja-JP" }
        };
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
            if (forced)
            {
                queryParams["force_locale"] = crLocale;
            }
        }

        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/series/{id}/seasons")
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("ParseSeriesById failed: {Error}", error);
            return new List<SeasonInfo>();
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrSeasonDetail>>(content);
            if (result?.Data == null) return new List<SeasonInfo>();

            return result.Data.Select(s => new SeasonInfo
            {
                Id = s.Id,
                Title = s.Title,
                SeasonNumber = s.SeasonNumber,
                Identifier = s.Identifier
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse series seasons");
            return new List<SeasonInfo>();
        }
    }

    public async Task<List<EpisodeInfo>> GetSeasonDataByIdAsync(string seasonId, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        return await GetSeasonEpisodesAsync(seasonId, true, crLocale, forcedLang, cancellationToken);
    }

    public async Task<SeriesInfo?> SeriesByIdAsync(string id, string? crLocale, bool forced = false, CancellationToken cancellationToken = default)
    {
        return await GetSeriesAsync(id, true, crLocale, forced, cancellationToken);
    }

    // [PT] Ported from upstream CrSeries.GetEpisodeLabelFromKey
    private static string GetEpisodeLabelFromKey(string key)
    {
        if (key.StartsWith("SP"))
            return key;

        var separatorIndex = key.LastIndexOf('E');
        return separatorIndex >= 0 && separatorIndex < key.Length - 1
            ? key[(separatorIndex + 1)..]
            : key;
    }

    // [PT] Ported from upstream CrunchyEpisode.IsRegularEpisodeNumber
    private static bool IsRegularEpisodeNumber(string? episode)
    {
        return !string.IsNullOrWhiteSpace(episode) &&
               Regex.IsMatch(episode, @"^\d+(\.\d+)?(\s*-\s*\d+(\.\d+)?)?$");
    }

    // [PT] Upstream v1.6.14 fix ("special season detection incorrectly identifying some regular
    // seasons as specials"): a regular sequential episode whose text label is a non-numeric saga
    // code (e.g. One Piece "FMI1") must NOT be treated as a special. CR still provides a valid
    // integer episode_number for those; true OVAs/specials have episode_number 0 or null.
    internal static bool IsSpecialEpisode(string? episodeLabel, int? episodeNumber)
    {
        if (string.IsNullOrEmpty(episodeLabel)) return false;
        if (IsRegularEpisodeNumber(episodeLabel)) return false;
        if (episodeNumber.HasValue && episodeNumber.Value > 0) return false;
        return true;
    }

    // CRITICAL: Ported from upstream CrSeries.ItemSelectMultiDub
    public Dictionary<string, CrunchyEpMeta> ItemSelectMultiDub(Dictionary<string, EpisodeAndLanguage> eps, List<string> dubLang, bool? all, List<string>? e)
    {
        var ret = new Dictionary<string, CrunchyEpMeta>();

        var hasPremium = _authService.Profile?.HasPremium ?? false;
        var hslang = "none"; // Use default, could be fetched from config if needed

        bool ShouldInclude(string checkKey) =>
            all is true || (e != null && e.Contains(checkKey));

        foreach (var (key, episode) in eps)
        {
            var epNum = GetEpisodeLabelFromKey(key);

            foreach (var v in episode.Variants)
            {
                var item = v.Item;
                var lang = v.Lang;

                // Skip variants with missing data
                if (item == null || string.IsNullOrEmpty(item.Id))
                {
                    _logger?.LogWarning("Skipping variant with missing item data for key {Key}", key);
                    continue;
                }

                item.SeqId = epNum;

                if (item.IsPremiumOnly && !hasPremium)
                {
                    _logger?.LogWarning("Episode is premium only - skipping {EpisodeId}", item.Id);
                    continue;
                }

                // history override could be added here if HistoryService is injected
                var effectiveDubs = dubLang ?? new List<string>();

                if (string.IsNullOrEmpty(lang.CrLocale) || !effectiveDubs.Contains(lang.CrLocale))
                    continue;

                // season title fallbacks
                item.HideSeasonTitle = true;
                if (string.IsNullOrEmpty(item.SeasonTitle) && !string.IsNullOrEmpty(item.SeriesTitle))
                {
                    item.SeasonTitle = item.SeriesTitle;
                    item.HideSeasonTitle = false;
                    item.HideSeasonNumber = true;
                }

                if (string.IsNullOrEmpty(item.SeasonTitle) && string.IsNullOrEmpty(item.SeriesTitle))
                {
                    item.SeasonTitle = "NO_TITLE";
                    item.SeriesTitle = "NO_TITLE";
                }

                // selection gate
                if (!ShouldInclude(key))
                    continue;

                // Create base queue item once per key
                if (!ret.TryGetValue(key, out var qItem))
                {
                    var seriesTitle = DownloadQueueItemFactory.CanonicalTitle(
                        episode.Variants.Where(x => x.Item != null).Select(x => (string?)x.Item.SeriesTitle));

                    var seasonTitle = DownloadQueueItemFactory.CanonicalTitle(
                        episode.Variants.Where(x => x.Item != null).Select(x => (string?)x.Item.SeasonTitle));

                    var (img, imgBig) = DownloadQueueItemFactory.GetThumbSmallBig(item.Images);

                    var selectedDubs = effectiveDubs
                        .Where(d => episode.Variants.Any(x => !string.IsNullOrEmpty(x.Lang.CrLocale) && x.Lang.CrLocale == d))
                        .ToList();

                    qItem = DownloadQueueItemFactory.CreateShell(
                        service: StreamingService.Crunchyroll,
                        seriesTitle: seriesTitle,
                        seasonTitle: seasonTitle,
                        episodeNumber: item.Episode,
                        episodeTitle: item.Title,
                        description: item.Description,
                        episodeId: item.Id,
                        seriesId: item.SeriesId,
                        seasonId: item.SeasonId,
                        season: Helpers.ExtractNumberAfterS(item.Identifier) ?? item.SeasonNumber.ToString(),
                        absolutEpisodeNumberE: epNum,
                        image: img,
                        imageBig: imgBig,
                        hslang: hslang,
                        availableSubs: item.SubtitleLocales ?? new List<string>(),
                        selectedDubs: selectedDubs
                    );

                    ret.Add(key, qItem);
                }

                // playback preference
                var playback = item.Playback;
                if (!string.IsNullOrEmpty(item.StreamsLink))
                {
                    playback = item.StreamsLink;
                    if (string.IsNullOrEmpty(item.Playback))
                        item.Playback = item.StreamsLink;
                }

                // Add variant
                ret[key].Data.Add(DownloadQueueItemFactory.CreateVariant(
                    mediaId: item.Id,
                    lang: lang,
                    playback: playback,
                    versions: item.Versions?.Select(v => new EpisodeVersion
                    {
                        AudioLocale = v.AudioLocale,
                        Guid = v.Guid,
                        Original = v.Original,
                        SeasonGuid = v.SeasonGuid
                    }).ToList(),
                    isSubbed: item.IsSubbed,
                    isDubbed: item.IsDubbed
                ));
            }
        }

        return ret;
    }

    // CRITICAL: Ported from upstream CrSeries.ListSeriesId
    public async Task<CrunchySeriesList?> ListSeriesIdAsync(string id, string crLocale, CrunchyMultiDownload? data, bool forcedLocale = false, CancellationToken cancellationToken = default)
    {
        bool serieshasversions = true;

        var parsedSeries = await ParseSeriesByIdAsync(id, crLocale, forcedLocale, cancellationToken);

        if (parsedSeries == null || parsedSeries.Count == 0)
        {
            _logger?.LogError("Parse Data Invalid for series {SeriesId}", id);
            return null;
        }

        var episodes = new Dictionary<string, EpisodeAndLanguage>();

        var cachedSeasonId = "";
        List<CrEpisodeDetail>? seasonData = null;

        foreach (var s in parsedSeries)
        {
            if (data?.S != null && s.Id != data.S)
                continue;

            int fallbackIndex = 0;

            if (cachedSeasonId != s.Id)
            {
                seasonData = await GetSeasonEpisodesRawAsync(s.Id, forcedLocale ? crLocale : "", forcedLocale, cancellationToken);
                cachedSeasonId = s.Id;
            }

            if (seasonData == null)
                continue;

            foreach (var episode in seasonData)
            {
                string episodeNum =
                    (episode.Episode != string.Empty ? episode.Episode : (episode.EpisodeNumber != null ? episode.EpisodeNumber + "" : $"F{fallbackIndex++}"))
                    ?? string.Empty;

                var seasonIdentifier = !string.IsNullOrEmpty(s.Identifier)
                    ? (s.Identifier.Split('|').Length > 1 ? s.Identifier.Split('|')[1] : $"S{episode.SeasonNumber}")
                    : $"S{episode.SeasonNumber}";

                var episodeKey = $"{seasonIdentifier}E{episodeNum}";

                if (!episodes.TryGetValue(episodeKey, out var item))
                {
                    item = new EpisodeAndLanguage();
                    episodes[episodeKey] = item;
                }

                if (episode.Versions != null)
                {
                    foreach (var version in episode.Versions)
                    {
                        var lang = Array.Find(Languages.languages, a => a.CrLocale == version.AudioLocale) ?? Languages.DEFAULT_lang;
                        item.AddUnique(episode, lang);
                    }
                }
                else
                {
                    serieshasversions = false;
                    var lang = Array.Find(Languages.languages, a => a.CrLocale == episode.AudioLocale) ?? Languages.DEFAULT_lang;
                    item.AddUnique(episode, lang);
                }
            }
        }

        int specialIndex = 1;
        int epIndex = 1;

        var keys = new List<string>(episodes.Keys);

        foreach (var key in keys)
        {
            var item = episodes[key];
            if (item.Variants.Count == 0)
                continue;

            var baseEp = item.Variants[0].Item;

            var epStr = baseEp.Episode;
            var hasRealEpisodeNumber = baseEp.EpisodeNumber.HasValue && baseEp.EpisodeNumber.Value > 0;
            var isSpecial = IsSpecialEpisode(epStr, baseEp.EpisodeNumber);

            string newKey;
            if (isSpecial && !string.IsNullOrEmpty(baseEp.Episode))
            {
                newKey = $"SP{specialIndex}_" + baseEp.Episode;
            }
            else
            {
                // [PT] Upstream: keep the season prefix and use the real episode label
                // (supports multi-episode ranges like "11-12"). When the label is a non-numeric
                // saga code, fall back to the real episode_number so it keys as a normal episode.
                var episodeLabel = IsRegularEpisodeNumber(baseEp.Episode)
                    ? baseEp.Episode
                    : (hasRealEpisodeNumber ? baseEp.EpisodeNumber!.Value.ToString() : epIndex.ToString());
                var separatorIndex = key.LastIndexOf('E');
                var keyPrefix = separatorIndex > 0 ? key[..separatorIndex] : string.Empty;
                newKey = $"{keyPrefix}E{episodeLabel}";
            }

            episodes.Remove(key);

            int counter = 1;
            string originalKey = newKey;
            while (episodes.ContainsKey(newKey))
            {
                newKey = originalKey + "_" + counter;
                counter++;
            }

            episodes.Add(newKey, item);

            if (isSpecial) specialIndex++;
            else epIndex++;
        }

        var normal = episodes.Where(kvp => !kvp.Key.StartsWith("SP")).ToList();
        var specials = episodes.Where(kvp => kvp.Key.StartsWith("SP")).ToList();

        var sortedEpisodes = new Dictionary<string, EpisodeAndLanguage>(normal.Concat(specials));

        foreach (var kvp in sortedEpisodes)
        {
            var key = kvp.Key;
            var item = kvp.Value;

            if (item.Variants.Count == 0)
                continue;

            var baseEp = item.Variants[0].Item;

            var seasonTitle = DownloadQueueItemFactory.CanonicalTitle(
                item.Variants.Select(v => v.Item.SeasonTitle)
            );

            var title = baseEp.Title;
            var seasonNumber = Helpers.ExtractNumberAfterS(baseEp.Identifier) ?? baseEp.SeasonNumber.ToString();

            var languages = item.Variants
                .Select(v => $"{(v.Item.IsPremiumOnly ? "+ " : "")}{v.Lang?.Name ?? "Unknown"}")
                .ToArray();

            _logger?.LogInformation("[{Key}] {SeasonTitle} - Season {SeasonNumber} - {Title} [{Languages}]",
                key, seasonTitle, seasonNumber, title, string.Join(", ", languages));
        }

        if (!serieshasversions)
            _logger?.LogWarning("Couldn't find versions on some episodes, added languages with language array.");

        var crunchySeriesList = new CrunchySeriesList
        {
            Data = sortedEpisodes
        };

        crunchySeriesList.List = sortedEpisodes.Select(kvp =>
        {
            var key = kvp.Key;
            var value = kvp.Value;

            if (value.Variants.Count == 0)
            {
                return new EpisodeDisplay
                {
                    E = key,
                    Lang = new List<string>(),
                    Name = string.Empty,
                    Season = string.Empty,
                    SeriesTitle = string.Empty,
                    SeasonTitle = string.Empty,
                    EpisodeNum = key,
                    Id = string.Empty,
                    Img = string.Empty,
                    Description = string.Empty,
                    EpisodeType = EpisodeType.Episode,
                    Time = "0:00"
                };
            }

            var baseEp = value.Variants[0].Item;

            var thumbRow = baseEp.Images?.ContainsKey("thumbnail") == true
                ? baseEp.Images["thumbnail"]?.FirstOrDefault()
                : null;
            string img = "/notFound.jpg";
            if (thumbRow != null && thumbRow.Count > 0)
            {
                var firstImg = thumbRow[0];
                if (firstImg is string s)
                {
                    img = s;
                }
                else if (firstImg is Newtonsoft.Json.Linq.JObject jo)
                {
                    var source = jo["source"]?.ToString();
                    if (!string.IsNullOrEmpty(source)) img = source;
                }
                else if (firstImg is System.Collections.Generic.Dictionary<string, object> dict)
                {
                    if (dict.TryGetValue("source", out var sourceObj) && sourceObj is string sourceStr)
                    {
                        img = sourceStr;
                    }
                }
            }

            var seconds = (int)Math.Floor((baseEp.DurationMs) / 1000.0);

            var langList = value.Variants
                .Select(v => v.Lang.CrLocale)
                .Distinct()
                .ToList();

            Languages.SortListByLangList(langList);

            return new EpisodeDisplay
            {
                E = key,
                Lang = langList,
                Name = baseEp.Title ?? string.Empty,
                Season = (Helpers.ExtractNumberAfterS(baseEp.Identifier) ?? baseEp.SeasonNumber.ToString()) ?? string.Empty,
                SeriesTitle = DownloadQueueItemFactory.StripDubSuffix(baseEp.SeriesTitle),
                SeasonTitle = DownloadQueueItemFactory.StripDubSuffix(baseEp.SeasonTitle),
                EpisodeNum = key.StartsWith("SP")
                    ? key
                    : GetEpisodeLabelFromKey(key),
                Id = baseEp.SeasonId ?? string.Empty,
                Img = img,
                Description = baseEp.Description ?? string.Empty,
                EpisodeType = EpisodeType.Episode,
                Time = $"{seconds / 60}:{seconds % 60:D2}"
            };
        }).ToList();

        return crunchySeriesList;
    }

    // Helper to get raw season episodes for ListSeriesId
    private async Task<List<CrEpisodeDetail>> GetSeasonEpisodesRawAsync(string seasonId, string? crLocale, bool forcedLang, CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return new List<CrEpisodeDetail>();
        }

        var queryParams = new NameValueCollection();
        queryParams["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
            if (forcedLang)
            {
                queryParams["force_locale"] = crLocale;
            }
        }

        var uriBuilder = new UriBuilder($"{ApiUrls.Cms(true)}/seasons/{seasonId}/episodes")
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("GetSeasonEpisodesRaw failed: {Error}", error);
            return new List<CrEpisodeDetail>();
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrCmsListResponse<CrEpisodeDetail>>(content);
            return result?.Data ?? new List<CrEpisodeDetail>();
        }
        catch
        {
            return new List<CrEpisodeDetail>();
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync(bool useBetaApi, CancellationToken cancellationToken)
    {
        if (!_authService.IsAuthenticated)
        {
            return await _authService.AuthenticateAsync(useBetaApi, cancellationToken);
        }
        return true;
    }

    // Anonymous-capable endpoints (browse/new episodes) only need an access token,
    // not a logged-in account. AuthenticateAsync returns false in anonymous mode
    // even when the guest token was obtained successfully.
    private async Task<bool> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_authService.Token?.access_token == null)
        {
            await _authService.AuthenticateAsync(true, cancellationToken);
        }
        return _authService.Token?.access_token != null;
    }

    private static string ExtractIdFromUrl(string input)
    {
        if (input.StartsWith("http"))
        {
            var parts = input.Split('/');
            if (parts.Length == 0) return input;
            return parts.Last().Split('?')[0];
        }
        return input;
    }

    private static List<string> ExtractImageUrls(Dictionary<string, List<List<object>>>? images)
    {
        var urls = new List<string>();
        if (images == null) return urls;

        foreach (var kvp in images)
        {
            if (kvp.Value != null)
            {
                foreach (var list in kvp.Value)
                {
                    if (list != null && list.Count > 0)
                    {
                        var url = ExtractImageSource(list[0]);
                        if (!string.IsNullOrEmpty(url))
                        {
                            urls.Add(url);
                        }
                    }
                }
            }
        }
        return urls;
    }

    // Retina-safe ceiling for catalog thumbnails/posters. CR offers variants up to 1280-1920px,
    // but grids render them at ~150-300px, so ~480px stays sharp on 2x displays while cutting the
    // fetched+cached bytes by roughly half (measured: a 750px poster PNG = 1.2MB vs 480px = 0.57MB).
    // We pick from CR's OFFERED variants only (never rewrite the size in the URL — CR's resizer
    // rejects non-preset sizes, e.g. 360x540 -> error, so a rewrite would break images).
    private const int TargetImageWidth = 480;

    private static string? ExtractBestImage(Dictionary<string, List<List<object>>>? images, string type)
    {
        if (images == null || !images.ContainsKey(type)) return null;

        var imageList = images[type];
        if (imageList == null || imageList.Count == 0) return null;

        // CR stores each image as an array of size variants ordered small -> large, and the
        // largest can be 1280-1920px - far more than a ~150-300px thumbnail/poster needs, which
        // bloated the on-disk cache and slowed first paint. Pick the SMALLEST variant whose width
        // is >= the retina-safe target, falling back to the largest available if none reach it
        // (so it never regresses to the old blurry img[0]).
        string? chosen = null;
        int chosenWidth = int.MaxValue;
        string? largest = null;
        int largestWidth = -1;
        foreach (var img in imageList)
        {
            if (img == null) continue;
            foreach (var variant in img)
            {
                var (url, width) = ExtractImageSourceAndWidth(variant);
                if (string.IsNullOrEmpty(url)) continue;
                if (width > largestWidth) { largest = url; largestWidth = width; }
                if (width >= TargetImageWidth && width < chosenWidth) { chosen = url; chosenWidth = width; }
            }
        }
        return chosen ?? largest;
    }

    private static (string? source, int width) ExtractImageSourceAndWidth(object? imageObj)
    {
        var url = ExtractImageSource(imageObj);
        int width = 0;
        try
        {
            JObject? jObj = imageObj as JObject;
            if (jObj == null)
            {
                var json = imageObj?.ToString();
                if (!string.IsNullOrEmpty(json) && json.StartsWith("{")) jObj = JObject.Parse(json);
            }
            if (jObj != null) width = jObj["width"]?.ToObject<int>() ?? 0;
        }
        catch { /* width stays 0 */ }
        return (url, width);
    }

    private static string? ExtractImageSource(object? imageObj)
    {
        if (imageObj == null) return null;

        // If it's already a string, return it
        if (imageObj is string str) return str;

        // If it's a JObject, extract the source property
        if (imageObj is JObject jObj)
        {
            return jObj["source"]?.ToString();
        }

        // Try to parse as JSON
        try
        {
            var json = imageObj.ToString();
            if (!string.IsNullOrEmpty(json) && json.StartsWith("{"))
            {
                var jObj2 = JObject.Parse(json);
                return jObj2["source"]?.ToString();
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return imageObj.ToString();
    }

    // Ported from upstream CrSeries.GetAllSeries
    public async Task<List<SeriesInfo>> GetAllSeriesAsync(string? crLocale = null, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting all series");

        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return new List<SeriesInfo>();
        }

        var complete = new List<SeriesInfo>();
        var total = 0;
        var i = 0;

        do
        {
            var queryParams = new NameValueCollection{
                { "start", i.ToString() },
                { "n", "50" },
                { "sort_by", "alphabetical" }
            };

            if (!string.IsNullOrEmpty(crLocale))
            {
                queryParams["locale"] = crLocale;
            }

            var uriBuilder = new UriBuilder(ApiUrls.Browse(true))
            {
                Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
            };

            var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

            if (!isOk)
            {
                _logger?.LogError("GetAllSeries request failed: {Error}", error);
                return complete;
            }

            try
            {
                var result = JsonConvert.DeserializeObject<CrBrowseSeriesBase>(content);
                if (result?.Data == null) break;

                total = result.Total;
                foreach (var item in result.Data)
                {
                    complete.Add(new SeriesInfo
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Description = item.Description,
                        Images = ExtractImageUrls(item.Images),
                        CoverArtUrl = ExtractBestImage(item.Images, "poster_tall"),
                        ThumbnailUrl = ExtractBestImage(item.Images, "poster_wide"),
                        AudioLocales = item.SeriesMetadata?.AudioLocales ?? new List<string>(),
                        MaturityRatings = item.SeriesMetadata?.MaturityRatings ?? new List<string>()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to parse GetAllSeries results");
                break;
            }

            i += 50;
        } while (i < total);

        return complete;
    }

    // Ported from upstream CrSeries.GetSeasonalSeries
    private static readonly HttpClient _anilistClient = new HttpClient();
    private const string AnilistSeasonQuery =
        "query($season: MediaSeason, $year: Int, $page: Int){ Page(page:$page){ pageInfo{ hasNextPage } " +
        "media(season:$season, seasonYear:$year, type:ANIME, isAdult:false, sort:TITLE_ENGLISH){ " +
        "id title{ romaji english native } episodes description coverImage{ extraLarge } " +
        "startDate{ year month day } nextAiringEpisode{ episode airingAt } " +
        "externalLinks{ site url } } } }";

    /// <summary>
    /// Seasonal anime lineup. Mirrors upstream: the full season is pulled from AniList
    /// (complete list + high-res covers), filtered to titles that link to Crunchyroll,
    /// then merged with Crunchyroll's own seasonal_tag browse to catch anything AniList
    /// didn't link. CR-catalogued titles AniList missed are still included.
    /// </summary>
    public async Task<List<SeriesInfo>> GetSeasonalSeriesAsync(string season, string year, string? crLocale = null, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting seasonal series: {Season} {Year}", season, year);

        var bySeries = new Dictionary<string, SeriesInfo>(StringComparer.Ordinal); // keyed by CR series id
        int.TryParse(year, out var yearInt);

        // 1) AniList - complete seasonal lineup with high-res covers, CR-linked only.
        try
        {
            int page = 1;
            bool hasNext;
            do
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    query = AnilistSeasonQuery,
                    variables = new { season = season.ToUpperInvariant(), year = yearInt, page }
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Anilist)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using var resp = await _anilistClient.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("AniList seasonal request failed: {Status}", resp.StatusCode);
                    break;
                }
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                var pageNode = JObject.Parse(body)["data"]?["Page"];
                hasNext = pageNode?["pageInfo"]?["hasNextPage"]?.ToObject<bool>() ?? false;
                foreach (var m in pageNode?["media"] as JArray ?? new JArray())
                {
                    var crLink = (m["externalLinks"] as JArray)?.FirstOrDefault(l =>
                        string.Equals(l["site"]?.ToString(), "Crunchyroll", StringComparison.OrdinalIgnoreCase));
                    var url = crLink?["url"]?.ToString();
                    if (string.IsNullOrEmpty(url)) continue; // not on Crunchyroll at all

                    // Resolve the CR series id from the link, following redirects when the
                    // link isn't a clean /series/<id> URL (upstream does the same HEAD hop).
                    // Titles whose id can't be resolved are STILL listed (just not clickable)
                    // so the lineup matches upstream's count.
                    var crId = await ResolveCrunchyrollSeriesIdAsync(url, cancellationToken);

                    var title = m["title"]?["english"]?.ToString();
                    if (string.IsNullOrEmpty(title)) title = m["title"]?["romaji"]?.ToString();
                    if (string.IsNullOrEmpty(title)) title = m["title"]?["native"]?.ToString();
                    var cover = m["coverImage"]?["extraLarge"]?.ToString();

                    // Air dates ("as JObject" so a JSON null field doesn't throw on indexing).
                    var sd = m["startDate"] as JObject;
                    string? startDate = null;
                    var sy = sd?["year"]?.ToObject<int?>();
                    if (sy.HasValue)
                    {
                        var smo = sd?["month"]?.ToObject<int?>() ?? 1;
                        var sdy = sd?["day"]?.ToObject<int?>() ?? 1;
                        startDate = $"{sy.Value:D4}-{smo:D2}-{sdy:D2}";
                    }
                    var nae = m["nextAiringEpisode"] as JObject;
                    int? nextEp = nae?["episode"]?.ToObject<int?>();
                    DateTime? nextAir = null;
                    var airAt = nae?["airingAt"]?.ToObject<long?>();
                    if (airAt.HasValue) nextAir = DateTimeOffset.FromUnixTimeSeconds(airAt.Value).UtcDateTime;

                    var info = new SeriesInfo
                    {
                        Id = crId ?? "",
                        Title = title ?? "",
                        Description = StripHtmlTags(m["description"]?.ToString()),
                        CoverArtUrl = cover,
                        ThumbnailUrl = cover,
                        EpisodeCount = m["episodes"]?.ToObject<int?>(),
                        OnCrunchyroll = true,
                        StartDate = startDate,
                        NextEpisodeNumber = nextEp,
                        NextAirUtc = nextAir
                    };
                    var key = !string.IsNullOrEmpty(crId) ? crId : ("anilist:" + (m["id"]?.ToString() ?? title ?? Guid.NewGuid().ToString()));
                    bySeries[key] = info;
                }
                page++;
            } while (hasNext && page <= 10);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AniList seasonal fetch failed for {Season} {Year}", season, year);
        }

        // 2) Crunchyroll seasonal_tag browse - add CR titles AniList didn't link.
        foreach (var cr in await GetSeasonalFromCrunchyrollAsync(season, year, crLocale, cancellationToken))
        {
            if (!bySeries.ContainsKey(cr.Id))
            {
                cr.OnCrunchyroll = true;
                bySeries[cr.Id] = cr;
            }
        }

        return bySeries.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? StripHtmlTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        return Regex.Replace(html, "<.*?>", string.Empty).Trim();
    }

    // Extract the Crunchyroll series id from an external link. If the URL isn't a
    // clean /series/<id> form, follow redirects (HEAD) and re-check the final URL.
    private async Task<string?> ResolveCrunchyrollSeriesIdAsync(string url, CancellationToken cancellationToken)
    {
        var m = Regex.Match(url, @"series/([^/?#]+)");
        if (m.Success) return m.Groups[1].Value;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _anilistClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var m2 = Regex.Match(finalUrl, @"series/([^/?#]+)");
            if (m2.Success) return m2.Groups[1].Value;
        }
        catch { /* unresolved - title still listed, just not clickable */ }
        return null;
    }

    private async Task<List<SeriesInfo>> GetSeasonalFromCrunchyrollAsync(string season, string year, string? crLocale, CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(true, cancellationToken))
        {
            return new List<SeriesInfo>();
        }

        var queryParams = new NameValueCollection{
            { "seasonal_tag", $"{season.ToLower()}-{year}" },
            // Upstream uses n=100 (CrSeries.GetSeasonalSeries); the browse endpoint
            // rejects larger page sizes with 400 Bad Request.
            { "n", "100" }
        };
        if (!string.IsNullOrEmpty(crLocale))
        {
            queryParams["locale"] = crLocale;
        }

        var uriBuilder = new UriBuilder(ApiUrls.Browse(true))
        {
            Query = string.Join("&", queryParams.AllKeys.Select(k => $"{k}={HttpUtility.UrlEncode(queryParams[k])}"))
        };

        var request = HttpClientWrapper.CreateRequest(uriBuilder.ToString(), HttpMethod.Get, true, _authService.Token?.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            _logger?.LogError("GetSeasonalSeries (CR) request failed: {Error}", error);
            return new List<SeriesInfo>();
        }

        try
        {
            var result = JsonConvert.DeserializeObject<CrBrowseSeriesBase>(content);
            if (result?.Data == null) return new List<SeriesInfo>();

            return result.Data.Select(item => new SeriesInfo
            {
                Id = item.Id,
                Title = item.Title,
                Description = item.Description,
                Images = ExtractImageUrls(item.Images),
                CoverArtUrl = ExtractBestImage(item.Images, "poster_tall"),
                ThumbnailUrl = ExtractBestImage(item.Images, "poster_wide")
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse GetSeasonalSeries (CR) results");
            return new List<SeriesInfo>();
        }
    }

    /// <summary>
    /// Builds episode data from EpisodeInfo.
    /// Ported from upstream CrEpisode.EpisodeData.
    /// </summary>
    public Task<CrunchyRollEpisodeData> EpisodeDataAsync(EpisodeInfo episode, bool updateHistory = false, CancellationToken cancellationToken = default)
    {
        bool serieshasversions = true;
        var data = new CrunchyRollEpisodeData();

        // Note: updateHistory is not implemented here because HistoryService is not injected.
        // The conversion logic is ported from upstream CrEpisode.EpisodeData.

        var seasonIdentifier = !string.IsNullOrEmpty(episode.Identifier)
            ? (episode.Identifier.Split('|').Length > 1 ? episode.Identifier.Split('|')[1] : $"S{episode.SeasonNumber}")
            : $"S{episode.SeasonNumber}";

        data.Key = $"{seasonIdentifier}E{episode.Episode ?? episode.EpisodeNumber.ToString()}";

        data.EpisodeAndLanguages = new EpisodeAndLanguage();

        var detail = MapToCrEpisodeDetail(episode);

        if (episode.Versions != null && episode.Versions.Count > 0)
        {
            foreach (var version in episode.Versions)
            {
                var lang = Array.Find(Languages.languages, a => a.CrLocale == version.AudioLocale)
                           ?? Languages.DEFAULT_lang;
                data.EpisodeAndLanguages.AddUnique(detail, lang);
            }
        }
        else
        {
            serieshasversions = false;
            var lang = Array.Find(Languages.languages, a => a.CrLocale == episode.AudioLocale)
                       ?? Languages.DEFAULT_lang;
            data.EpisodeAndLanguages.AddUnique(detail, lang);
        }

        if (data.EpisodeAndLanguages.Variants.Count == 0)
            return Task.FromResult(data);

        var baseEp = data.EpisodeAndLanguages.Variants[0].Item;

        // [PT] Same special-detection fix as the season grouping (shared IsSpecialEpisode helper).
        bool isSpecial = IsSpecialEpisode(baseEp.Episode, baseEp.EpisodeNumber);

        string newKey;
        if (isSpecial && !string.IsNullOrEmpty(baseEp.Episode))
        {
            newKey = baseEp.Episode;
        }
        else
        {
            var epPart = (!string.IsNullOrEmpty(baseEp.Episode) && IsRegularEpisodeNumber(baseEp.Episode))
                ? baseEp.Episode
                : (baseEp.EpisodeNumber?.ToString() ?? baseEp.Episode ?? "1");
            newKey = isSpecial
                ? $"SP{epPart} {baseEp.Id}"
                : $"E{epPart}";
        }

        data.Key = newKey;

        var seasonTitle = data.EpisodeAndLanguages.Variants
            .Select(v => v.Item.SeasonTitle)
            .FirstOrDefault(t => !DownloadQueueItemFactory.HasDubSuffix(t))
            ?? DownloadQueueItemFactory.StripDubSuffix(baseEp.SeasonTitle);

        var title = baseEp.Title;
        var seasonNumber = Helpers.ExtractNumberAfterS(baseEp.Identifier) ?? baseEp.SeasonNumber.ToString();

        var languages = data.EpisodeAndLanguages.Variants
            .Select(v => $"{(v.Item.IsPremiumOnly ? "+ " : "")}{v.Lang?.Name ?? "Unknown"}")
            .ToArray();

        _logger?.LogInformation("[{Key}] {SeasonTitle} - Season {SeasonNumber} - {Title} [{Languages}]",
            data.Key, seasonTitle, seasonNumber, title, string.Join(", ", languages));

        if (!serieshasversions)
            _logger?.LogWarning("Couldn\'t find versions on episode, added languages with language array.");

        return Task.FromResult(data);
    }

    /// <summary>
    /// Creates CrunchyEpMeta from CrunchyRollEpisodeData.
    /// Ported from upstream CrEpisode.EpisodeMeta.
    /// </summary>
    public CrunchyEpMeta EpisodeMeta(CrunchyRollEpisodeData episodeP, List<string> dubLang)
    {
        CrunchyEpMeta? retMeta = null;

        var epNum = GetEpisodeLabelFromKey(episodeP.Key);
        var hslang = "none"; // Default, could be fetched from config if needed

        var selectedDubs = dubLang
            .Where(d => episodeP.EpisodeAndLanguages.Variants.Any(v => v.Lang.CrLocale == d))
            .ToList();

        foreach (var v in episodeP.EpisodeAndLanguages.Variants)
        {
            var item = v.Item;
            var lang = v.Lang;

            if (!dubLang.Contains(lang.CrLocale))
                continue;

            item.HideSeasonTitle = true;
            if (string.IsNullOrEmpty(item.SeasonTitle) && !string.IsNullOrEmpty(item.SeriesTitle))
            {
                item.SeasonTitle = item.SeriesTitle;
                item.HideSeasonTitle = false;
                item.HideSeasonNumber = true;
            }

            if (string.IsNullOrEmpty(item.SeasonTitle) && string.IsNullOrEmpty(item.SeriesTitle))
            {
                item.SeasonTitle = "NO_TITLE";
                item.SeriesTitle = "NO_TITLE";
            }

            item.SeqId = epNum;

            if (retMeta == null)
            {
                var seriesTitle = DownloadQueueItemFactory.CanonicalTitle(
                    episodeP.EpisodeAndLanguages.Variants.Select(x => (string?)x.Item.SeriesTitle));

                var seasonTitle = DownloadQueueItemFactory.CanonicalTitle(
                    episodeP.EpisodeAndLanguages.Variants.Select(x => (string?)x.Item.SeasonTitle));

                var (img, imgBig) = DownloadQueueItemFactory.GetThumbSmallBig(item.Images);

                retMeta = DownloadQueueItemFactory.CreateShell(
                    service: StreamingService.Crunchyroll,
                    seriesTitle: seriesTitle,
                    seasonTitle: seasonTitle,
                    episodeNumber: item.Episode,
                    episodeTitle: item.Title,
                    description: item.Description,
                    episodeId: item.Id,
                    seriesId: item.SeriesId,
                    seasonId: item.SeasonId,
                    season: Helpers.ExtractNumberAfterS(item.Identifier) ?? item.SeasonNumber.ToString(),
                    absolutEpisodeNumberE: epNum,
                    image: img,
                    imageBig: imgBig,
                    hslang: hslang,
                    availableSubs: item.SubtitleLocales,
                    selectedDubs: selectedDubs
                );
            }

            var playback = item.Playback;
            if (!string.IsNullOrEmpty(item.StreamsLink))
            {
                playback = item.StreamsLink;
                if (string.IsNullOrEmpty(item.Playback))
                    item.Playback = item.StreamsLink;
            }

            retMeta.Data.Add(DownloadQueueItemFactory.CreateVariant(
                mediaId: item.Id,
                lang: lang,
                playback: playback,
                versions: item.Versions?.Select(v => new EpisodeVersion
                {
                    AudioLocale = v.AudioLocale,
                    Guid = v.Guid,
                    MediaGuid = v.MediaGuid,
                    Original = v.Original,
                    SeasonGuid = v.SeasonGuid
                }).ToList(),
                isSubbed: item.IsSubbed,
                isDubbed: item.IsDubbed
            ));
        }

        return retMeta ?? new CrunchyEpMeta();
    }

    private static CrEpisodeDetail MapToCrEpisodeDetail(EpisodeInfo ep)
    {
        return new CrEpisodeDetail
        {
            Id = ep.Id,
            Guid = ep.Guid,
            Title = ep.Title,
            Description = ep.Description ?? "",
            EpisodeNumber = ep.EpisodeNumber,
            SeasonNumber = ep.SeasonNumber,
            SeriesTitle = ep.SeriesTitle,
            SeasonTitle = ep.SeasonTitle,
            SeasonId = ep.SeasonId,
            SeriesId = ep.SeriesId,
            AudioLocale = ep.AudioLocale,
            IsPremiumOnly = ep.IsPremium,
            IsDubbed = ep.IsDubbed,
            IsSubbed = ep.IsSubbed,
            SubtitleLocales = ep.SubtitleLocales,
            Identifier = ep.Identifier,
            Episode = ep.Episode,
            Playback = ep.Playback,
            StreamsLink = ep.StreamsLink,
            DurationMs = ep.DurationMs,
            Images = ep.RawImages,
            Versions = ep.Versions?.Select(v => new CrEpisodeVersion
            {
                AudioLocale = v.AudioLocale,
                Guid = v.Guid,
                MediaGuid = v.MediaGuid,
                Original = v.Original,
                SeasonGuid = v.SeasonGuid
            }).ToList()
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

// API Response Models
public class CrSearchResult
{
    public int Total { get; set; }
    public List<CrSearchGroup>? Data { get; set; }
}

public class CrSearchGroup
{
    public string? Type { get; set; }
    public int Count { get; set; }
    public List<CrSearchItem>? Items { get; set; }
}

public class CrSearchItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, List<List<object>>>? Images { get; set; }
}

// Browse series models for GetAllSeries/GetSeasonalSeries
public class CrBrowseSeriesBase
{
    public int Total { get; set; }
    public List<CrBrowseSeriesItem>? Data { get; set; }
}

public class CrBrowseSeriesItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, List<List<object>>>? Images { get; set; }
    [JsonProperty("series_metadata")]
    public CrBrowseSeriesMetadata? SeriesMetadata { get; set; }
}

public class CrBrowseSeriesMetadata
{
    [JsonProperty("audio_locales")]
    public List<string>? AudioLocales { get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }
    [JsonProperty("maturity_ratings")]
    public List<string>? MaturityRatings { get; set; }
}

public class CrCmsResponse<T>
{
    public T? Data { get; set; }
}

public class CrCmsListResponse<T>
{
    public List<T>? Data { get; set; }
    public int Total { get; set; }
}

public class CrSeriesDetail
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, List<List<object>>>? Images { get; set; }
}

// CR may send null for value-type fields (dates/numbers) on specials/movies; ignore them so one
// null never throws away the whole response (the empty-calendar bug, 1.0.14).
[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public class CrSeasonDetail
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonProperty("season_number")]
    public int SeasonNumber { get; set; }
    public string? Identifier { get; set; }
}

public class CrEpisodeVersion
{
    [JsonProperty("audio_locale")]
    public string AudioLocale { get; set; } = "";
    public string Guid { get; set; } = "";
    [JsonProperty("media_guid")]
    public string? MediaGuid { get; set; }
    public bool Original { get; set; }
    [JsonProperty("season_guid")]
    public string SeasonGuid { get; set; } = "";
}

[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public class CrEpisodeDetail
{
    public string Id { get; set; } = "";
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    [JsonProperty("episode_number")]
    public int? EpisodeNumber { get; set; }
    [JsonProperty("season_number")]
    public int SeasonNumber { get; set; }
    [JsonProperty("series_title")]
    public string SeriesTitle { get; set; } = "";
    [JsonProperty("season_title")]
    public string? SeasonTitle { get; set; }
    [JsonProperty("season_id")]
    public string? SeasonId { get; set; }
    [JsonProperty("series_id")]
    public string? SeriesId { get; set; }
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly { get; set; }
    [JsonProperty("is_dubbed")]
    public bool IsDubbed { get; set; }
    [JsonProperty("is_subbed")]
    public bool IsSubbed { get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }
    public string? Identifier { get; set; }
    public string? Episode { get; set; }
    public string? Playback { get; set; }
    [JsonProperty("streams_link")]
    public string? StreamsLink { get; set; }
    [JsonProperty("duration_ms")]
    public int DurationMs { get; set; }
    public List<CrEpisodeVersion>? Versions { get; set; }
    public Dictionary<string, List<List<object>>>? Images { get; set; }
    [JsonProperty("hide_season_title")]
    public bool? HideSeasonTitle { get; set; }
    [JsonProperty("hide_season_number")]
    public bool? HideSeasonNumber { get; set; }
    [JsonProperty("seq_id")]
    public string? SeqId { get; set; }
}

// Browse Episode models for GetNewEpisodes
public class CrBrowseEpisodeBase
{
    public int Total { get; set; }
    public List<CrBrowseEpisode>? Data { get; set; }
    public CrBrowseMeta? Meta { get; set; }
}

[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public class CrBrowseEpisode
{
    [JsonProperty("external_id")]
    public string? ExternalId { get; set; }
    [JsonProperty("last_public")]
    public DateTime LastPublic { get; set; }
    public string? Description { get; set; }
    public bool New { get; set; }
    [JsonProperty("linked_resource_key")]
    public string? LinkedResourceKey { get; set; }
    [JsonProperty("slug_title")]
    public string? SlugTitle { get; set; }
    public string? Title { get; set; }
    [JsonProperty("promo_title")]
    public string? PromoTitle { get; set; }
    [JsonProperty("episode_metadata")]
    public CrBrowseEpisodeMetaData EpisodeMetadata { get; set; } = new();
    public string? Id { get; set; }
    public CrBrowseImages? Images { get; set; }
    [JsonProperty("promo_description")]
    public string? PromoDescription { get; set; }
    public string? Slug { get; set; }
    public string? Type { get; set; }
    [JsonProperty("channel_id")]
    public string? ChannelId { get; set; }
    [JsonProperty("streams_link")]
    public string? StreamsLink { get; set; }
}

[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public class CrBrowseEpisodeMetaData
{
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    [JsonProperty("content_descriptors")]
    public List<string>? ContentDescriptors { get; set; }
    [JsonProperty("availability_notes")]
    public string? AvailabilityNotes { get; set; }
    public string? Episode { get; set; }
    [JsonProperty("episode_air_date")]
    public DateTime EpisodeAirDate { get; set; }
    // CR sends episode_number = null for specials/movies/recaps. Ignore the null so the whole
    // browse page doesn't fail to deserialize (was emptying the entire calendar).
    [JsonProperty("episode_number", NullValueHandling = NullValueHandling.Ignore)]
    public int EpisodeCount { get; set; }
    [JsonProperty("duration_ms")]
    public int DurationMs { get; set; }
    [JsonProperty("extended_maturity_rating")]
    public Dictionary<object, object>? ExtendedMaturityRating { get; set; }
    [JsonProperty("is_dubbed")]
    public bool IsDubbed { get; set; }
    [JsonProperty("is_mature")]
    public bool IsMature { get; set; }
    [JsonProperty("is_subbed")]
    public bool IsSubbed { get; set; }
    [JsonProperty("mature_blocked")]
    public bool MatureBlocked { get; set; }
    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly { get; set; }
    [JsonProperty("is_clip")]
    public bool IsClip { get; set; }
    [JsonProperty("maturity_ratings")]
    public List<string>? MaturityRatings { get; set; }
    [JsonProperty("season_number")]
    public double SeasonNumber { get; set; }
    [JsonProperty("season_sequence_number")]
    public double SeasonSequenceNumber { get; set; }
    [JsonProperty("sequence_number")]
    public double SequenceNumber { get; set; }
    [JsonProperty("upload_date")]
    public DateTime UploadDate { get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }
    [JsonProperty("premium_available_date")]
    public DateTime PremiumAvailableDate { get; set; }
    [JsonProperty("availability_ends")]
    public DateTime AvailabilityEnds { get; set; }
    [JsonProperty("availability_starts")]
    public DateTime AvailabilityStarts { get; set; }
    [JsonProperty("free_available_date")]
    public DateTime FreeAvailableDate { get; set; }
    [JsonProperty("identifier")]
    public string? Identifier { get; set; }
    [JsonProperty("season_id")]
    public string? SeasonId { get; set; }
    [JsonProperty("series_id")]
    public string? SeriesId { get; set; }
    [JsonProperty("season_display_number")]
    public string? SeasonDisplayNumber { get; set; }
    [JsonProperty("eligible_region")]
    public string? EligibleRegion { get; set; }
    [JsonProperty("available_date")]
    public DateTime? AvailableDate { get; set; }
    [JsonProperty("premium_date")]
    public DateTime? PremiumDate { get; set; }
    [JsonProperty("available_offline")]
    public bool AvailableOffline { get; set; }
    [JsonProperty("closed_captions_available")]
    public bool ClosedCaptionsAvailable { get; set; }
    [JsonProperty("season_slug_title")]
    public string? SeasonSlugTitle { get; set; }
    [JsonProperty("season_title")]
    public string? SeasonTitle { get; set; }
    [JsonProperty("series_slug_title")]
    public string? SeriesSlugTitle { get; set; }
    [JsonProperty("series_title")]
    public string? SeriesTitle { get; set; }
    [JsonProperty("versions")]
    public List<CrBrowseEpisodeVersion>? Versions { get; set; }
}

public class CrBrowseEpisodeVersion
{
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    public string? Guid { get; set; }
    public bool Original { get; set; }
    public string? Variant { get; set; }
    [JsonProperty("season_guid")]
    public string? SeasonGuid { get; set; }
    [JsonProperty("media_guid")]
    public string? MediaGuid { get; set; }
}

public class CrBrowseImages
{
    public List<List<CrBrowseThumbnail>>? Thumbnail { get; set; }
}

public class CrBrowseThumbnail
{
    public string? Source { get; set; }
}

public class CrBrowseMeta
{
    public int TotalBeforeFilter { get; set; }
    public int TotalAfterFilter { get; set; }
}

