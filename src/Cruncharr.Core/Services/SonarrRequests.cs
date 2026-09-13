using System.Text;
using Cruncharr.Core.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Services;

public record SonarrQualityProfile(int Id, string Name);
public record SonarrRootFolder(int Id, string Path);
public record SonarrSetupOptions(List<SonarrQualityProfile> QualityProfiles, List<SonarrRootFolder> RootFolders);

public partial class SonarrService
{
    public void InvalidateCache()
    {
        lock (_metadataCacheLock)
        {
            _seriesCache = null;
            _episodeListCache.Clear();
            _episodeCache.Clear();
            _fileStatusFailure = null;
        }
    }

    private async Task<JToken> RequestJsonAsync(HttpMethod method, string resource, SonarrConfig config,
        object? body = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, $"{BuildBaseUrl(config)}/{resource}");
        request.Headers.Add("X-Api-Key", config.ApiKey);
        if (body != null)
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        // Mutations are not blindly retried. A timed-out series add is reconciled by TVDB ID.
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Sonarr {method} {resource.Split('?')[0]} returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(timeout.Token);
        return string.IsNullOrWhiteSpace(content) ? new JObject() : JToken.Parse(content);
    }

    public async Task<SonarrSetupOptions> GetSetupOptionsAsync(SonarrConfig config, CancellationToken cancellationToken = default)
    {
        var profiles = await RequestJsonAsync(HttpMethod.Get, "qualityprofile", config, cancellationToken: cancellationToken);
        var folders = await RequestJsonAsync(HttpMethod.Get, "rootfolder", config, cancellationToken: cancellationToken);
        return new SonarrSetupOptions(
            profiles.Select(p => new SonarrQualityProfile((int)p["id"]!, (string?)p["name"] ?? "")).ToList(),
            folders.Select(p => new SonarrRootFolder((int)p["id"]!, (string?)p["path"] ?? "")).ToList());
    }

    public async Task<SonarrSeries> AddSeriesAsync(int tvdbId, SonarrConfig config, CancellationToken cancellationToken = default)
    {
        // Check the actual server before creating anything, including after a previous timeout.
        var libraryJson = await RequestJsonAsync(HttpMethod.Get, "series", config, cancellationToken: cancellationToken);
        var existing = libraryJson.FirstOrDefault(s => (int?)s["tvdbId"] == tvdbId);
        if (existing != null) return existing.ToObject<SonarrSeries>()!;
        var lookup = await RequestJsonAsync(HttpMethod.Get, $"series/lookup?term=tvdb:{tvdbId}", config, cancellationToken: cancellationToken);
        var selected = lookup.FirstOrDefault(s => (int?)s["tvdbId"] == tvdbId) as JObject
            ?? throw new InvalidOperationException("Sonarr could not find the selected TVDB series.");
        var options = await GetSetupOptionsAsync(config, cancellationToken);
        var profileId = config.QualityProfileId;
        if (profileId == 0)
            profileId = libraryJson.GroupBy(s => (int?)s["qualityProfileId"] ?? 0)
                .OrderByDescending(g => g.Count()).Select(g => g.Key)
                .FirstOrDefault(id => options.QualityProfiles.Any(p => p.Id == id));
        if (profileId == 0 && options.QualityProfiles.Count == 1) profileId = options.QualityProfiles[0].Id;
        if (!options.QualityProfiles.Any(p => p.Id == profileId))
            throw new InvalidOperationException("Choose a Sonarr quality profile in Settings / Sonarr.");
        var root = config.RootFolderPath;
        if (string.IsNullOrWhiteSpace(root))
            root = options.RootFolders.OrderByDescending(folder => libraryJson.Count(s =>
                    ((string?)s["path"] ?? "").Replace('\\', '/').StartsWith(folder.Path.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                .ThenBy(folder => folder.Id).FirstOrDefault()?.Path ?? "";
        if (!options.RootFolders.Any(f => f.Path == root))
            throw new InvalidOperationException("Choose a Sonarr root folder in Settings / Sonarr.");
        var payload = (JObject)selected.DeepClone();
        payload.Remove("id");
        payload.Remove("path");
        payload["rootFolderPath"] = root;
        payload["qualityProfileId"] = profileId;
        payload["seasonFolder"] = true;
        payload["monitored"] = false;
        payload["monitorNewItems"] = "none";
        payload["addOptions"] = JObject.FromObject(new { monitor = "none", searchForMissingEpisodes = false, searchForCutoffUnmetEpisodes = false });
        foreach (var season in payload["seasons"] ?? new JArray()) season["monitored"] = false;
        try
        {
            var added = await RequestJsonAsync(HttpMethod.Post, "series", config, payload, cancellationToken);
            InvalidateCache();
            return added.ToObject<SonarrSeries>()!;
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            var refreshed = await RequestJsonAsync(HttpMethod.Get, "series", config, cancellationToken: cancellationToken);
            var reconciled = refreshed.FirstOrDefault(s => (int?)s["tvdbId"] == tvdbId);
            if (reconciled == null) throw;
            InvalidateCache();
            return reconciled.ToObject<SonarrSeries>()!;
        }
    }

    public async Task SetMonitoringAsync(int seriesId, bool monitored, SonarrConfig config, CancellationToken cancellationToken = default)
    {
        var series = await RequestJsonAsync(HttpMethod.Get, $"series/{seriesId}", config, cancellationToken: cancellationToken);
        if ((bool?)series["monitored"] == monitored) return;
        // Preserve the server resource, including profiles, tags and season preferences.
        series["monitored"] = monitored;
        await RequestJsonAsync(HttpMethod.Put, $"series/{seriesId}", config, series, cancellationToken);
        InvalidateCache();
    }

    public async Task<int> SearchEpisodesAsync(int seriesId, IReadOnlyList<int> episodeIds, SonarrConfig config, CancellationToken cancellationToken = default)
    {
        if (episodeIds.Count == 0) throw new ArgumentException("At least one episode is required.", nameof(episodeIds));
        await SetMonitoringAsync(seriesId, true, config, cancellationToken);
        await RequestJsonAsync(HttpMethod.Put, "episode/monitor", config, new { episodeIds, monitored = true }, cancellationToken);
        // Reconcile a retry after Sonarr accepted a command but Cruncharr lost its response.
        var recent = await RequestJsonAsync(HttpMethod.Get, "command", config, cancellationToken: cancellationToken);
        var existing = recent.FirstOrDefault(c => (string?)c["name"] == "EpisodeSearch" &&
            c["body"]?["episodeIds"] is JArray ids && ids.Values<int>().Order().SequenceEqual(episodeIds.Order()) &&
            (string?)c["status"] != "failed" && DateTime.TryParse((string?)c["queued"], out var queued) &&
            DateTime.UtcNow - queued.ToUniversalTime() < TimeSpan.FromMinutes(30));
        if (existing != null) return (int)existing["id"]!;
        var command = await RequestJsonAsync(HttpMethod.Post, "command", config, new { name = "EpisodeSearch", episodeIds }, cancellationToken);
        return (int)command["id"]!;
    }

    public async Task<int> ImportFileAsync(string path, int seriesId, SonarrConfig config, CancellationToken cancellationToken = default, int? episodeId = null)
    {
        var series = await RequestJsonAsync(HttpMethod.Get, $"series/{seriesId}", config, cancellationToken: cancellationToken);
        var seriesPath = ((string?)series["path"] ?? "").Replace('\\', '/').TrimEnd('/') + "/";
        var inLibrary = seriesPath != "/" && path.Replace('\\', '/').StartsWith(seriesPath, StringComparison.Ordinal);
        object body = inLibrary
            ? new { name = "RescanSeries", seriesId }
            : new { name = "DownloadedEpisodesScan", path, importMode = "Copy" };
        if (!inLibrary && episodeId.HasValue)
        {
            var episodes = await GetCurrentEpisodesAsync(seriesId, config, true, cancellationToken);
            var episode = episodes.SingleOrDefault(e => e.Id == episodeId.Value)
                ?? throw new InvalidOperationException("The confirmed episode is no longer in Sonarr.");
            var files = await RequestJsonAsync(HttpMethod.Get, $"manualimport?folder={Uri.EscapeDataString(path)}&filterExistingFiles=false",
                config, cancellationToken: cancellationToken);
            var file = files.SingleOrDefault(f => (string?)f["path"] == path) as JObject
                ?? throw new InvalidOperationException("Sonarr cannot read the completed video. Check its download folder mapping.");
            file["seriesId"] = seriesId;
            file["episodeIds"] = new JArray(episodeId.Value);
            file["seasonNumber"] = episode.SeasonNumber;
            // Reprocess with the confirmed identity. Respect Sonarr's file/quality rejections.
            var checkedFiles = await RequestJsonAsync(HttpMethod.Post, "manualimport", config, new JArray(file), cancellationToken);
            var checkedFile = checkedFiles.Single();
            var rejections = checkedFile["rejections"]?.Select(r => (string?)r["reason"]).Where(r => !string.IsNullOrEmpty(r)).ToList() ?? [];
            if (rejections.Count > 0) throw new InvalidOperationException("Sonarr import: " + string.Join("; ", rejections));
            body = new { name = "ManualImport", importMode = "Copy", files = new[] { new {
                path, seriesId, episodeIds = new[] { episodeId.Value }, quality = checkedFile["quality"], languages = checkedFile["languages"],
                releaseGroup = checkedFile["releaseGroup"], indexerFlags = checkedFile["indexerFlags"], releaseType = checkedFile["releaseType"]
            } } };
        }
        var command = await RequestJsonAsync(HttpMethod.Post, "command", config, body, cancellationToken);
        return (int)command["id"]!;
    }

    public async Task<string> GetCommandStatusAsync(int commandId, SonarrConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await RequestJsonAsync(HttpMethod.Get, $"command/{commandId}", config, cancellationToken: cancellationToken);
            return (string?)command["status"] ?? "unknown";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return "missing";
        }
    }
}
