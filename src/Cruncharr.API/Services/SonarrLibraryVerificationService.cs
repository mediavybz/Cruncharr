using System.Security.Cryptography;
using System.Text;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

public record LibraryTitleFailure(string SeriesId, string Title);
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
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class State
    {
        public string Scope { get; set; } = "";
        public Dictionary<string, Match> Matches { get; set; } = [];
        [JsonIgnore] public HashSet<string> Pending { get; } = [];
        [JsonIgnore] public List<LibraryTitleFailure> Failures { get; } = [];
        [JsonIgnore] public bool Unavailable { get; set; }
        [JsonIgnore] public DateTime CompletedUtc { get; set; }
        [JsonIgnore] public Task? Work { get; set; }
    }

    public async Task<LibraryVerificationSnapshot> GetAsync(List<SonarrSeries> library, SonarrConfig config,
        bool startVerification, bool waitForCompletion, CancellationToken cancellationToken)
    {
        // A changed server, credential, identity or alias invalidates prior assumptions, including
        // newly added remakes which make an earlier title ambiguous. File counts stay live.
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            config.Host, config.Port, config.UrlBase, config.UseSsl, config.ApiKey,
            Series = library.OrderBy(s => s.Id).Select(s => new
            {
                s.Id, s.TvdbId, Titles = SonarrTitleIndex.Titles(s).Order(StringComparer.Ordinal).ToArray()
            })
        }))));
        State state;
        Task? work;
        lock (_gate)
        {
            if (_state?.Scope != scope) _state = Load(scope);
            state = _state;
            foreach (var id in state.Matches.Where(p => p.Value.ExpiresUtc <= DateTime.UtcNow).Select(p => p.Key).ToArray())
                state.Matches.Remove(id);
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

    private State Load(string scope)
    {
        try
        {
            if (cachePath != null && File.Exists(cachePath) &&
                JsonConvert.DeserializeObject<State>(File.ReadAllText(cachePath)) is { } saved &&
                saved.Scope == scope && saved.Matches != null && saved.Matches.Values.All(m => m != null))
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { logger?.LogWarning(ex, "Sonarr library identity cache will be rebuilt"); }
        return new State { Scope = scope };
    }

    private async Task VerifyAsync(State state, List<SonarrSeries> library, SonarrConfig config)
    {
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
                        state.Matches[entry.Id] = new Match { Title = entry.Title, SonarrId = exact.Id, ExpiresUtc = DateTime.UtcNow.AddDays(1) };
                    else if (!state.Matches.ContainsKey(entry.Id) && entry.EpisodeCount != 0 && index.Candidates(entry.Title).Count > 0)
                    {
                        candidates.Add(entry);
                        state.Pending.Add(entry.Id);
                    }
                }
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
                        { Title = entry.Title!, SonarrId = match.Id, ExpiresUtc = DateTime.UtcNow.AddDays(1) };
                        state.Pending.Remove(entry.Id!);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        state.Unavailable = true;
                        state.Failures.Add(new(entry.Id!, entry.Title!));
                    }
                    logger?.LogWarning(ex, "Could not verify Sonarr identity for {Title}", entry.Title);
                }
            }
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
                if (cachePath != null && ReferenceEquals(state, _state))
                {
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
        }
    }
}
