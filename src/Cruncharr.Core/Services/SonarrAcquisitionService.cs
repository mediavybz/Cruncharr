using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

public record SonarrCandidate(int TvdbId, string Title, int Year);
public sealed class SonarrMatchRequiredException(string seriesId, List<SonarrCandidate> candidates)
    : InvalidOperationException("Choose the matching Sonarr series before continuing.")
{
    public List<SonarrCandidate> Candidates { get; } = candidates;
    public string SeriesId { get; } = seriesId;
}

public class SonarrAcquisitionState
{
    [JsonIgnore] public bool AlreadyInSonarr { get; set; }
    public string SeriesId { get; set; } = "";
    public string Title { get; set; } = "";
    public int SonarrSeriesId { get; set; }
    public int TvdbId { get; set; }
    public string Server { get; set; } = "";
    public HashSet<string> PendingEpisodes { get; set; } = [];
    public Dictionary<string, DateTime> SearchedEpisodes { get; set; } = [];
    public Dictionary<string, int?> PendingImports { get; set; } = [];
    public Dictionary<string, string> ImportEpisodeIds { get; set; } = [];
    public bool NeedsEpisodeMatch { get; set; } = true;
    public DateTime ProviderUpdatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime RetryAfterUtc { get; set; }
    public string Status { get; set; } = "Waiting for Sonarr";
    public string? LastError { get; set; }
    public int EpisodeCount { get; set; }
    public int EpisodeFileCount { get; set; }
}

public interface ISonarrAcquisitionService
{
    Task<SonarrAcquisitionState> PrepareAsync(EpisodeInfo episode, bool premium, int? tvdbId = null, CancellationToken cancellationToken = default);
    Task RegisterImportAsync(EpisodeInfo episode, string outputPath, CancellationToken cancellationToken = default);
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
    Task<List<SonarrAcquisitionState>> GetStatusAsync();
}

public sealed class SonarrAcquisitionService(
    ISonarrService sonarr, ICrunchyrollApiService api, IHistoryService history, CruncharrConfig config,
    ILogger<SonarrAcquisitionService>? logger = null, string? statePath = null) : ISonarrAcquisitionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = statePath ?? Path.Combine(AppContext.BaseDirectory, "sonarr-requests.json");
    private List<SonarrAcquisitionState>? _state;
    private DateTime _lastLibrarySync;
    private string Server => $"{config.Sonarr.UseSsl}|{config.Sonarr.Host}|{config.Sonarr.Port}|{config.Sonarr.UrlBase}";
    private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;

    private async Task LoadAsync()
    {
        if (_state != null) return;
        _state = File.Exists(_path)
            ? JsonConvert.DeserializeObject<List<SonarrAcquisitionState>>(await File.ReadAllTextAsync(_path))
                ?? throw new InvalidDataException("Sonarr request state is invalid.")
            : [];
        if (_state.Any(s => string.IsNullOrWhiteSpace(s.SeriesId) || s.TvdbId <= 0 || s.SonarrSeriesId <= 0 ||
                            s.PendingEpisodes == null || s.PendingImports == null || s.ImportEpisodeIds == null || s.SearchedEpisodes == null))
        {
            _state = null;
            throw new InvalidDataException("Sonarr request state is invalid; restore it before submitting requests.");
        }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonConvert.SerializeObject(_state, Formatting.Indented));
        File.Move(temporary, _path, overwrite: true);
    }

    public async Task<List<SonarrAcquisitionState>> GetStatusAsync()
    {
        await _gate.WaitAsync();
        try { await LoadAsync(); return Copy(_state!.Where(s => s.Server == Server).ToList()); }
        finally { _gate.Release(); }
    }

    public async Task<SonarrAcquisitionState> PrepareAsync(EpisodeInfo episode, bool premium, int? tvdbId = null, CancellationToken cancellationToken = default)
    {
        if (!config.Sonarr.Enabled || (!premium && !config.Sonarr.SearchWithoutPremium))
            throw new InvalidOperationException("Enable Sonarr requests in Settings / Sonarr, or sign in to Crunchyroll Premium.");
        if (string.IsNullOrWhiteSpace(episode.SeriesId))
        {
            var metadata = await api.ParseEpisodeByIdAsync(episode.Id, null, false, cancellationToken)
                ?? throw new InvalidOperationException("Crunchyroll could not identify this episode's series.");
            episode.SeriesId = metadata.SeriesId;
            episode.SeriesTitle = metadata.SeriesTitle;
            episode.SeasonId = metadata.SeasonId;
            episode.SeasonTitle = metadata.SeasonTitle;
            episode.SeasonNumber = metadata.SeasonNumber;
            episode.EpisodeNumber = metadata.EpisodeNumber;
            episode.Episode = metadata.Episode;
            episode.Title = metadata.Title;
        }
        if (string.IsNullOrWhiteSpace(episode.SeriesId) || string.IsNullOrWhiteSpace(episode.SeriesTitle))
            throw new InvalidOperationException("Episode metadata does not identify a series.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync();
            var link = _state!.FirstOrDefault(s => s.Server == Server && s.SeriesId == episode.SeriesId);
            var library = await sonarr.GetCurrentSeriesAsync(config.Sonarr, cancellationToken);
            var match = link == null ? null : library.FirstOrDefault(s => s.TvdbId == link.TvdbId);
            if (match == null && tvdbId == null)
                match = await sonarr.ResolveSeriesAsync(episode.SeriesId, episode.SeriesTitle, config.Sonarr, cancellationToken);
            if (match == null || tvdbId.HasValue && match.TvdbId != tvdbId.Value)
            {
                var lookup = await sonarr.LookupSeriesAsync(episode.SeriesTitle, config.Sonarr).WaitAsync(cancellationToken);
                var index = new SonarrTitleIndex(lookup);
                var candidates = index.Candidates(episode.SeriesTitle).Where(s => s.TvdbId > 0).ToList();
                var selected = tvdbId.HasValue ? candidates.SingleOrDefault(s => s.TvdbId == tvdbId.Value) : index.FindExact(episode.SeriesTitle);
                if (selected == null || selected.TvdbId <= 0)
                {
                    if (candidates.Count == 0) throw new InvalidOperationException("Sonarr has no matching TVDB series. Check the title in Sonarr.");
                    throw new SonarrMatchRequiredException(episode.SeriesId, candidates.Select(s => new SonarrCandidate(s.TvdbId, s.Title ?? "", s.Year)).ToList());
                }
                match = await sonarr.AddSeriesAsync(selected.TvdbId, config.Sonarr, cancellationToken);
            }
            if (premium && config.Sonarr.UnmonitorPremiumRequests)
                await sonarr.SetMonitoringAsync(match.Id, false, config.Sonarr, cancellationToken);
            link ??= new SonarrAcquisitionState { SeriesId = episode.SeriesId, Server = Server };
            if (!_state!.Contains(link)) _state.Add(link);
            if (link.SonarrSeriesId != match.Id) link.NeedsEpisodeMatch = true;
            link.SonarrSeriesId = match.Id;
            link.TvdbId = match.TvdbId;
            link.Title = episode.SeriesTitle;
            link.EpisodeCount = match.Statistics?.TotalEpisodeCount ?? 0;
            link.EpisodeFileCount = match.Statistics?.EpisodeFileCount ?? 0;
            link.LastError = null;
            link.UpdatedUtc = DateTime.UtcNow;
            if (DateTime.UtcNow - link.ProviderUpdatedUtc > TimeSpan.FromMinutes(10))
            {
                var episodes = await api.GetEpisodesAsync(episode.SeriesId, true, cancellationToken);
                if (episodes.Count == 0) throw new InvalidOperationException("Episode metadata is unavailable; the series is in Sonarr, but the request cannot yet be matched.");
                foreach (var season in episodes.GroupBy(e => e.SeasonId)) await history.UpdateWithSeasonDataAsync(season.ToList());
                link.ProviderUpdatedUtc = DateTime.UtcNow;
                link.NeedsEpisodeMatch = true;
            }
            await history.SetSonarrSeriesAsync(episode.SeriesId, match);
            var alreadyInSonarr = false;
            if (premium && !config.Download.ReplaceExistingFiles)
            {
                var targets = await sonarr.GetCurrentEpisodesAsync(match.Id, config.Sonarr, true, cancellationToken);
                if (targets.Count > 0)
                {
                    if (link.NeedsEpisodeMatch)
                    {
                        await history.MatchHistoryEpisodesWithSonarrAsync(episode.SeriesId);
                        link.NeedsEpisodeMatch = false;
                    }
                    var selected = (await history.GetHistorySeriesAsync()).FirstOrDefault(s => s.SeriesId == episode.SeriesId)
                        ?.Seasons.SelectMany(s => s.EpisodesList).FirstOrDefault(e => e.EpisodeId == episode.Id);
                    alreadyInSonarr = targets.Any(t => t.Id.ToString() == selected?.SonarrEpisodeId && t.HasFile);
                }
            }
            if (!premium)
            {
                if (!link.SearchedEpisodes.TryGetValue(episode.Id, out var lastSearch) || DateTime.UtcNow - lastSearch > TimeSpan.FromMinutes(30))
                    link.PendingEpisodes.Add(episode.Id);
                link.Status = link.PendingEpisodes.Count > 0 ? "Waiting to search selected episodes" : "Already requested in Sonarr";
            }
            else
            {
                // A Premium request for this show cancels unsubmitted guest searches.
                link.PendingEpisodes.Clear();
                link.Status = alreadyInSonarr ? "Selected episode is already in Sonarr" : "Waiting for Cruncharr downloads";
            }
            await SaveAsync();
            var result = Copy(link);
            result.AlreadyInSonarr = alreadyInSonarr;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task RegisterImportAsync(EpisodeInfo episode, string outputPath, CancellationToken cancellationToken = default)
    {
        var localPath = Path.GetFullPath(outputPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync();
            var link = _state!.FirstOrDefault(s => s.Server == Server && s.SeriesId == episode.SeriesId)
                ?? throw new InvalidOperationException("This download has no registered Sonarr series.");
            link.PendingImports.TryAdd(localPath, null);
            link.ImportEpisodeIds[localPath] = episode.Id;
            link.NeedsEpisodeMatch = true;
            link.Status = "Waiting for Sonarr import";
            await SaveAsync();
        }
        finally { _gate.Release(); }
    }

    internal static string MapImportPath(string path, string outputDirectory, string sonarrDirectory)
    {
        if (string.IsNullOrWhiteSpace(sonarrDirectory)) return Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(outputDirectory), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The completed download is outside the configured download folder.");
        return sonarrDirectory.TrimEnd('/', '\\') + "/" + relative.Replace('\\', '/');
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!config.Sonarr.Enabled || !await _gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await LoadAsync();
            var library = await sonarr.GetCurrentSeriesAsync(config.Sonarr, cancellationToken);
            foreach (var link in _state!.Where(s => s.Server == Server))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentSeries = library.FirstOrDefault(s => s.TvdbId == link.TvdbId);
                if (currentSeries == null)
                {
                    link.LastError = "The registered series was removed from Sonarr. Submit a new request to add it again.";
                    continue;
                }
                if (currentSeries.Id != link.SonarrSeriesId)
                {
                    link.SonarrSeriesId = currentSeries.Id;
                    link.NeedsEpisodeMatch = true;
                    link.RetryAfterUtc = default;
                    await history.SetSonarrSeriesAsync(link.SeriesId, currentSeries);
                }
                if (link.RetryAfterUtc > DateTime.UtcNow) continue;
                try
                {
                    if (link.PendingEpisodes.Count > 0 && config.Sonarr.SearchWithoutPremium)
                    {
                        var targets = await sonarr.GetCurrentEpisodesAsync(link.SonarrSeriesId, config.Sonarr, true, cancellationToken);
                        if (targets.Count == 0) { link.Status = "Waiting for Sonarr episode metadata"; link.RetryAfterUtc = DateTime.UtcNow.AddMinutes(1); continue; }
                        if (link.NeedsEpisodeMatch)
                        {
                            await history.MatchHistoryEpisodesWithSonarrAsync(link.SeriesId);
                            link.NeedsEpisodeMatch = false;
                        }
                        var series = (await history.GetHistorySeriesAsync()).FirstOrDefault(s => s.SeriesId == link.SeriesId);
                        var requested = series?.Seasons.SelectMany(s => s.EpisodesList)
                            .Where(e => link.PendingEpisodes.Contains(e.EpisodeId ?? "") && int.TryParse(e.SonarrEpisodeId, out _)).ToList() ?? [];
                        var available = requested.Where(e => targets.Any(t => t.Id.ToString() == e.SonarrEpisodeId && t.HasFile)).ToList();
                        var missing = requested.Except(available).Select(e => int.Parse(e.SonarrEpisodeId!)).Distinct().ToList();
                        if (missing.Count > 0) await sonarr.SearchEpisodesAsync(link.SonarrSeriesId, missing, config.Sonarr, cancellationToken);
                        foreach (var episode in requested)
                        {
                            link.PendingEpisodes.Remove(episode.EpisodeId!);
                            link.SearchedEpisodes[episode.EpisodeId!] = DateTime.UtcNow;
                        }
                        link.Status = link.PendingEpisodes.Count > 0 ? "Waiting for unmatched episode metadata" : missing.Count > 0 ? "Search sent to Sonarr" : "Selected episodes are already in Sonarr";
                        if (link.PendingEpisodes.Count > 0) { link.NeedsEpisodeMatch = true; link.RetryAfterUtc = DateTime.UtcNow.AddMinutes(1); }
                    }
                    if (link.PendingImports.Count > 0 && link.NeedsEpisodeMatch)
                    {
                        await history.MatchHistoryEpisodesWithSonarrAsync(link.SeriesId);
                        link.NeedsEpisodeMatch = false;
                    }
                    foreach (var path in link.PendingImports.Keys.ToList())
                    {
                        if (link.PendingImports[path] == null)
                        {
                            var mapped = MapImportPath(path, config.Download.OutputDirectory, config.Sonarr.DownloadPath);
                            var sourceId = link.ImportEpisodeIds.GetValueOrDefault(path);
                            var source = (await history.GetHistorySeriesAsync()).FirstOrDefault(s => s.SeriesId == link.SeriesId)
                                ?.Seasons.SelectMany(s => s.EpisodesList).FirstOrDefault(e => e.EpisodeId == sourceId);
                            if (!int.TryParse(source?.SonarrEpisodeId, out var episodeId))
                            {
                                link.NeedsEpisodeMatch = true;
                                throw new InvalidOperationException("Waiting for a confirmed Sonarr episode match before importing.");
                            }
                            link.PendingImports[path] = await sonarr.ImportFileAsync(mapped, link.SonarrSeriesId, config.Sonarr, cancellationToken, episodeId);
                            await SaveAsync();
                        }
                        var status = await sonarr.GetCommandStatusAsync(link.PendingImports[path]!.Value, config.Sonarr, cancellationToken);
                        if (status is "failed" or "aborted" or "cancelled" or "missing")
                        {
                            link.PendingImports[path] = null;
                            throw new InvalidOperationException("Sonarr could not import the download. Check the download path mapping and Sonarr logs.");
                        }
                        if (status == "completed")
                        {
                            link.PendingImports.Remove(path);
                            link.ImportEpisodeIds.Remove(path);
                            sonarr.InvalidateCache();
                            link.NeedsEpisodeMatch = true;
                            link.Status = "Sonarr import scan completed";
                        }
                    }
                    if (link.NeedsEpisodeMatch && link.PendingEpisodes.Count == 0)
                    {
                        var episodes = await sonarr.GetCurrentEpisodesAsync(link.SonarrSeriesId, config.Sonarr, false, cancellationToken);
                        if (episodes.Count > 0)
                        {
                            await history.MatchHistoryEpisodesWithSonarrAsync(link.SeriesId);
                            link.NeedsEpisodeMatch = false;
                        }
                    }
                    link.LastError = null;
                    link.UpdatedUtc = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    link.LastError = ex.Message;
                    link.RetryAfterUtc = DateTime.UtcNow.AddMinutes(1);
                    logger?.LogWarning(ex, "Sonarr request for {Title} is pending", link.Title);
                }
                await SaveAsync();
            }
            library = await sonarr.GetCurrentSeriesAsync(config.Sonarr, cancellationToken);
            foreach (var link in _state!.Where(s => s.Server == Server))
            {
                var currentSeries = library.FirstOrDefault(s => s.TvdbId == link.TvdbId);
                if (currentSeries == null) continue;
                link.EpisodeCount = currentSeries.Statistics?.TotalEpisodeCount ?? 0;
                link.EpisodeFileCount = currentSeries.Statistics?.EpisodeFileCount ?? 0;
            }
            await SaveAsync();
        }
        finally { _gate.Release(); }
        // Library-wide refreshes must not hold the request-state lock while reading every show.
        if (DateTime.UtcNow - _lastLibrarySync > TimeSpan.FromMinutes(1))
        {
            _lastLibrarySync = DateTime.UtcNow;
            await history.MatchHistorySeriesWithSonarrAsync();
            await history.RefreshSonarrFileStatusAsync(cancellationToken);
        }
    }
}
