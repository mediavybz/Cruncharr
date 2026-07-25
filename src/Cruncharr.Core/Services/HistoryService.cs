using System.Globalization;
using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IHistoryService
{
    Task AddAsync(DownloadHistory entry);
    Task<List<DownloadHistory>> GetAllAsync(int offset = 0, int limit = 100);
    Task<bool> IsDownloadedAsync(string episodeId, string audioLanguage);
    Task RemoveAsync(string episodeId);
    Task<bool> RemoveSeriesAsync(string seriesId);
    Task<List<DownloadHistory>> GetSeriesHistoryAsync(string seriesId);

    // Rich history methods
    Task<List<HistorySeries>> GetHistorySeriesAsync();
    Task UpdateWithSeasonDataAsync(List<EpisodeInfo> episodes);
    Task UpdateWithEpisodeListAsync(List<EpisodeInfo> episodeList);
    Task UpdateWithEpisodeAsync(List<CrBrowseEpisode> episodes);
    Task UpdateWithMusicEpisodeListAsync(List<MusicVideo> episodeList);
    Task<bool> CrUpdateSeriesAsync(string? seriesId, string? seasonId);
    Task SetAsDownloadedAsync(string? seriesId, string? seasonId, string episodeId, List<string>? downloadedDubs = null, List<string>? downloadedSubs = null);
    Task<HistoryEpisode?> GetHistoryEpisodeAsync(string? seriesId, string? seasonId, string episodeId);
    Task RemoveUnavailableEpisodesAsync();

    // History getters with overrides
    Task<(HistoryEpisode? HistoryEpisode, string DownloadDirPath)> GetHistoryEpisodeWithDownloadDirAsync(string? seriesId, string? seasonId, string episodeId);
    Task<(HistoryEpisode? HistoryEpisode, List<string> DubList, List<string> SubList, string DownloadDirPath, string VideoQuality)> GetHistoryEpisodeWithDubListAndDownloadDirAsync(string? seriesId, string? seasonId, string episodeId);
    Task<List<string>> GetDubListAsync(string? seriesId, string? seasonId);
    Task<(List<string> SubList, string VideoQuality)> GetSubListAsync(string? seriesId, string? seasonId);

    // Sorting
    Task SortItemsAsync();

    // Sonarr integration
    Task<SonarrMatchResult> MatchHistorySeriesWithSonarrAsync(bool updateAll = false);
    Task MatchHistoryEpisodesWithSonarrAsync(string seriesId, bool rematchAll = false);

    // Utilities
    double CalculateSimilarity(string source, string target);

    // Browse/Calendar metadata refresh
    Task RefreshExistingEpisodesFromBrowseAsync(List<CrBrowseEpisode> episodes);

    // Thumbnail
    Task<string?> GetSeriesThumbnailAsync(string seriesId);

    // Settings overrides
    Task SetSeriesSettingsOverrideAsync(string seriesId, string? videoQuality, List<string>? dubLanguages, List<string>? softSubs);
    Task SetSeasonSettingsOverrideAsync(string seasonId, string? videoQuality, List<string>? dubLanguages, List<string>? softSubs);
}

public class HistoryService : IHistoryService, IDisposable
{
    private readonly string _historyPath;
    private readonly string _flatHistoryPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<HistoryService>? _logger;
    private readonly ISonarrService? _sonarrService;
    private readonly ICrunchyrollApiService? _apiService;
    private readonly IMusicService? _musicService;
    private readonly ICrunchyrollAuthService? _authService;
    private readonly CruncharrConfig _config;
    private List<HistorySeries> _historyList = [];
    private volatile bool _loaded = false;

    public HistoryService(string? historyPath = null, ILogger<HistoryService>? logger = null, ISonarrService? sonarrService = null, ICrunchyrollApiService? apiService = null, IMusicService? musicService = null, ICrunchyrollAuthService? authService = null, CruncharrConfig? config = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "cruncharr",
            "history.json"
        );
        _flatHistoryPath = Path.Combine(
            Path.GetDirectoryName(_historyPath)!,
            "history-flat.json"
        );
        _logger = logger;
        _sonarrService = sonarrService;
        _apiService = apiService;
        _musicService = musicService;
        _authService = authService;
        _config = config ?? new CruncharrConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
    }

    public async Task AddAsync(DownloadHistory entry)
    {
        await _lock.WaitAsync();
        try
        {
            var history = await LoadHistoryAsync();
            history.RemoveAll(h => h.EpisodeId == entry.EpisodeId && h.AudioLanguage == entry.AudioLanguage);
            history.Add(entry);
            await SaveHistoryAsync(history);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<DownloadHistory>> GetAllAsync(int offset = 0, int limit = 100)
    {
        await _lock.WaitAsync();
        try
        {
            var history = await LoadHistoryAsync();
            return history
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsDownloadedAsync(string episodeId, string audioLanguage)
    {
        await _lock.WaitAsync();
        try
        {
            var history = await LoadHistoryAsync();
            return history.Any(h => h.EpisodeId == episodeId && h.AudioLanguage == audioLanguage);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string episodeId)
    {
        await _lock.WaitAsync();
        try
        {
            var history = await LoadHistoryAsync();
            history.RemoveAll(h => h.EpisodeId == episodeId);
            await SaveHistoryAsync(history);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<DownloadHistory>> GetSeriesHistoryAsync(string seriesId)
    {
        var history = await GetAllAsync();
        return history.Where(h => h.SeriesId == seriesId).ToList();
    }

    // Remove an entire series from history (rich tree + flat log) so users can drop series they
    // no longer want tracked. Single lock; saves are atomic (temp+rename) inside.
    public async Task<bool> RemoveSeriesAsync(string seriesId)
    {
        if (string.IsNullOrEmpty(seriesId)) return false;
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var removed = _historyList.RemoveAll(s => string.Equals(s.SeriesId, seriesId, StringComparison.Ordinal)) > 0;
            if (removed) await SaveRichHistoryAsync();

            var flat = await LoadHistoryAsync();
            if (flat.RemoveAll(h => h.SeriesId == seriesId) > 0)
            {
                await SaveHistoryAsync(flat);
            }
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Rich history methods
    public async Task<List<HistorySeries>> GetHistorySeriesAsync()
    {
        await EnsureLoadedAsync();
        return await GetHistorySnapshotAsync();
    }

    public async Task UpdateWithSeasonDataAsync(List<EpisodeInfo> episodes)
    {
        if (episodes == null || episodes.Count == 0) return;

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            var firstEpisode = episodes.First();
            var seriesId = firstEpisode.SeriesId;
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);

            if (historySeries != null)
            {
                historySeries.HistorySeriesAddDate ??= DateTime.Now;
                // A series card must never use an episode screenshot. Leave it empty until series
                // metadata supplies poster_tall rather than persisting the episode thumbnail here.
                if (string.IsNullOrEmpty(historySeries.ThumbnailImageUrl) && !string.IsNullOrEmpty(firstEpisode.CoverArtUrl))
                    historySeries.ThumbnailImageUrl = firstEpisode.CoverArtUrl;
                var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == firstEpisode.SeasonId);

                if (historySeason != null)
                {
                    // Update existing season
                    foreach (var episode in episodes)
                    {
                        if (episode.SeasonId != historySeason.SeasonId) continue;

                        var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episode.Id);
                        if (historyEpisode == null)
                        {
                            historySeason.EpisodesList.Add(CreateHistoryEpisode(episode));
                        }
                        else
                        {
                            UpdateHistoryEpisode(historyEpisode, episode);
                        }
                    }

                    historySeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                }
                else
                {
                    // Create new season
                    var newSeason = CreateHistorySeason(episodes, firstEpisode);
                    newSeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                    historySeries.Seasons.Add(newSeason);
                }

                historySeries.UpdateNewEpisodes();
            }
            else if (!string.IsNullOrEmpty(seriesId))
            {
                // Create new series
                historySeries = new HistorySeries
                {
                    SeriesTitle = firstEpisode.SeriesTitle,
                    SeriesId = seriesId,
                    Seasons = [],
                    HistorySeriesAddDate = DateTime.Now,
                    SeriesType = SeriesType.Series,
                    SeriesStreamingService = "Crunchyroll",
                    ThumbnailImageUrl = firstEpisode.CoverArtUrl
                };
                _historyList.Add(historySeries);

                var newSeason = CreateHistorySeason(episodes, firstEpisode);
                newSeason.EpisodesList.Sort(new NumericStringPropertyComparer());
                historySeries.Seasons.Add(newSeason);
                historySeries.UpdateNewEpisodes();
            }

            await SaveRichHistoryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsDownloadedAsync(string? seriesId, string? seasonId, string episodeId, List<string>? downloadedDubs = null, List<string>? downloadedSubs = null)
    {
        // A manual "Mark as Downloaded" supplies no specific dubs/subs: the user is asserting the
        // episode is fully downloaded, so reset the dub/sub tracking to everything available for the
        // episode. Otherwise stale partial tracking would incorrectly re-flag it as partial.
        bool isManualMark = downloadedDubs == null && downloadedSubs == null;
        var normalizedDubs = NormalizeLocales(downloadedDubs);
        var normalizedSubs = NormalizeLocales(downloadedSubs);

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            if (historySeries != null)
            {
                var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
                if (historySeason != null)
                {
                    var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episodeId);
                    if (historyEpisode != null)
                    {
                        historyEpisode.WasDownloaded = true;
                        if (isManualMark)
                        {
                            // Reset tracking to the full available set so the episode is treated as
                            // fully downloaded, not partial.
                            historyEpisode.DownloadedDubLang = new List<string>(historyEpisode.HistoryEpisodeAvailableDubLang);
                            historyEpisode.DownloadedSoftSubs = new List<string>(historyEpisode.HistoryEpisodeAvailableSoftSubs);
                        }
                        else
                        {
                            // Track downloaded dubs/subs for partial download detection
                            if (normalizedDubs.Count > 0)
                            {
                                foreach (var dub in normalizedDubs.Where(d => !historyEpisode.DownloadedDubLang.Contains(d, StringComparer.OrdinalIgnoreCase)))
                                {
                                    historyEpisode.DownloadedDubLang.Add(dub);
                                }
                            }
                            if (normalizedSubs.Count > 0)
                            {
                                foreach (var sub in normalizedSubs.Where(s => !historyEpisode.DownloadedSoftSubs.Contains(s, StringComparer.OrdinalIgnoreCase)))
                                {
                                    historyEpisode.DownloadedSoftSubs.Add(sub);
                                }
                            }
                        }
                        historySeason.UpdateDownloaded();
                        historySeries.UpdateNewEpisodes();
                        await SaveRichHistoryAsync();
                        return;
                    }
                }
            }

            _logger?.LogWarning("Couldn't update download history for episode {EpisodeId}", episodeId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HistoryEpisode?> GetHistoryEpisodeAsync(string? seriesId, string? seasonId, string episodeId)
    {
        await EnsureLoadedAsync();
        var snapshot = await GetHistorySnapshotAsync();
        return snapshot
            .FirstOrDefault(s => s.SeriesId == seriesId)?
            .Seasons.FirstOrDefault(s => s.SeasonId == seasonId)?
            .EpisodesList.Find(e => e.EpisodeId == episodeId);
    }

    public async Task RemoveUnavailableEpisodesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            foreach (var historySeries in _historyList.ToList())
            {
                var seasonsToRemove = new List<HistorySeason>();

                foreach (var season in historySeries.Seasons)
                {
                    var unavailableEpisodes = season.EpisodesList
                        .Where(episode => !episode.IsEpisodeAvailableOnStreamingService)
                        .ToList();

                    foreach (var episode in unavailableEpisodes)
                    {
                        season.EpisodesList.Remove(episode);
                    }

                    if (season.EpisodesList.Count == 0)
                    {
                        seasonsToRemove.Add(season);
                    }
                    else
                    {
                        season.EpisodesList.Sort(new NumericStringPropertyComparer());
                        season.UpdateDownloaded();
                    }
                }

                foreach (var season in seasonsToRemove)
                {
                    historySeries.Seasons.Remove(season);
                }

                if (historySeries.Seasons.Count == 0)
                {
                    _historyList.Remove(historySeries);
                }
                else
                {
                    historySeries.UpdateNewEpisodes();
                }
            }

            await SaveRichHistoryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;

        await _loadSemaphore.WaitAsync();
        try
        {
            if (!_loaded)
            {
                await LoadRichHistoryAsync();
                _loaded = true;
            }
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private async Task<List<HistorySeries>> GetHistorySnapshotAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(
                _historyList,
                HistoryJsonContext.Default.ListHistorySeries);
            return JsonSerializer.Deserialize(
                json,
                HistoryJsonContext.Default.ListHistorySeries) ?? [];
        }
        finally
        {
            _lock.Release();
        }
    }

    private static HistoryEpisode CreateHistoryEpisode(EpisodeInfo episode)
    {
        return new HistoryEpisode
        {
            EpisodeTitle = episode.Title,
            EpisodeDescription = episode.Description,
            EpisodeId = episode.Id,
            Episode = episode.EpisodeNumber.ToString(),
            EpisodeSeasonNum = episode.SeasonNumber.ToString(),
            SpecialEpisode = false,
            IsEpisodeAvailableOnStreamingService = true,
            ThumbnailImageUrl = episode.ThumbnailUrl,
            EpisodeType = EpisodeType.Episode,
            EpisodeSeriesType = SeriesType.Series,
            HistoryEpisodeAvailableDubLang = new List<string> { episode.AudioLocale },
            HistoryEpisodeAvailableSoftSubs = episode.SubtitleLocales ?? new List<string>()
        };
    }

    private static void UpdateHistoryEpisode(HistoryEpisode historyEpisode, EpisodeInfo episode)
    {
        historyEpisode.EpisodeTitle = episode.Title;
        historyEpisode.EpisodeDescription = episode.Description;
        historyEpisode.EpisodeId = episode.Id;
        historyEpisode.Episode = episode.EpisodeNumber.ToString();
        historyEpisode.EpisodeSeasonNum = episode.SeasonNumber.ToString();
        historyEpisode.IsEpisodeAvailableOnStreamingService = true;
        historyEpisode.ThumbnailImageUrl = episode.ThumbnailUrl;
        historyEpisode.EpisodeSeriesType = SeriesType.Series;
        // Update available dub/sub metadata for existing episodes
        if (!historyEpisode.HistoryEpisodeAvailableDubLang.Contains(episode.AudioLocale, StringComparer.OrdinalIgnoreCase))
        {
            historyEpisode.HistoryEpisodeAvailableDubLang.Add(episode.AudioLocale);
        }
        if (episode.SubtitleLocales != null)
        {
            foreach (var sub in episode.SubtitleLocales.Where(sub =>
                         !historyEpisode.HistoryEpisodeAvailableSoftSubs.Contains(sub, StringComparer.OrdinalIgnoreCase)))
            {
                historyEpisode.HistoryEpisodeAvailableSoftSubs.Add(sub);
            }
        }
    }

    private static HistorySeason CreateHistorySeason(List<EpisodeInfo> episodes, EpisodeInfo firstEpisode)
    {
        var season = new HistorySeason
        {
            SeasonTitle = firstEpisode.SeasonTitle,
            SeasonId = firstEpisode.SeasonId,
            SeasonNum = firstEpisode.SeasonNumber.ToString(),
            EpisodesList = [],
            SpecialSeason = false
        };

        foreach (var episode in episodes)
        {
            if (episode.SeasonId != season.SeasonId) continue;
            season.EpisodesList.Add(CreateHistoryEpisode(episode));
        }

        return season;
    }

    // Persistence
    private async Task LoadRichHistoryAsync()
    {
        if (!File.Exists(_historyPath))
        {
            _historyList = [];
            return;
        }

        try
        {
            var content = await DecompressJsonFileAsync(_historyPath);
            if (string.IsNullOrEmpty(content))
            {
                // Fallback to uncompressed
                content = await File.ReadAllTextAsync(_historyPath);
            }
            _historyList = JsonSerializer.Deserialize(content, HistoryJsonContext.Default.ListHistorySeries) ?? [];
            SanitizeHistoryCollections();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load rich history");
            _historyList = [];
        }
    }

    private void SanitizeHistoryCollections()
    {
        foreach (var series in _historyList)
        {
            series.Seasons ??= [];
            series.HistorySeriesDubLangOverride ??= [];
            series.HistorySeriesSoftSubsOverride ??= [];
            foreach (var season in series.Seasons)
            {
                season.EpisodesList ??= [];
                season.HistorySeasonDubLangOverride ??= [];
                season.HistorySeasonSoftSubsOverride ??= [];
                foreach (var episode in season.EpisodesList)
                {
                    episode.DownloadedDubLang ??= [];
                    episode.DownloadedSoftSubs ??= [];
                    episode.HistoryEpisodeAvailableDubLang ??= [];
                    episode.HistoryEpisodeAvailableSoftSubs ??= [];
                }
            }
        }
    }

    private async Task SaveRichHistoryAsync()
    {
        try
        {
            await WriteJsonToFileCompressedAsync(_historyPath, _historyList, keepBackups: 5);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save rich history");
        }
    }

    private async Task<List<DownloadHistory>> LoadHistoryAsync()
    {
        if (!File.Exists(_flatHistoryPath)) return new List<DownloadHistory>();

        try
        {
            var content = await DecompressJsonFileAsync(_flatHistoryPath);
            if (string.IsNullOrEmpty(content))
            {
                // Fallback to uncompressed
                content = await File.ReadAllTextAsync(_flatHistoryPath);
            }
            var flatHistory = JsonSerializer.Deserialize(content, HistoryJsonContext.Default.ListDownloadHistory) ?? new List<DownloadHistory>();
            return flatHistory;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load flat history");
            return new List<DownloadHistory>();
        }
    }

    private async Task SaveHistoryAsync(List<DownloadHistory> flatHistory)
    {
        try
        {
            await WriteJsonToFileCompressedAsync(_flatHistoryPath, flatHistory, keepBackups: 5);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save flat history");
        }
    }

    // Ported from upstream CfgManager.WriteJsonToFileCompressed
    private static async Task WriteJsonToFileCompressedAsync(string pathToFile, object obj, int keepBackups = 5)
    {
        string? directoryPath = Path.GetDirectoryName(pathToFile);
        if (string.IsNullOrEmpty(directoryPath))
            directoryPath = Environment.CurrentDirectory;

        Directory.CreateDirectory(directoryPath);
        string tmp = pathToFile + ".tmp";

        try
        {
            // Write compressed JSON to temp file
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: false))
            using (var sw = new StreamWriter(gzip))
            {
                var content = JsonSerializer.Serialize(obj, obj.GetType(), HistoryJsonContext.Default);
                await sw.WriteAsync(content);
            }

            if (File.Exists(pathToFile))
            {
                string backupPath = GetDailyBackupPath(pathToFile, DateTime.Today);
                File.Replace(tmp, pathToFile, backupPath, ignoreMetadataErrors: true);
                PruneBackups(pathToFile, keepBackups);
            }
            else
            {
                File.Move(tmp, pathToFile, overwrite: true);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // ignored
            }
            throw;
        }
    }

    // Ported from upstream CfgManager.DecompressJsonFile
    private static async Task<string?> DecompressJsonFileAsync(string pathToFile)
    {
        try
        {
            using var fs = new FileStream(pathToFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Check if file is gzip compressed by reading magic bytes
            var magic = new byte[2];
            await fs.ReadAsync(magic, 0, 2);
            fs.Position = 0;

            bool isGzip = magic[0] == 0x1f && magic[1] == 0x8b;

            if (isGzip)
            {
                using var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                using var sr = new StreamReader(gzip);
                return await sr.ReadToEndAsync();
            }
            else
            {
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync();
            }
        }
        catch
        {
            return null;
        }
    }

    private static string GetDailyBackupPath(string pathToFile, DateTime date)
    {
        string dir = Path.GetDirectoryName(pathToFile)!;
        string name = Path.GetFileName(pathToFile);
        string backupName = $".{name}.{date:yyyy-MM-dd}.bak";
        return Path.Combine(dir, backupName);
    }

    private static void PruneBackups(string pathToFile, int keep)
    {
        try
        {
            string dir = Path.GetDirectoryName(pathToFile)!;
            string name = Path.GetFileName(pathToFile);
            string glob = $".{name}.*.bak";
            var rx = new System.Text.RegularExpressions.Regex(@"^\." + System.Text.RegularExpressions.Regex.Escape(name) + @"\.(\d{4}-\d{2}-\d{2})\.bak$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            var datedBackups = new List<(string Path, DateTime Date)>();
            foreach (var path in Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly))
            {
                string file = Path.GetFileName(path);
                var match = rx.Match(file);
                if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    datedBackups.Add((path, date));
                }
            }

            // Sort by date descending and delete oldest
            foreach (var backup in datedBackups.OrderByDescending(b => b.Date).Skip(keep))
            {
                try
                {
                    File.Delete(backup.Path);
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    // Sonarr integration methods
    public async Task<SonarrMatchResult> MatchHistorySeriesWithSonarrAsync(bool updateAll = false)
    {
        if (_config.Sonarr is not { Enabled: true } || _sonarrService == null)
        {
            return new SonarrMatchResult(0, 0, 0);
        }

        await EnsureLoadedAsync();

        // Fetch Sonarr data OUTSIDE the lock
        List<SonarrSeries> sonarrSeries;
        try
        {
            sonarrSeries = await _sonarrService.GetSeriesAsync(_config.Sonarr);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch Sonarr series data");
            return new SonarrMatchResult(0, 0, 0);
        }

        var sonarrSeriesById = updateAll
            ? sonarrSeries.ToDictionary(series => series.Id.ToString())
            : [];

        var historyTotal = 0;
        var matched = 0;
        await _lock.WaitAsync();
        try
        {
            foreach (var historySeries in _historyList)
            {
                if (historySeries.SeriesType == SeriesType.Artist) continue;
                historyTotal++;

                if (string.IsNullOrEmpty(historySeries.SonarrSeriesId))
                {
                    var matchedSeries = FindClosestMatch(historySeries.SeriesTitle ?? string.Empty, sonarrSeries);
                    if (matchedSeries != null)
                    {
                        historySeries.SonarrSeriesId = matchedSeries.Id.ToString();
                        historySeries.SonarrTvDbId = matchedSeries.TvdbId.ToString();
                        historySeries.SonarrSlugTitle = matchedSeries.TitleSlug;
                    }
                }
                else if (updateAll)
                {
                    if (sonarrSeriesById.TryGetValue(historySeries.SonarrSeriesId, out var matchedSeries))
                    {
                        historySeries.SonarrSeriesId = matchedSeries.Id.ToString();
                        historySeries.SonarrTvDbId = matchedSeries.TvdbId.ToString();
                        historySeries.SonarrSlugTitle = matchedSeries.TitleSlug;
                    }
                    else
                    {
                        _logger?.LogWarning("Unable to find sonarr series for {SeriesTitle}", historySeries.SeriesTitle);
                    }
                }

                if (!string.IsNullOrEmpty(historySeries.SonarrSeriesId)) matched++;
            }

            await SaveRichHistoryAsync();
        }
        finally
        {
            _lock.Release();
        }

        return new SonarrMatchResult(historyTotal, matched, sonarrSeries.Count);
    }

    public async Task MatchHistoryEpisodesWithSonarrAsync(string seriesId, bool rematchAll = false)
    {
        if (_config.Sonarr is not { Enabled: true } || _sonarrService == null)
        {
            return;
        }

        await EnsureLoadedAsync();

        // Find series and parse Sonarr ID under lock
        int sonarrSeriesId;
        await _lock.WaitAsync();
        try
        {
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            if (historySeries == null) return;

            if (!int.TryParse(historySeries.SonarrSeriesId, out sonarrSeriesId))
            {
                return;
            }
        }
        finally
        {
            _lock.Release();
        }

        // Fetch Sonarr episodes OUTSIDE the lock
        List<SonarrEpisode> episodes;
        try
        {
            episodes = await _sonarrService.GetEpisodesAsync(sonarrSeriesId, _config.Sonarr);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch Sonarr episodes for series {SeriesId}", seriesId);
            return;
        }

        var nextAirDate = GetNextAirDate(episodes);
        var episodesById = episodes.ToDictionary(episode => episode.Id);

        await _lock.WaitAsync();
        try
        {
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            if (historySeries == null) return;

            historySeries.SonarrNextAirDate = nextAirDate;

            var allHistoryEpisodes = historySeries.Seasons
                .SelectMany(historySeriesSeason => historySeriesSeason.EpisodesList)
                .ToList();

            var usedSonarrEpisodeIds = new HashSet<int>();
            var episodesToMatch = new List<HistoryEpisode>();

            if (!rematchAll)
            {
                foreach (var historyEpisode in allHistoryEpisodes)
                {
                    if (int.TryParse(historyEpisode.SonarrEpisodeId, out var sonarrEpisodeId) &&
                        episodesById.TryGetValue(sonarrEpisodeId, out var sonarrEpisode) &&
                        usedSonarrEpisodeIds.Add(sonarrEpisode.Id))
                    {
                        historyEpisode.AssignSonarrEpisodeData(sonarrEpisode);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(historyEpisode.SonarrEpisodeId))
                    {
                        historyEpisode.ClearSonarrEpisodeData();
                    }

                    episodesToMatch.Add(historyEpisode);
                }
            }
            else
            {
                foreach (var historyEpisode in allHistoryEpisodes)
                {
                    historyEpisode.ClearSonarrEpisodeData();
                    episodesToMatch.Add(historyEpisode);
                }
            }

            var titleAvailableEpisodes = episodes
                .Where(episode => !usedSonarrEpisodeIds.Contains(episode.Id))
                .ToList();

            var titleCandidates = episodesToMatch
                .Select(historyEpisode =>
                {
                    var availableEpisodes = IsSpecialHistoryEpisode(historySeries, historyEpisode)
                        ? titleAvailableEpisodes.Where(episode => episode.SeasonNumber == 0).ToList()
                        : titleAvailableEpisodes;
                    var match = FindClosestMatchEpisodeWithScore(
                        availableEpisodes,
                        historyEpisode.EpisodeTitle ?? string.Empty);
                    return new
                    {
                        HistoryEpisode = historyEpisode,
                        match.Episode,
                        match.Score
                    };
                })
                .Where(candidate => candidate.Episode != null)
                .OrderByDescending(candidate => candidate.Score)
                .ToList();

            var failedEpisodes = new List<HistoryEpisode>();
            var matchedHistoryEpisodes = new HashSet<HistoryEpisode>();

            foreach (var candidate in titleCandidates)
            {
                if (TryAssignSonarrEpisode(candidate.HistoryEpisode, candidate.Episode, usedSonarrEpisodeIds))
                {
                    matchedHistoryEpisodes.Add(candidate.HistoryEpisode);
                }
            }

            failedEpisodes.AddRange(episodesToMatch.Where(historyEpisode => !matchedHistoryEpisodes.Contains(historyEpisode)));

            // Try matching by episode/season number
            foreach (var historyEpisode in failedEpisodes.ToList())
            {
                if (IsSpecialHistoryEpisode(historySeries, historyEpisode))
                {
                    continue;
                }

                var episode = episodes.FirstOrDefault(ele =>
                {
                    if (usedSonarrEpisodeIds.Contains(ele.Id))
                    {
                        return false;
                    }

                    var episodeNumberStr = ele.EpisodeNumber.ToString();
                    var seasonNumberStr = ele.SeasonNumber.ToString();

                    return episodeNumberStr == historyEpisode.Episode && seasonNumberStr == historyEpisode.EpisodeSeasonNum;
                });

                if (TryAssignSonarrEpisode(historyEpisode, episode, usedSonarrEpisodeIds))
                {
                    failedEpisodes.Remove(historyEpisode);
                }
            }

            // Try matching by description similarity
            foreach (var historyEpisode in failedEpisodes.ToList())
            {
                var episode = episodes.FirstOrDefault(ele =>
                    !usedSonarrEpisodeIds.Contains(ele.Id) &&
                    (!IsSpecialHistoryEpisode(historySeries, historyEpisode) || ele.SeasonNumber == 0) &&
                    !string.IsNullOrEmpty(historyEpisode.EpisodeDescription) &&
                    !string.IsNullOrEmpty(ele.Overview) &&
                    StringSimilarity.CalculateCosineSimilarity(ele.Overview, historyEpisode.EpisodeDescription) > 0.8);

                if (TryAssignSonarrEpisode(historyEpisode, episode, usedSonarrEpisodeIds))
                {
                    failedEpisodes.Remove(historyEpisode);
                }
            }

            // Crunchyroll commonly exposes OVAs in a provider-local season (for example "Specials"
            // as S4E1-E4), while TVDB/Sonarr stores them in season 0 with other entries such as a
            // movie before them. Titles can also be translated differently. Align the remaining
            // special group to an ordered subset of Sonarr season 0 using title/overview evidence.
            foreach (var matchedSpecial in MatchSpecialEpisodesByOrder(
                         historySeries,
                         failedEpisodes,
                         episodes,
                         usedSonarrEpisodeIds))
            {
                failedEpisodes.Remove(matchedSpecial);
            }

            // Try matching by absolute episode number
            foreach (var historyEpisode in failedEpisodes.ToList())
            {
                var episode = episodes.FirstOrDefault(ele =>
                    !usedSonarrEpisodeIds.Contains(ele.Id) &&
                    !IsSpecialHistoryEpisode(historySeries, historyEpisode) &&
                    ele.AbsoluteEpisodeNumber.ToString() == historyEpisode.Episode);

                if (TryAssignSonarrEpisode(historyEpisode, episode, usedSonarrEpisodeIds))
                {
                    failedEpisodes.Remove(historyEpisode);
                }
            }

            foreach (var historyEpisode in failedEpisodes)
            {
                historyEpisode.ClearSonarrEpisodeData();
            }

            await SaveRichHistoryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SonarrSeries? FindClosestMatch(string title, List<SonarrSeries> sonarrSeries)
    {
        if (string.IsNullOrEmpty(title) || sonarrSeries.Count == 0)
        {
            return null;
        }

        SonarrSeries? closestMatch = null;
        double highestSimilarity = 0.0;
        var lockObject = new object();

        var needle = title.ToLower();

        Parallel.ForEach(sonarrSeries, series =>
        {
            // Score against the primary title AND any alternate titles (anime in Sonarr
            // often carry romaji/native/english variants there; CR uses a different one).
            double best = 0.0;
            if (series.Title != null)
            {
                best = StringSimilarity.CalculateSimilarity(series.Title.ToLower(), needle);
            }
            if (series.AlternateTitles != null)
            {
                foreach (var alt in series.AlternateTitles)
                {
                    if (string.IsNullOrEmpty(alt.Title)) continue;
                    var sim = StringSimilarity.CalculateSimilarity(alt.Title.ToLower(), needle);
                    if (sim > best) best = sim;
                }
            }

            if (best > 0.0)
            {
                lock (lockObject)
                {
                    if (best > highestSimilarity)
                    {
                        highestSimilarity = best;
                        closestMatch = series;
                    }
                }
            }
        });

        return highestSimilarity < 0.8 ? null : closestMatch;
    }

    private static (SonarrEpisode? Episode, double Score) FindClosestMatchEpisodeWithScore(List<SonarrEpisode> episodeList, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || episodeList.Count == 0)
        {
            return (null, 0.0);
        }

        SonarrEpisode? closestMatch = null;
        double highestSimilarity = 0.0;
        foreach (var episode in episodeList)
        {
            if (!string.IsNullOrWhiteSpace(episode.Title))
            {
                double similarity = StringSimilarity.CalculateSimilarity(episode.Title, title);
                if (similarity <= highestSimilarity) continue;

                highestSimilarity = similarity;
                closestMatch = episode;
            }
        }

        return highestSimilarity < 0.8 ? (null, highestSimilarity) : (closestMatch, highestSimilarity);
    }

    private List<HistoryEpisode> MatchSpecialEpisodesByOrder(
        HistorySeries historySeries,
        List<HistoryEpisode> failedEpisodes,
        List<SonarrEpisode> sonarrEpisodes,
        HashSet<int> usedSonarrEpisodeIds)
    {
        var failed = failedEpisodes.ToHashSet();
        var matched = new List<HistoryEpisode>();

        foreach (var historySeason in historySeries.Seasons.Where(IsSpecialHistorySeason))
        {
            var sourceEpisodes = historySeason.EpisodesList
                .Where(failed.Contains)
                .OrderBy(historyEpisode => ParseEpisodeOrdinal(historyEpisode.Episode))
                .ToList();
            if (sourceEpisodes.Count == 0) continue;

            var targetEpisodes = sonarrEpisodes
                .Where(sonarrEpisode =>
                    sonarrEpisode.SeasonNumber == 0 &&
                    !usedSonarrEpisodeIds.Contains(sonarrEpisode.Id))
                .OrderBy(sonarrEpisode => sonarrEpisode.EpisodeNumber)
                .ToList();
            if (targetEpisodes.Count < sourceEpisodes.Count) continue;

            var alignment = FindBestOrderedSpecialAlignment(sourceEpisodes, targetEpisodes);
            if (alignment.Count != sourceEpisodes.Count) continue;

            var scores = alignment
                .Select(pair => CalculateEpisodeEvidenceScore(pair.HistoryEpisode, pair.SonarrEpisode))
                .ToList();
            var hasEnoughEvidence = sourceEpisodes.Count == 1
                ? scores[0] >= 0.8
                : scores.Count(score => score >= 0.45) >= 2 &&
                  scores.Average() >= 0.25;
            if (!hasEnoughEvidence)
            {
                continue;
            }

            foreach (var pair in alignment)
            {
                if (!TryAssignSonarrEpisode(pair.HistoryEpisode, pair.SonarrEpisode, usedSonarrEpisodeIds))
                {
                    continue;
                }

                matched.Add(pair.HistoryEpisode);
                _logger?.LogInformation(
                    "Ordered special match: CR S{CrS}E{CrE} \"{CrTitle}\" -> Sonarr S00E{SnE} \"{SnTitle}\"",
                    pair.HistoryEpisode.EpisodeSeasonNum,
                    pair.HistoryEpisode.Episode,
                    pair.HistoryEpisode.EpisodeTitle,
                    pair.SonarrEpisode.EpisodeNumber,
                    pair.SonarrEpisode.Title);
            }
        }

        return matched;
    }

    private static List<(HistoryEpisode HistoryEpisode, SonarrEpisode SonarrEpisode)>
        FindBestOrderedSpecialAlignment(
            IReadOnlyList<HistoryEpisode> sourceEpisodes,
            IReadOnlyList<SonarrEpisode> targetEpisodes)
    {
        var sourceCount = sourceEpisodes.Count;
        var targetCount = targetEpisodes.Count;
        if (sourceCount == 0 || targetCount < sourceCount) return [];

        const double impossible = -1_000_000;
        var scores = new double[sourceCount, targetCount];
        for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                scores[sourceIndex, targetIndex] = CalculateEpisodeEvidenceScore(
                    sourceEpisodes[sourceIndex],
                    targetEpisodes[targetIndex]);
            }
        }

        var best = new double[sourceCount + 1, targetCount + 1];
        var take = new bool[sourceCount + 1, targetCount + 1];
        for (var sourceIndex = 1; sourceIndex <= sourceCount; sourceIndex++)
        {
            best[sourceIndex, 0] = impossible;
        }

        for (var sourceIndex = 1; sourceIndex <= sourceCount; sourceIndex++)
        {
            for (var targetIndex = 1; targetIndex <= targetCount; targetIndex++)
            {
                var skipTarget = best[sourceIndex, targetIndex - 1];
                var match = best[sourceIndex - 1, targetIndex - 1] +
                            scores[sourceIndex - 1, targetIndex - 1];
                if (match > skipTarget)
                {
                    best[sourceIndex, targetIndex] = match;
                    take[sourceIndex, targetIndex] = true;
                }
                else
                {
                    best[sourceIndex, targetIndex] = skipTarget;
                }
            }
        }

        var alignment = new List<(HistoryEpisode HistoryEpisode, SonarrEpisode SonarrEpisode)>();
        var sourceCursor = sourceCount;
        var targetCursor = targetCount;
        while (sourceCursor > 0 && targetCursor > 0)
        {
            if (!take[sourceCursor, targetCursor])
            {
                targetCursor--;
                continue;
            }

            alignment.Add((
                sourceEpisodes[sourceCursor - 1],
                targetEpisodes[targetCursor - 1]));
            sourceCursor--;
            targetCursor--;
        }

        alignment.Reverse();
        return alignment;
    }

    private static double CalculateEpisodeEvidenceScore(
        HistoryEpisode historyEpisode,
        SonarrEpisode sonarrEpisode)
    {
        var titleSimilarity =
            string.IsNullOrWhiteSpace(historyEpisode.EpisodeTitle) ||
            string.IsNullOrWhiteSpace(sonarrEpisode.Title)
                ? 0.0
                : StringSimilarity.CalculateSimilarity(
                    historyEpisode.EpisodeTitle.ToLowerInvariant(),
                    sonarrEpisode.Title.ToLowerInvariant());
        var titleTokenSimilarity = CalculateNormalizedTokenCosine(
            historyEpisode.EpisodeTitle,
            sonarrEpisode.Title);
        var overviewSimilarity = CalculateNormalizedTokenCosine(
            historyEpisode.EpisodeDescription,
            sonarrEpisode.Overview);

        return Math.Max(titleSimilarity, Math.Max(titleTokenSimilarity, overviewSimilarity));
    }

    private static double CalculateNormalizedTokenCosine(string? source, string? target)
    {
        static Dictionary<string, int> TokenCounts(string? value)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) return counts;
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the", "and", "or", "to", "of", "in", "on", "for", "with",
                "from", "is", "are", "was", "were", "be", "by", "as", "at", "it", "this",
                "that", "they", "them", "his", "her", "he", "she", "him"
            };

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(value, @"[\p{L}\p{N}]+"))
            {
                var token = match.Value.ToLowerInvariant();
                if (stopWords.Contains(token)) continue;
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }

            return counts;
        }

        var sourceTokens = TokenCounts(source);
        var targetTokens = TokenCounts(target);
        if (sourceTokens.Count == 0 || targetTokens.Count == 0) return 0.0;

        var dotProduct = sourceTokens.Sum(pair =>
            pair.Value * targetTokens.GetValueOrDefault(pair.Key));
        var sourceNorm = Math.Sqrt(sourceTokens.Values.Sum(value => value * value));
        var targetNorm = Math.Sqrt(targetTokens.Values.Sum(value => value * value));
        return sourceNorm == 0 || targetNorm == 0
            ? 0.0
            : dotProduct / (sourceNorm * targetNorm);
    }

    private static bool IsSpecialHistorySeason(HistorySeason historySeason) =>
        historySeason.SpecialSeason ||
        historySeason.SeasonTitle?.Contains("special", StringComparison.OrdinalIgnoreCase) == true ||
        historySeason.SeasonNum == "0";

    private static bool IsSpecialHistoryEpisode(
        HistorySeries historySeries,
        HistoryEpisode historyEpisode) =>
        historyEpisode.SpecialEpisode ||
        historySeries.Seasons.Any(historySeason =>
            IsSpecialHistorySeason(historySeason) &&
            historySeason.EpisodesList.Contains(historyEpisode));

    private static double ParseEpisodeOrdinal(string? episode) =>
        double.TryParse(episode, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.MaxValue;

    private static bool TryAssignSonarrEpisode(HistoryEpisode historyEpisode, SonarrEpisode? episode, HashSet<int> usedSonarrEpisodeIds)
    {
        if (episode == null || !usedSonarrEpisodeIds.Add(episode.Id))
        {
            return false;
        }

        historyEpisode.AssignSonarrEpisodeData(episode);
        return true;
    }

    private static string GetNextAirDate(List<SonarrEpisode> episodes)
    {
        DateTime today = DateTime.UtcNow.Date;

        var todayEpisode = episodes.FirstOrDefault(e => e.AirDateUtc.Date == today);
        if (todayEpisode != null)
        {
            return "Today";
        }

        var nextEpisode = episodes
            .Where(e => e.AirDateUtc.Date > today)
            .OrderBy(e => e.AirDateUtc.Date)
            .FirstOrDefault();

        if (nextEpisode != null)
        {
            return nextEpisode.AirDateUtc.ToString("dd.MM.yyyy");
        }

        return string.Empty;
    }

    // Methods ported from upstream History.cs

    public async Task<bool> CrUpdateSeriesAsync(string? seriesId, string? seasonId)
    {
        if (string.IsNullOrEmpty(seriesId) || _apiService == null)
        {
            return false;
        }

        if (_authService != null)
        {
            await _authService.AuthenticateAsync(true);
        }

        await EnsureLoadedAsync();

        // Mark episodes unavailable under lock
        await _lock.WaitAsync();
        try
        {
            var historySeries = _historyList.FirstOrDefault(series => series.SeriesId == seriesId);

            if (historySeries != null)
            {
                if (string.IsNullOrEmpty(seasonId))
                {
                    foreach (var historySeriesSeason in historySeries.Seasons)
                    {
                        foreach (var historyEpisode in historySeriesSeason.EpisodesList)
                        {
                            historyEpisode.IsEpisodeAvailableOnStreamingService = false;
                        }
                    }
                }
                else
                {
                    var matchingSeason = historySeries.Seasons.FirstOrDefault(historySeason => historySeason.SeasonId == seasonId);

                    if (matchingSeason != null)
                    {
                        foreach (var historyEpisode in matchingSeason.EpisodesList)
                        {
                            historyEpisode.IsEpisodeAvailableOnStreamingService = false;
                        }
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        // Perform API calls OUTSIDE the lock
        List<SeasonInfo>? seasons = null;
        try
        {
            seasons = await _apiService.ParseSeriesByIdAsync(seriesId, "ja-JP", true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh series data for {SeriesId}", seriesId);
        }

        // Resilience: a download that couldn't capture Crunchyroll's series_id stored the series
        // TITLE as the id, so the full-season fetch above fails. Recover the real CR series id from a
        // known episode of this series, re-key the history entry, and retry - so the History page can
        // show ALL episodes (downloaded + missing) for it.
        if (seasons == null || seasons.Count == 0)
        {
            var realSeriesId = await DeriveCrSeriesIdAsync(seriesId);
            if (!string.IsNullOrEmpty(realSeriesId) && !string.Equals(realSeriesId, seriesId, StringComparison.Ordinal))
            {
                _logger?.LogInformation("Recovered CR series id {Real} for history series {Old}", realSeriesId, seriesId);
                try { seasons = await _apiService.ParseSeriesByIdAsync(realSeriesId, "ja-JP", true); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Retry with derived series id {Id} failed", realSeriesId); }
                if (seasons is { Count: > 0 })
                {
                    await RekeyHistorySeriesAsync(seriesId, realSeriesId);
                    seriesId = realSeriesId;
                }
            }
        }

        if (seasons == null || seasons.Count == 0)
        {
            _logger?.LogError("Parse Data Invalid - series is maybe only available with VPN or got deleted");
            return false;
        }

        var lang = string.IsNullOrEmpty(_config.History.Lang)
            ? "en-US"
            : _config.History.Lang;

        // [PT] Desktop History.RefreshSeriesData calls SeriesById and always reapplies the
        // series-level title, description, and poster. Season episode data only carries episode
        // imagery, so it cannot correct a screenshot that was stored when the history row was
        // first created.
        SeriesInfo? seriesData = null;
        try
        {
            seriesData = await _apiService.SeriesByIdAsync(seriesId, lang, true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh series metadata for {SeriesId}", seriesId);
        }

        foreach (var season in seasons)
        {
            var candidateIds = new List<string>();
            candidateIds.Add(season.Id);

            if (!string.IsNullOrEmpty(seasonId) &&
                !candidateIds.Contains(seasonId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidateId in candidateIds)
            {
                try
                {
                    var seasonEpisodes = await _apiService.GetSeasonDataByIdAsync(candidateId, lang, true);

                    if (seasonEpisodes != null && seasonEpisodes.Count > 0)
                    {
                        await UpdateWithSeasonDataAsync(seasonEpisodes);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to refresh series data for candidate {CandidateId}", candidateId);
                }
            }
        }

        // Final update under lock
        await _lock.WaitAsync();
        try
        {
            var historySeries = _historyList.FirstOrDefault(series => series.SeriesId == seriesId);

            if (historySeries != null)
            {
                if (seriesData != null)
                {
                    historySeries.SeriesDescription = seriesData.Description;
                    historySeries.ThumbnailImageUrl = seriesData.CoverArtUrl ?? "";
                    historySeries.SeriesTitle = seriesData.Title;
                }

                // Merge any episode that ended up in more than one season (a legacy fallback-keyed
                // season + the real populated season) so a downloaded episode shows under its real
                // season with its download state intact, not duplicated.
                DeduplicateEpisodesAcrossSeasons(historySeries);
                RemoveUnavailableEpisodesFromSeries(historySeries);
                if (historySeries.Seasons.Count == 0)
                {
                    _historyList.Remove(historySeries);
                    await SaveRichHistoryAsync();
                }
                else
                {
                    await SaveRichHistoryAsync();
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        // Sonarr matching outside lock
        await MatchHistorySeriesWithSonarrAsync(false);
        await MatchHistoryEpisodesWithSonarrAsync(seriesId, false);

        return true;
    }

    // Recover the real Crunchyroll series id from a downloaded episode of a history series whose id
    // is a title fallback (a download that didn't capture series_id). The CMS episode endpoint
    // carries series_id, so any known episode of the series yields it.
    private async Task<string?> DeriveCrSeriesIdAsync(string? historySeriesId)
    {
        if (string.IsNullOrEmpty(historySeriesId) || _apiService == null) return null;
        await EnsureLoadedAsync();
        string? episodeId = null;
        await _lock.WaitAsync();
        try
        {
            var hs = _historyList.FirstOrDefault(s => s.SeriesId == historySeriesId);
            episodeId = hs?.Seasons
                .SelectMany(se => se.EpisodesList)
                .FirstOrDefault(e => !string.IsNullOrEmpty(e.EpisodeId))?.EpisodeId;
        }
        finally { _lock.Release(); }

        if (string.IsNullOrEmpty(episodeId)) return null;
        try
        {
            var ep = await _apiService.ParseEpisodeByIdAsync(episodeId, null, true, default);
            return string.IsNullOrEmpty(ep?.SeriesId) ? null : ep!.SeriesId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to derive CR series id from episode {EpisodeId}", episodeId);
            return null;
        }
    }

    // Adopt the real CR series id on a history series that was keyed by title (no duplicate exists).
    private async Task RekeyHistorySeriesAsync(string oldId, string newId)
    {
        await _lock.WaitAsync();
        try
        {
            var hs = _historyList.FirstOrDefault(s => s.SeriesId == oldId);
            if (hs != null && _historyList.All(s => s.SeriesId != newId))
            {
                hs.SeriesId = newId;
                await SaveRichHistoryAsync();
            }
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateWithEpisodeListAsync(List<EpisodeInfo> episodeList)
    {
        if (episodeList is { Count: > 0 })
        {
            var episodeVersions = episodeList.First().Versions;
            if (episodeVersions != null)
            {
                var version = episodeVersions.Find(a => a.Original);
                if (!string.Equals(version?.AudioLocale, episodeList.First().AudioLocale, StringComparison.OrdinalIgnoreCase))
                {
                    await CrUpdateSeriesAsync(episodeList.First().SeriesId, version?.SeasonGuid);
                    return;
                }
            }
            else
            {
                await CrUpdateSeriesAsync(episodeList.First().SeriesId, "");
                return;
            }

            await UpdateWithSeasonDataAsync(episodeList);
        }
    }

    // Ported from upstream History.UpdateWithEpisode - updates history from calendar/browse data
    public async Task UpdateWithEpisodeAsync(List<CrBrowseEpisode> episodes)
    {
        if (episodes == null || episodes.Count == 0) return;

        await EnsureLoadedAsync();

        // Build history index for quick lookup
        var historyIndex = _historyList
            .Where(h => !string.IsNullOrWhiteSpace(h.SeriesId))
            .ToDictionary(
                h => h.SeriesId!,
                h => h.Seasons
                    .Where(s => !string.IsNullOrWhiteSpace(s.SeasonId))
                    .ToDictionary(
                        s => s.SeasonId ?? "UNKNOWN",
                        s => s.EpisodesList
                            .Select(ep => ep.EpisodeId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToHashSet(StringComparer.Ordinal),
                        StringComparer.Ordinal
                    ),
                StringComparer.Ordinal
            );

        // Filter to episodes whose series is in history
        var relevantEpisodes = episodes
            .Where(e => !string.IsNullOrWhiteSpace(e.EpisodeMetadata?.SeriesId)
                        && historyIndex.ContainsKey(e.EpisodeMetadata!.SeriesId!))
            .ToList();

        foreach (var seriesGroup in relevantEpisodes.GroupBy(e => e.EpisodeMetadata?.SeriesId ?? "UNKNOWN_SERIES"))
        {
            var seriesId = seriesGroup.Key;
            var convertedEpisodes = seriesGroup.Select(ConvertBrowseEpisodeToEpisodeInfo).ToList();

            if (convertedEpisodes.Count > 0)
            {
                _logger?.LogInformation("Updating history from browse data for series {SeriesId} with {Count} episodes", seriesId, convertedEpisodes.Count);
                await UpdateWithSeasonDataAsync(convertedEpisodes);
            }
        }

        await SaveRichHistoryAsync();
    }

    private static EpisodeInfo ConvertBrowseEpisodeToEpisodeInfo(CrBrowseEpisode browseEpisode)
    {
        var metadata = browseEpisode.EpisodeMetadata;
        var originalVersion = metadata?.Versions?.FirstOrDefault(v => v.Original);

        return new EpisodeInfo
        {
            Id = browseEpisode.Id ?? originalVersion?.Guid ?? "",
            Guid = originalVersion?.Guid ?? browseEpisode.Id ?? "",
            Title = browseEpisode.Title ?? "",
            SeriesTitle = "", // Not available in browse data
            SeasonTitle = "",
            Description = browseEpisode.Description ?? "",
            EpisodeNumber = metadata?.EpisodeCount ?? 0,
            SeasonNumber = (int)(metadata?.SeasonNumber ?? 0),
            SeasonId = originalVersion?.SeasonGuid ?? "",
            SeriesId = metadata?.SeriesId ?? "", // Use SeriesId from metadata if available
            AudioLocale = metadata?.AudioLocale ?? "ja-JP",
            Locale = metadata?.AudioLocale ?? "ja-JP",
            IsPremium = metadata?.IsPremiumOnly ?? false,
            Versions = metadata?.Versions?.Select(v => new EpisodeVersion
            {
                AudioLocale = v.AudioLocale ?? metadata?.AudioLocale ?? "ja-JP",
                Guid = v.Guid ?? "",
                MediaGuid = v.MediaGuid,
                Original = v.Original,
                SeasonGuid = v.SeasonGuid ?? ""
            }).ToList(),
            SubtitleLocales = metadata?.SubtitleLocales ?? new List<string>(),
            Images = new List<string>(),
            ThumbnailUrl = null
        };
    }

    public async Task UpdateWithMusicEpisodeListAsync(List<MusicVideo> episodeList)
    {
        if (episodeList is { Count: > 0 } && _config.History.Enabled && _config.History.IncludeCrArtists)
        {
            // Group all music videos together since we don't have artist info in the model yet
            await UpdateWithSeasonDataAsync(episodeList.Select(mv => new EpisodeInfo
            {
                Id = mv.Id ?? "",
                Title = mv.Title ?? "",
                SeriesId = mv.Id,
                SeasonId = mv.Id,
                SeriesTitle = mv.Title ?? "",
                SeasonTitle = mv.Title ?? "",
                Description = mv.Description,
                EpisodeNumber = 0,
                SeasonNumber = 0
            }).ToList());
        }
    }

    public async Task<(HistoryEpisode? HistoryEpisode, string DownloadDirPath)> GetHistoryEpisodeWithDownloadDirAsync(string? seriesId, string? seasonId, string episodeId)
    {
        await EnsureLoadedAsync();

        var snapshot = await GetHistorySnapshotAsync();
        var historySeries = snapshot.FirstOrDefault(series => series.SeriesId == seriesId);
        var downloadDirPath = "";

        if (historySeries != null)
        {
            var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
            if (!string.IsNullOrEmpty(historySeries.SeriesDownloadPath))
            {
                downloadDirPath = historySeries.SeriesDownloadPath;
            }

            if (historySeason != null)
            {
                var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episodeId);
                if (!string.IsNullOrEmpty(historySeason.SeasonDownloadPath))
                {
                    downloadDirPath = historySeason.SeasonDownloadPath;
                }

                if (historyEpisode != null)
                {
                    return (historyEpisode, downloadDirPath);
                }
            }
        }

        return (null, downloadDirPath);
    }

    public async Task<(HistoryEpisode? HistoryEpisode, List<string> DubList, List<string> SubList, string DownloadDirPath, string VideoQuality)> GetHistoryEpisodeWithDubListAndDownloadDirAsync(string? seriesId, string? seasonId, string episodeId)
    {
        await EnsureLoadedAsync();

        var snapshot = await GetHistorySnapshotAsync();
        var historySeries = snapshot.FirstOrDefault(series => series.SeriesId == seriesId);

        var downloadDirPath = "";
        var videoQuality = "";
        List<string> dublist = [];
        List<string> sublist = [];

        if (historySeries != null)
        {
            var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
            if (historySeries.HistorySeriesDubLangOverride.Count > 0)
            {
                dublist = historySeries.HistorySeriesDubLangOverride.ToList();
            }

            if (historySeries.HistorySeriesSoftSubsOverride.Count > 0)
            {
                sublist = historySeries.HistorySeriesSoftSubsOverride.ToList();
            }

            if (!string.IsNullOrEmpty(historySeries.SeriesDownloadPath))
            {
                downloadDirPath = historySeries.SeriesDownloadPath;
            }

            if (!string.IsNullOrEmpty(historySeries.HistorySeriesVideoQualityOverride))
            {
                videoQuality = historySeries.HistorySeriesVideoQualityOverride;
            }

            if (historySeason != null)
            {
                var historyEpisode = historySeason.EpisodesList.Find(e => e.EpisodeId == episodeId);
                if (historySeason.HistorySeasonDubLangOverride.Count > 0)
                {
                    dublist = historySeason.HistorySeasonDubLangOverride.ToList();
                }

                if (historySeason.HistorySeasonSoftSubsOverride.Count > 0)
                {
                    sublist = historySeason.HistorySeasonSoftSubsOverride.ToList();
                }

                if (!string.IsNullOrEmpty(historySeason.SeasonDownloadPath))
                {
                    downloadDirPath = historySeason.SeasonDownloadPath;
                }

                if (!string.IsNullOrEmpty(historySeason.HistorySeasonVideoQualityOverride))
                {
                    videoQuality = historySeason.HistorySeasonVideoQualityOverride;
                }

                if (historyEpisode != null)
                {
                    return (historyEpisode, dublist, sublist, downloadDirPath, videoQuality);
                }
            }
        }

        return (null, dublist, sublist, downloadDirPath, videoQuality);
    }

    public async Task<List<string>> GetDubListAsync(string? seriesId, string? seasonId)
    {
        await EnsureLoadedAsync();

        var snapshot = await GetHistorySnapshotAsync();
        var historySeries = snapshot.FirstOrDefault(series => series.SeriesId == seriesId);

        List<string> dublist = [];

        if (historySeries != null)
        {
            var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
            if (historySeries.HistorySeriesDubLangOverride.Count > 0)
            {
                dublist = historySeries.HistorySeriesDubLangOverride.ToList();
            }

            if (historySeason is { HistorySeasonDubLangOverride.Count: > 0 })
            {
                dublist = historySeason.HistorySeasonDubLangOverride.ToList();
            }
        }

        return dublist;
    }

    public async Task<(List<string> SubList, string VideoQuality)> GetSubListAsync(string? seriesId, string? seasonId)
    {
        await EnsureLoadedAsync();

        var snapshot = await GetHistorySnapshotAsync();
        var historySeries = snapshot.FirstOrDefault(series => series.SeriesId == seriesId);

        List<string> sublist = [];
        var videoQuality = "";

        if (historySeries != null)
        {
            var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
            if (historySeries.HistorySeriesSoftSubsOverride.Count > 0)
            {
                sublist = historySeries.HistorySeriesSoftSubsOverride.ToList();
            }

            if (!string.IsNullOrEmpty(historySeries.HistorySeriesVideoQualityOverride))
            {
                videoQuality = historySeries.HistorySeriesVideoQualityOverride;
            }

            if (historySeason is { HistorySeasonSoftSubsOverride.Count: > 0 })
            {
                sublist = historySeason.HistorySeasonSoftSubsOverride.ToList();
            }

            if (historySeason != null && !string.IsNullOrEmpty(historySeason.HistorySeasonVideoQualityOverride))
            {
                videoQuality = historySeason.HistorySeasonVideoQualityOverride;
            }
        }

        return (sublist, videoQuality);
    }

    public async Task SortItemsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            var currentSortingType = _config.HistoryPageProperties?.SelectedSorting ?? SortingType.SeriesTitle;
            var sortingDir = _config.HistoryPageProperties?.Ascending ?? false;
            DateTime today = DateTime.Now.Date;

            switch (currentSortingType)
            {
                case SortingType.SeriesTitle:
                    var sortedList = sortingDir
                        ? _historyList.OrderByDescending(s => s.SeriesTitle).ToList()
                        : _historyList.OrderBy(s => s.SeriesTitle).ToList();

                    _historyList.Clear();
                    _historyList.AddRange(sortedList);
                    return;

                case SortingType.NextAirDate:
                    var sortedSeriesDates = sortingDir
                        ? _historyList
                            .OrderByDescending(s =>
                            {
                                var date = ParseDate(s.SonarrNextAirDate ?? string.Empty, today);
                                return date ?? DateTime.MinValue;
                            })
                            .ThenByDescending(s => s.SonarrNextAirDate == "Today" ? 1 : 0)
                            .ThenBy(s => string.IsNullOrEmpty(s.SonarrNextAirDate) ? 1 : 0)
                            .ThenBy(s => s.SeriesTitle)
                            .ToList()
                        : _historyList
                            .OrderByDescending(s => s.SonarrNextAirDate == "Today")
                            .ThenBy(s => s.SonarrNextAirDate == "Today" ? s.SeriesTitle : null)
                            .ThenBy(s =>
                            {
                                var date = ParseDate(s.SonarrNextAirDate ?? string.Empty, today);
                                return date ?? DateTime.MaxValue;
                            })
                            .ThenBy(s => s.SeriesTitle)
                            .ToList();

                    _historyList.Clear();
                    _historyList.AddRange(sortedSeriesDates);
                    return;

                case SortingType.HistorySeriesAddDate:
                    var sortedSeriesAddDates = _historyList
                        .OrderBy(s => sortingDir
                            ? -(s.HistorySeriesAddDate?.Date.Ticks ?? DateTime.MinValue.Ticks)
                            : s.HistorySeriesAddDate?.Date.Ticks ?? DateTime.MaxValue.Ticks)
                        .ThenBy(s => s.SeriesTitle)
                        .ToList();

                    _historyList.Clear();
                    _historyList.AddRange(sortedSeriesAddDates);
                    return;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void SortSeasons(HistorySeries series)
    {
        var sortedSeasons = series.Seasons
            .OrderBy(s =>
            {
                double seasonNum;
                return double.TryParse(s.SeasonNum, NumberStyles.Any, CultureInfo.InvariantCulture, out seasonNum)
                    ? seasonNum
                    : double.MaxValue;
            })
            .ToList();

        series.Seasons.Clear();

        foreach (var season in sortedSeasons)
        {
            series.Seasons.Add(season);
        }
    }



    // When the same CR episode id appears in more than one season (e.g. a legacy season keyed by the
    // series-title fallback plus the real season added by a full populate), collapse them into the
    // episode in the real (available) season and carry over the downloaded state. CR episode ids are
    // unique per episode, so duplicates across seasons only arise from that migration case.
    internal static void DeduplicateEpisodesAcrossSeasons(HistorySeries series)
    {
        var byId = new Dictionary<string, List<(HistorySeason Season, HistoryEpisode Ep)>>();
        foreach (var season in series.Seasons)
        {
            foreach (var ep in season.EpisodesList)
            {
                if (string.IsNullOrEmpty(ep.EpisodeId)) continue;
                if (!byId.TryGetValue(ep.EpisodeId!, out var list))
                {
                    list = new List<(HistorySeason, HistoryEpisode)>();
                    byId[ep.EpisodeId!] = list;
                }
                list.Add((season, ep));
            }
        }

        foreach (var occurrences in byId.Values)
        {
            if (occurrences.Count < 2) continue;

            // Prefer the copy in a real (available) season; otherwise keep the first.
            var keep = occurrences.FirstOrDefault(o => o.Ep.IsEpisodeAvailableOnStreamingService).Ep
                       ?? occurrences[0].Ep;

            if (occurrences.Any(o => o.Ep.WasDownloaded)) keep.WasDownloaded = true;
            keep.DownloadedDubLang = occurrences.SelectMany(o => o.Ep.DownloadedDubLang)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            keep.DownloadedSoftSubs = occurrences.SelectMany(o => o.Ep.DownloadedSoftSubs)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var (season, ep) in occurrences)
            {
                if (!ReferenceEquals(ep, keep)) season.EpisodesList.Remove(ep);
            }
        }

        series.Seasons.RemoveAll(s => s.EpisodesList.Count == 0);
        foreach (var season in series.Seasons) season.UpdateDownloaded();
        series.UpdateNewEpisodes();
    }

    private void RemoveUnavailableEpisodesFromSeries(HistorySeries historySeries)
    {
        if (!_config.History.RemoveMissingEpisodes)
        {
            return;
        }

        var seasonsToRemove = new List<HistorySeason>();

        foreach (var season in historySeries.Seasons)
        {
            var unavailableEpisodes = season.EpisodesList
                .Where(episode => !episode.IsEpisodeAvailableOnStreamingService)
                .ToList();

            foreach (var episode in unavailableEpisodes)
            {
                season.EpisodesList.Remove(episode);
            }

            if (season.EpisodesList.Count == 0)
            {
                seasonsToRemove.Add(season);
                continue;
            }

            season.EpisodesList.Sort(new NumericStringPropertyComparer());
            season.UpdateDownloaded();
        }

        foreach (var season in seasonsToRemove)
        {
            historySeries.Seasons.Remove(season);
        }

        historySeries.UpdateNewEpisodes();
        SortSeasons(historySeries);
    }

    public double CalculateSimilarity(string source, string target)
    {
        return StringSimilarity.CalculateSimilarity(source, target);
    }

    public DateTime? ParseDate(string dateStr, DateTime today)
    {
        if (dateStr == "Today")
        {
            return today;
        }

        if (DateTime.TryParseExact(dateStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date;
        }

        return null;
    }

    // Updates existing history episodes with metadata from browse/calendar data without triggering a full series refresh
    public async Task RefreshExistingEpisodesFromBrowseAsync(List<CrBrowseEpisode> episodes)
    {
        if (episodes == null || episodes.Count == 0) return;

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            bool anyUpdated = false;

            foreach (var browseEpisode in episodes)
            {
                if (string.IsNullOrWhiteSpace(browseEpisode.Id)) continue;

                // Find the episode in history
                var historyEpisode = _historyList
                    .SelectMany(s => s.Seasons)
                    .SelectMany(se => se.EpisodesList)
                    .FirstOrDefault(e => e.EpisodeId == browseEpisode.Id);

                if (historyEpisode == null) continue;

                // Update available dubs from versions
                var availableDubs = new List<string>();
                if (browseEpisode.EpisodeMetadata?.Versions != null)
                {
                    foreach (var version in browseEpisode.EpisodeMetadata.Versions)
                    {
                        if (!string.IsNullOrWhiteSpace(version.AudioLocale) &&
                            !availableDubs.Contains(version.AudioLocale, StringComparer.OrdinalIgnoreCase))
                        {
                            availableDubs.Add(version.AudioLocale);
                        }
                    }
                }

                // Also add the primary audio locale if present
                if (!string.IsNullOrWhiteSpace(browseEpisode.EpisodeMetadata?.AudioLocale) &&
                    !availableDubs.Contains(browseEpisode.EpisodeMetadata.AudioLocale, StringComparer.OrdinalIgnoreCase))
                {
                    availableDubs.Add(browseEpisode.EpisodeMetadata.AudioLocale);
                }

                // Update available subs
                var availableSubs = browseEpisode.EpisodeMetadata?.SubtitleLocales?.ToList() ?? new List<string>();

                // Only update if there's a meaningful change
                var dubsChanged = !historyEpisode.HistoryEpisodeAvailableDubLang.SequenceEqual(availableDubs, StringComparer.OrdinalIgnoreCase);
                var subsChanged = !historyEpisode.HistoryEpisodeAvailableSoftSubs.SequenceEqual(availableSubs, StringComparer.OrdinalIgnoreCase);

                if (dubsChanged || subsChanged)
                {
                    historyEpisode.HistoryEpisodeAvailableDubLang = availableDubs;
                    historyEpisode.HistoryEpisodeAvailableSoftSubs = availableSubs;
                    historyEpisode.IsEpisodeAvailableOnStreamingService = true;
                    anyUpdated = true;

                    _logger?.LogDebug("Updated episode {EpisodeId} metadata from browse data: dubs=[{Dubs}], subs=[{Subs}]",
                        browseEpisode.Id,
                        string.Join(",", availableDubs),
                        string.Join(",", availableSubs));
                }
            }

            if (anyUpdated)
            {
                // Update series-level aggregated metadata
                foreach (var series in _historyList)
                {
                    series.UpdateNewEpisodes();
                }

                await SaveRichHistoryAsync();
                _logger?.LogInformation("Refreshed {Count} existing episodes from browse data", episodes.Count);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // Fetches series from API and returns the thumbnail URL
    public async Task<string?> GetSeriesThumbnailAsync(string seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || _apiService == null)
        {
            return null;
        }

        try
        {
            var lang = string.IsNullOrEmpty(_config.History.Lang) ? "en-US" : _config.History.Lang;
            var seriesData = await _apiService.SeriesByIdAsync(seriesId, lang, true);

            if (seriesData == null)
            {
                return null;
            }

            // Prefer ThumbnailUrl if available
            if (!string.IsNullOrWhiteSpace(seriesData.ThumbnailUrl))
            {
                return seriesData.ThumbnailUrl;
            }

            // Fallback to CoverArtUrl
            if (!string.IsNullOrWhiteSpace(seriesData.CoverArtUrl))
            {
                return seriesData.CoverArtUrl;
            }

            // Fallback to first image in Images list
            if (seriesData.Images.Count > 0)
            {
                return seriesData.Images.First();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get series thumbnail for {SeriesId}", seriesId);
            return null;
        }
    }

    public async Task SetSeriesSettingsOverrideAsync(string seriesId, string? videoQuality, List<string>? dubLanguages, List<string>? softSubs)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var historySeries = _historyList.FirstOrDefault(s => s.SeriesId == seriesId);
            if (historySeries == null) return;

            if (!string.IsNullOrEmpty(videoQuality))
                historySeries.HistorySeriesVideoQualityOverride = videoQuality;
            else
                historySeries.HistorySeriesVideoQualityOverride = "";

            historySeries.HistorySeriesDubLangOverride = NormalizeLocales(dubLanguages);
            historySeries.HistorySeriesSoftSubsOverride = NormalizeLocales(softSubs);

            await SaveRichHistoryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSeasonSettingsOverrideAsync(string seasonId, string? videoQuality, List<string>? dubLanguages, List<string>? softSubs)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            // Find season across all series
            foreach (var historySeries in _historyList)
            {
                var historySeason = historySeries.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
                if (historySeason != null)
                {
                    if (!string.IsNullOrEmpty(videoQuality))
                        historySeason.HistorySeasonVideoQualityOverride = videoQuality;
                    else
                        historySeason.HistorySeasonVideoQualityOverride = "";

                    historySeason.HistorySeasonDubLangOverride = NormalizeLocales(dubLanguages);
                    historySeason.HistorySeasonSoftSubsOverride = NormalizeLocales(softSubs);

                    await SaveRichHistoryAsync();
                    return;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<string> NormalizeLocales(IEnumerable<string?>? locales)
    {
        return (locales ?? [])
            .Where(locale => !string.IsNullOrWhiteSpace(locale))
            .Select(locale => locale!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _loadSemaphore?.Dispose();
    }
}

public class NumericStringPropertyComparer : IComparer<HistoryEpisode>
{
    public int Compare(HistoryEpisode? x, HistoryEpisode? y)
    {
        if (double.TryParse(x?.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out double xDouble) &&
            double.TryParse(y?.Episode, NumberStyles.Any, CultureInfo.InvariantCulture, out double yDouble))
        {
            return xDouble.CompareTo(yDouble);
        }

        // [PT] Upstream: sort multi-episode ranges like "11-12" by their starting number
        if (TryParseEpisodeSortNumber(x?.Episode, out xDouble) &&
            TryParseEpisodeSortNumber(y?.Episode, out yDouble))
        {
            int numericCompare = xDouble.CompareTo(yDouble);
            if (numericCompare != 0)
            {
                return numericCompare;
            }
        }

        return string.Compare(x?.Episode, y?.Episode, StringComparison.Ordinal);
    }

    private static bool TryParseEpisodeSortNumber(string? episode, out double episodeNumber)
    {
        return double.TryParse(episode, NumberStyles.Any, CultureInfo.InvariantCulture, out episodeNumber) ||
               TryParseEpisodeRangeStart(episode, out episodeNumber);
    }

    private static bool TryParseEpisodeRangeStart(string? episode, out double episodeNumber)
    {
        episodeNumber = 0;
        if (string.IsNullOrWhiteSpace(episode))
        {
            return false;
        }

        string[] parts = episode.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out episodeNumber) &&
               double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
}
