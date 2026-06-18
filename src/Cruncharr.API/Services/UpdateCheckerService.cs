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

    public string? LatestVersion { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public UpdateCheckerService(ILogger<UpdateCheckerService>? logger, IHttpClientFactory httpClientFactory, CruncharrConfig config)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _config = config;
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
        public string? TagName { get; set; }
    }
}
