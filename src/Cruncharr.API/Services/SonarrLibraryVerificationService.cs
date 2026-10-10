using System.Security.Cryptography;
using System.Text;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

public record LibraryTitleFailure(string SeriesId, string Title, string? Reason = null);
public record LibraryVerificationSnapshot(Dictionary<string, int> Matches, string[] PendingSeriesIds,
    bool InProgress, bool Unavailable, LibraryTitleFailure[] Failures);

// Verification belongs to the application, not the lifetime of a browser request.
public sealed class SonarrLibraryVerificationService(
    ISonarrService sonarr, ICrunchyrollApiService api,
    ILogger<SonarrLibraryVerificationService>? logger = null, string? cachePath = null,
    CancellationToken stoppingToken = default)
{
    private readonly object _gate = new();
    private State? _state;

    private sealed class Match
    {
        public string Title { get; set; } = "";
        public int SonarrId { get; set; }
        public int TvdbId { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class State
    {
        public string Scope { get; set; } = "";
        // Connection half of Scope; a library-only change carries verified identities over.
        public string ConnectionScope { get; set; } = "";
        public Dictionary<string, Match> Matches { get; set; } = [];
        [JsonIgnore] public HashSet<string> Pending { get; } = [];
        [JsonIgnore] public List<LibraryTitleFailure> Failures { get; } = [];
        [JsonIgnore] public bool Unavailable { get; set; }
        [JsonIgnore] public DateTime CompletedUtc { get; set; }
        [JsonIgnore] public Task? Work { get; set; }
    }

    private static string Hash(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value))));

    public async Task<LibraryVerificationSnapshot> GetAsync(List<SonarrSeries> library, SonarrConfig config,
        bool startVerification, bool waitForCompletion, CancellationToken cancellationToken)
    {
        // A changed server or credential invalidates prior assumptions outright. A changed
        // library (an added remake that makes a title ambiguous, a refreshed alias list) keeps
        // identities whose Sonarr series still exists but marks them for re-verification, so
        // hundreds of verified badges do not vanish for the minutes the re-check takes.
        // File counts stay live.
        var connectionScope = Hash(new { config.Host, config.Port, config.UrlBase, config.UseSsl, config.ApiKey });
        var scope = Hash(new
        {
            Connection = connectionScope,
            Series = library.OrderBy(s => s.Id).Select(s => new
            {
                s.Id, s.TvdbId, Titles = SonarrTitleIndex.Titles(s).Order(StringComparer.Ordinal).ToArray()
            })
        });
        State state;
        Task? work;
        lock (_gate)
        {
            if (_state?.Scope != scope) _state = Load(scope, connectionScope, library, _state);
            state = _state;
            // Expired matches stay visible; VerifyAsync re-checks them and removes only rejections.
            if (startVerification && (state.Work == null || state.Work.IsCompleted) &&
                DateTime.UtcNow - state.CompletedUtc >= TimeSpan.FromMinutes(5))
            {
                var settings = JsonConvert.DeserializeObject<SonarrConfig>(JsonConvert.SerializeObject(config))!;
                state.Work = Task.Run(() => VerifyAsync(state, library, settings), stoppingToken);
            }
            work = state.Work;
        }
        if (waitForCompletion && work != null) await work.WaitAsync(cancellationToken);
        lock (_gate)
            return new(state.Matches.ToDictionary(p => p.Key, p => p.Value.SonarrId), state.Pending.ToArray(),
                state.Work is { IsCompleted: false }, state.Unavailable, state.Failures.ToArray());
    }

    private State Load(string scope, string connectionScope, List<SonarrSeries> library, State? previous)
    {
        State? saved = previous;
        try
        {
            if (saved == null && cachePath != null && File.Exists(cachePath))
            {
                var fromDisk = JsonConvert.DeserializeObject<State>(File.ReadAllText(cachePath));
                if (fromDisk is { Matches: not null } && fromDisk.Matches.Values.All(m => m != null))
                    saved = fromDisk;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { logger?.LogWarning(ex, "Sonarr library identity cache will be rebuilt"); }

        if (saved?.Scope == scope) return saved;

        var state = new State { Scope = scope, ConnectionScope = connectionScope };
        if (saved == null) return state;
        if (saved.ConnectionScope != connectionScope)
        {
            logger?.LogInformation("Sonarr connection changed; verified library identities will be rebuilt");
            return state;
        }

        var bySonarrId = library.ToDictionary(s => s.Id);
        foreach (var (id, match) in saved.Matches)
        {
            if (!bySonarrId.TryGetValue(match.SonarrId, out var series)) continue;
            if (match.TvdbId > 0 && series.TvdbId != match.TvdbId) continue;
            state.Matches[id] = new Match { Title = match.Title, SonarrId = match.SonarrId, TvdbId = series.TvdbId, ExpiresUtc = DateTime.MinValue };
            state.Pending.Add(id);
        }
        logger?.LogInformation("Sonarr library changed; {Kept} of {Total} verified identities carried over pending re-verification",
            state.Matches.Count, saved.Matches.Count);
        return state;
    }

    private async Task VerifyAsync(State state, List<SonarrSeries> library, SonarrConfig config)
    {
        var started = DateTime.UtcNow;
        var exactCount = 0;
        var resolved = 0;
        try
        {
            var catalog = await api.GetAllSeriesAsync(cancellationToken: stoppingToken);
            if (catalog.Count == 0) throw new HttpRequestException("Catalog metadata is unavailable.");
            var index = new SonarrTitleIndex(library);
            var candidates = new List<Cruncharr.Core.Models.SeriesInfo>();
            lock (_gate)
            {
                state.Pending.Clear();
                state.Failures.Clear();
                state.Unavailable = false;
                var current = catalog.Where(s => !string.IsNullOrEmpty(s.Id)).ToDictionary(s => s.Id!);
                foreach (var id in state.Matches.Keys.ToArray())
                    if (!current.TryGetValue(id, out var entry) || entry.Title != state.Matches[id].Title)
                        state.Matches.Remove(id);
                foreach (var entry in catalog)
                {
                    if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrEmpty(entry.Id)) continue;
                    if (index.FindExact(entry.Title) is { } exact)
                    {
                        state.Matches[entry.Id] = new Match { Title = entry.Title, SonarrId = exact.Id, TvdbId = exact.TvdbId, ExpiresUtc = DateTime.UtcNow.AddDays(1) };
                        exactCount++;
                    }
                    else if ((!state.Matches.TryGetValue(entry.Id, out var saved) || saved.ExpiresUtc <= DateTime.UtcNow)
                             && entry.EpisodeCount != 0 && index.Candidates(entry.Title).Count > 0)
                    {
                        candidates.Add(entry);
                        state.Pending.Add(entry.Id);
                    }
                }
                // Re-verify carried-over and expired identities first so stale badges clear quickly.
                candidates = candidates.OrderByDescending(entry => state.Matches.ContainsKey(entry.Id!)).ToList();
                logger?.LogInformation("Sonarr library verification: {Exact} exact titles, {Candidates} to verify by episodes ({Kept} previously verified)",
                    exactCount, candidates.Count, candidates.Count(entry => state.Matches.ContainsKey(entry.Id!)));
            }
            foreach (var entry in candidates)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    var match = await sonarr.ResolveSeriesAsync(entry.Id!, entry.Title!, config, stoppingToken);
                    lock (_gate)
                    {
                        if (match != null) state.Matches[entry.Id!] = new Match
                        { Title = entry.Title!, SonarrId = match.Id, TvdbId = match.TvdbId, ExpiresUtc = DateTime.UtcNow.AddDays(1) };
                        else state.Matches.Remove(entry.Id!); // a verified rejection, not an outage
                        state.Pending.Remove(entry.Id!);
                        if (++resolved % 25 == 0) Save(state);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        state.Unavailable = true;
                        state.Failures.Add(new(entry.Id!, entry.Title!, ex.Message));
                    }
                    logger?.LogWarning("Could not verify Sonarr identity for {Title}: {Reason}", entry.Title, ex.Message);
                }
            }
            logger?.LogInformation("Sonarr library verification finished: {Resolved} verified by episodes in {Seconds:F0}s, {Failures} could not be verified",
                resolved, (DateTime.UtcNow - started).TotalSeconds, state.Failures.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate) state.Unavailable = true;
            logger?.LogWarning(ex, "Could not finish verifying catalog titles against Sonarr");
        }
        finally
        {
            lock (_gate)
            {
                state.CompletedUtc = DateTime.UtcNow;
                Save(state);
            }
        }
    }

    // Caller holds _gate.
    private void Save(State state)
    {
        if (cachePath == null || !ReferenceEquals(state, _state)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            File.WriteAllText(cachePath + ".tmp", JsonConvert.SerializeObject(state));
            File.Move(cachePath + ".tmp", cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger?.LogWarning(ex, "Could not save verified Sonarr library identities"); }
    }
}
