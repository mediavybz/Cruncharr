using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

// Explicit subscriptions have their own durable baseline. Merely browsing or downloading a
// series must never subscribe its entire back catalog to automatic downloads.
public sealed class ScheduledDownloadsService
{
    private readonly string _path;
    private readonly IHistoryService _history;
    private readonly IQueueService _queue;
    private readonly ICrunchyrollAuthService _auth;
    private readonly ISonarrService _sonarr;
    private readonly CruncharrConfig _config;
    private readonly ISonarrAcquisitionService? _sonarrRequests;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SchedulerState _state;
    private readonly string? _loadError;
    public bool IsRunning { get; private set; }

    public ScheduledDownloadsService(IHistoryService history, IQueueService queue,
        ICrunchyrollAuthService auth, ISonarrService sonarr, CruncharrConfig config, string? statePath = null, ISonarrAcquisitionService? sonarrRequests = null)
    {
        (_history, _queue, _auth, _sonarr, _config) = (history, queue, auth, sonarr, config);
        _sonarrRequests = sonarrRequests;
        var configPath = CruncharrConfig.ConfigFilePath;
        _path = statePath ?? Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "subscriptions.json");
        // A corrupt subscription file must not reset the baseline and enqueue old episodes.
        try
        {
            _state = File.Exists(_path)
                ? JsonConvert.DeserializeObject<SchedulerState>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("Invalid subscriptions file")
                : new SchedulerState();
            if (_state.IntervalMinutes is < 1 or > 1440 || _state.Subscriptions == null ||
                _state.Subscriptions.Any(s => string.IsNullOrWhiteSpace(s.SeriesId) || s.HandledEpisodeIds == null))
                throw new InvalidDataException("Invalid subscription settings");
        }
        catch (Exception ex)
        {
            _loadError = "Could not read subscriptions.json. Restore the file from a backup before changing schedules. " + ex.Message;
            _state = new SchedulerState { Enabled = false, LastError = _loadError };
        }
    }

    private SchedulerState CopyState() => JsonConvert.DeserializeObject<SchedulerState>(JsonConvert.SerializeObject(_state))!;
    public object GetStatus()
    {
        var state = _state;
        return new
        {
            state.Enabled, state.IntervalMinutes, IsRunning, state.LastRun, state.LastError,
            state.LastQueuedCount, state.LastRunSummary,
            NextRun = state.Enabled && _config.History.Enabled && state.Subscriptions.Any(s => s.Enabled)
                ? state.LastRun?.AddMinutes(state.IntervalMinutes) ?? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            HistoryEnabled = _config.History.Enabled,
            CanDownload = _auth.IsAuthenticated && _auth.Profile.HasPremium ||
                _config.Sonarr.Enabled && _config.Sonarr.SearchWithoutPremium && _sonarrRequests != null,
            Destination = _auth.IsAuthenticated && _auth.Profile.HasPremium ? "cruncharr" : "sonarr",
            AutoDownload = _config.Queue.AutoDownload,
            Subscriptions = state.Subscriptions.Select(s => new { s.SeriesId, s.Title, s.Enabled, s.CreatedAt }).ToList()
        };
    }

    private void Save(SchedulerState state)
    {
        if (_loadError != null) throw new InvalidOperationException(_loadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(_path + ".tmp", _path, overwrite: true);
        _state = state; // Publish only after persistence succeeds.
    }

    public async Task ConfigureAsync(bool enabled, int intervalMinutes, CancellationToken ct)
    {
        if (intervalMinutes is < 1 or > 1440) throw new ArgumentException("Check interval must be 1–1440 minutes.");
        await _gate.WaitAsync(ct);
        try
        {
            var state = CopyState();
            state.Enabled = enabled;
            state.IntervalMinutes = intervalMinutes;
            Save(state);
        }
        finally { _gate.Release(); }
    }

    public async Task SubscribeAsync(string seriesId, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = CopyState();
            var subscription = state.Subscriptions.FirstOrDefault(s => s.SeriesId == seriesId);
            if (subscription == null)
            {
                if (!enabled) return;
                if (!_config.History.Enabled) throw new ArgumentException("Enable History before scheduling a series.");
                if (!await _history.CrUpdateSeriesAsync(seriesId, ""))
                    throw new ArgumentException("Could not load this series. No schedule was created; try again.");
                var series = (await _history.GetHistorySeriesAsync()).FirstOrDefault(s => s.SeriesId == seriesId)
                    ?? throw new ArgumentException("Series was not found in History.");
                subscription = new SeriesSubscription { SeriesId = seriesId, Title = series.SeriesTitle ?? seriesId };
                subscription.HandledEpisodeIds = series.Seasons.SelectMany(s => s.EpisodesList)
                    .Where(e => IsReleased(e, subscription.CreatedAt))
                    .Select(e => e.EpisodeId!).ToHashSet(StringComparer.Ordinal);
                state.Subscriptions.Add(subscription);
            }
            subscription.Enabled = enabled;
            Save(state);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string seriesId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = CopyState();
            state.Subscriptions.RemoveAll(s => s.SeriesId == seriesId);
            Save(state);
        }
        finally { _gate.Release(); }
    }

    internal static bool IsReleased(HistoryEpisode episode, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(episode.EpisodeId) && episode.IsEpisodeAvailableOnStreamingService &&
        (!episode.EpisodeCrPremiumAirDate.HasValue || episode.EpisodeCrPremiumAirDate.Value <= now.UtcDateTime);

    // Returns null when the check was skipped (not due, disabled, or another run in progress).
    public async Task<SchedulerRunSummary?> RunCheckAsync(bool force, CancellationToken ct)
    {
        if (_loadError != null) { if (force) throw new InvalidOperationException(_loadError); return null; }
        if (!force && (!_state.Enabled || !_config.History.Enabled ||
            !_state.Subscriptions.Any(s => s.Enabled) ||
            _state.LastRun?.AddMinutes(_state.IntervalMinutes) > DateTimeOffset.UtcNow)) return null;
        if (!await _gate.WaitAsync(0, ct))
        {
            if (force) throw new InvalidOperationException("A scheduler operation is already running.");
            return null;
        }
        IsRunning = true;
        var state = CopyState();
        state.LastQueuedCount = 0;
        state.LastError = null;
        var summary = new SchedulerRunSummary();
        try
        {
            if (!_config.History.Enabled) throw new InvalidOperationException("History is disabled.");
            foreach (var subscription in state.Subscriptions.Where(s => s.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                var show = await CheckSeriesAsync(subscription, backfill: false, ct);
                summary.Shows.Add(show);
                state.LastQueuedCount += show.Added;
                if (show.Error != null) state.LastError = show.Error;
            }
        }
        finally
        {
            state.LastRun = summary.FinishedAt = DateTimeOffset.UtcNow;
            state.LastRunSummary = summary;
            try { Save(state); }
            finally { IsRunning = false; _gate.Release(); }
        }
        return summary;
    }

    // Explicit, user-triggered catch-up for one subscription: queues released episodes that have
    // no local or Sonarr file, including those the subscription baseline marked handled. Scheduled
    // checks stay new-episodes-only, so subscribing never floods the queue by itself.
    public async Task<SchedulerRunSummary> BackfillAsync(string seriesId, CancellationToken ct)
    {
        if (_loadError != null) throw new InvalidOperationException(_loadError);
        if (!_config.History.Enabled) throw new InvalidOperationException("History is disabled.");
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("A scheduler operation is already running.");
        IsRunning = true;
        try
        {
            var state = CopyState();
            var subscription = state.Subscriptions.FirstOrDefault(s => s.SeriesId == seriesId)
                ?? throw new ArgumentException("This series is not scheduled.");
            var summary = new SchedulerRunSummary { Backfill = true };
            summary.Shows.Add(await CheckSeriesAsync(subscription, backfill: true, ct));
            summary.FinishedAt = DateTimeOffset.UtcNow;
            Save(state);
            return summary;
        }
        finally { IsRunning = false; _gate.Release(); }
    }

    // Evaluates every History episode of one subscription and records why each one was or was not
    // added, so a run that adds nothing explains itself. Failures are reported on the result.
    private async Task<SubscriptionRunResult> CheckSeriesAsync(SeriesSubscription subscription, bool backfill, CancellationToken ct)
    {
        var show = new SubscriptionRunResult { SeriesId = subscription.SeriesId, Title = subscription.Title };
        try
        {
            if (!await _history.CrUpdateSeriesAsync(subscription.SeriesId, ""))
                throw new InvalidOperationException($"Could not refresh {subscription.Title}; will retry.");
            var series = (await _history.GetHistorySeriesAsync()).FirstOrDefault(s => s.SeriesId == subscription.SeriesId)
                ?? throw new InvalidOperationException($"{subscription.Title} is missing from History.");
            var premium = _auth.IsAuthenticated && _auth.Profile.HasPremium;
            if (!premium && (!_config.Sonarr.Enabled || !_config.Sonarr.SearchWithoutPremium || _sonarrRequests == null))
            {
                show.Error = $"{subscription.Title} was not checked: sign in with Premium or enable Sonarr requests.";
                return show;
            }
            var local = HistoryService.GetEpisodeIdsWithExistingArtifacts(await _history.GetAllAsync(0, int.MaxValue) ?? []);
            bool sonarrUnavailable = false;
            var files = _config.Sonarr.Enabled
                ? await HistoryService.GetCurrentSonarrArtifactEpisodeIdsAsync([series], _sonarr, _config.Sonarr, ct,
                    (_, _) => sonarrUnavailable = true) : [];
            if (sonarrUnavailable) throw new InvalidOperationException($"Sonarr is unavailable for {subscription.Title}; will retry without downloading duplicates.");
            foreach (var season in series.Seasons)
            foreach (var episode in season.EpisodesList)
            {
                if (!IsReleased(episode, DateTimeOffset.UtcNow)) { show.NotReleased++; continue; }
                if ((season.SpecialSeason || episode.SpecialEpisode) && !_config.History.AddSpecials) { show.Excluded++; continue; }
                if (_config.History.SkipUnmonitored && _config.Sonarr.Enabled && !_config.Sonarr.AutoAddSeries &&
                    !string.IsNullOrEmpty(episode.SonarrEpisodeId) && !episode.SonarrIsMonitored) { show.Excluded++; continue; }
                if (local.Contains(episode.EpisodeId!) || files.Contains(episode.EpisodeId!))
                {
                    subscription.HandledEpisodeIds.Add(episode.EpisodeId!);
                    show.HaveFile++;
                    continue;
                }
                if (!backfill && subscription.HandledEpisodeIds.Contains(episode.EpisodeId!))
                {
                    // Released before subscribing (or already queued/requested): scheduled checks
                    // only add new releases. Backfill is the explicit way to fetch these.
                    show.MissingFromBaseline++;
                    continue;
                }
                if (!premium)
                {
                    await _sonarrRequests!.PrepareAsync(new EpisodeInfo {
                        Id = episode.EpisodeId!, SeriesId = series.SeriesId, SeriesTitle = series.SeriesTitle ?? subscription.Title,
                        SeasonId = season.SeasonId, Title = episode.EpisodeTitle ?? "Episode"
                    }, false, cancellationToken: ct);
                    subscription.HandledEpisodeIds.Add(episode.EpisodeId!);
                    show.Added++;
                    continue;
                }
                var dubs = (season.HistorySeasonDubLangOverride.Count > 0 ? season.HistorySeasonDubLangOverride :
                    series.HistorySeriesDubLangOverride.Count > 0 ? series.HistorySeriesDubLangOverride :
                    _config.Download.DownloadMultipleDubs ? _config.Download.DubLanguages : new List<string> { _config.Download.DefaultAudio })
                    .Where(l => !l.Equals("none", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var subs = (season.HistorySeasonSoftSubsOverride.Count > 0 ? season.HistorySeasonSoftSubsOverride :
                    series.HistorySeriesSoftSubsOverride.Count > 0 ? series.HistorySeriesSoftSubsOverride : _config.Download.SoftSubs).ToList();
                // Wait for all requested dubs, including delayed releases, rather than
                // permanently marking an episode handled after downloading another language.
                if (dubs.Count == 0 || dubs.Any(d => !HistoryEpisode.ContainsNormalized(episode.HistoryEpisodeAvailableDubLang, d)))
                { show.WaitingForLanguage++; continue; }
                if (!_config.Download.SkipSubs && _config.Download.DownloadOnlyWithAllSelectedDubSub && subs.Any(s => s is not ("all" or "none") &&
                    !episode.HistoryEpisodeAvailableSoftSubs.Contains(s, StringComparer.OrdinalIgnoreCase)))
                { show.WaitingForLanguage++; continue; }
                var result = _queue.AddToQueue(new EpisodeInfo
                {
                    Id = episode.EpisodeId!, Title = episode.EpisodeTitle ?? "Episode", Episode = episode.Episode,
                    SeriesId = series.SeriesId, SeriesTitle = series.SeriesTitle ?? subscription.Title,
                    SeasonId = season.SeasonId, SeasonTitle = season.SeasonTitle,
                    SeasonNumber = int.TryParse(season.SeasonNum, out var sn) ? sn : 0,
                    EpisodeNumber = int.TryParse(episode.Episode, out var en) ? en : 0,
                    AudioLocale = dubs[0], Locale = _config.History.Lang,
                    SelectedDubs = dubs, SelectedSubs = subs,
                    ThumbnailUrl = episode.ThumbnailImageUrl, CoverArtUrl = series.ThumbnailImageUrl
                });
                // AddToQueue handles exact episode/season identity and persists its queue.
                if (result.Added) show.Added++; else show.AlreadyQueued++;
                // Only a durable queue entry may mark the episode handled; otherwise a
                // restart before it finishes would lose it and later checks would skip it.
                if (!result.Persisted)
                {
                    show.Error = $"The download queue could not be saved; {subscription.Title} will be re-checked on the next run.";
                    continue;
                }
                subscription.HandledEpisodeIds.Add(episode.EpisodeId!);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { show.Error = ex.Message; }
        return show;
    }
}

public sealed class SchedulerState
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public DateTimeOffset? LastRun { get; set; }
    public string? LastError { get; set; }
    public int LastQueuedCount { get; set; }
    public SchedulerRunSummary? LastRunSummary { get; set; }
    public List<SeriesSubscription> Subscriptions { get; set; } = [];
}

public sealed class SchedulerRunSummary
{
    public bool Backfill { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedAt { get; set; }
    public int Added => Shows.Sum(s => s.Added);
    public int Failed => Shows.Count(s => s.Error != null);
    public List<SubscriptionRunResult> Shows { get; set; } = [];
}

// Per-series outcome of one check. Every released, non-excluded episode lands in exactly one
// count, so "0 added" always comes with the reason.
public sealed class SubscriptionRunResult
{
    public string SeriesId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Added { get; set; }               // queued, or requested from Sonarr
    public int AlreadyQueued { get; set; }       // already waiting in the queue
    public int HaveFile { get; set; }            // local download or Sonarr file exists
    public int MissingFromBaseline { get; set; } // no file, but released before subscribing
    public int WaitingForLanguage { get; set; }  // selected dub/subtitles not available yet
    public int NotReleased { get; set; }
    public int Excluded { get; set; }            // specials off or unmonitored in Sonarr
    public string? Error { get; set; }
}

public sealed class SeriesSubscription
{
    public string SeriesId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public HashSet<string> HandledEpisodeIds { get; set; } = [];
}
