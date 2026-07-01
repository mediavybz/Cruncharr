using System.Reflection;
using Cruncharr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

public class UpdateCheckerService : BackgroundService
{
    private readonly ILogger<UpdateCheckerService>? _logger;
    private readonly HttpClient _httpClient;
    private readonly CruncharrConfig _config;
    private readonly Cruncharr.Core.Services.INotificationService _notification;

    public string? LatestVersion { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public UpdateCheckerService(ILogger<UpdateCheckerService>? logger, IHttpClientFactory httpClientFactory, CruncharrConfig config, Cruncharr.Core.Services.INotificationService notification)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _config = config;
        _notification = notification;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.Notifications?.NotifyUpdateAvailable != true)
        {
            _logger?.LogInformation("Update checker disabled (NotifyUpdateAvailable=false)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Update check failed");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        // Check THIS project's releases, not the upstream Crunchy-Downloader repo.
        // (Upstream versions are 3.x while Cruncharr is 0.2.x, so comparing against
        // upstream made every check report a bogus "update available" pointing at a
        // different product.)
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/mediavybz/Cruncharr/releases/latest");
        request.Headers.Add("User-Agent", "Cruncharr-UpdateChecker/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonConvert.DeserializeObject<GitHubRelease>(json);
        if (release?.TagName == null) return;

        var currentVersionStr = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";
        var latestVersionStr = release.TagName.TrimStart('v');
        LatestVersion = release.TagName;

        if (IsNewerVersion(latestVersionStr, currentVersionStr))
        {
            UpdateAvailable = true;
            _logger?.LogInformation("Update available: {LatestVersion} (current: {CurrentVersion})", release.TagName, currentVersionStr);
            try { await _notification.NotifyUpdateAvailableAsync(currentVersionStr, latestVersionStr, _config); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Update-available notification failed"); }
        }
        else
        {
            UpdateAvailable = false;
            _logger?.LogDebug("No update available. Latest: {LatestVersion}, Current: {CurrentVersion}", release.TagName, currentVersionStr);
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        var l = ParseSemver(latest);
        var c = ParseSemver(current);

        for (int i = 0; i < 3; i++)
        {
            if (l[i] != c[i]) return l[i] > c[i];
        }

        if (l[3] == null && c[3] != null) return true;
        if (l[3] != null && c[3] == null) return false;
        if (l[3] != null && c[3] != null) return l[3] > c[3];

        return false;
    }

    private static int?[] ParseSemver(string version)
    {
        version = version.TrimStart('v', 'V');
        // InformationalVersion carries "+<commit>" build metadata (e.g. "1.0.20+abc123");
        // strip it or the patch segment fails to parse as 0 and every release looks newer.
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0) version = version.Substring(0, plusIndex);
        int? prereleaseNum = null;
        var dashIndex = version.IndexOf('-');
        if (dashIndex > 0)
        {
            var prerelease = version.Substring(dashIndex + 1);
            var match = System.Text.RegularExpressions.Regex.Match(prerelease, @"\d+");
            if (match.Success) prereleaseNum = int.Parse(match.Value);
            version = version.Substring(0, dashIndex);
        }

        var parts = version.Split('.');
        var result = new int?[4];
        for (int i = 0; i < 3; i++)
            result[i] = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        result[3] = prereleaseNum;
        return result;
    }

    private class GitHubRelease
    {
        // GitHub's API returns snake_case; without the explicit mapping TagName
        // deserializes to null and the update check silently never fires.
        [JsonProperty("tag_name")]
        public string? TagName { get; set; }
    }
}
