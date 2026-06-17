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

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        var latestVersion = ParseVersion(release.TagName.TrimStart('v'));
        LatestVersion = release.TagName;

        if (latestVersion > currentVersion)
        {
            UpdateAvailable = true;
            _logger?.LogInformation("Update available: {LatestVersion} (current: {CurrentVersion})", release.TagName, currentVersion);
        }
        else
        {
            UpdateAvailable = false;
            _logger?.LogDebug("No update available. Latest: {LatestVersion}, Current: {CurrentVersion}", release.TagName, currentVersion);
        }
    }

    private static Version ParseVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return new Version(0, 0, 0, 0);

        // Remove 'v' prefix if present
        version = version.TrimStart('v', 'V');

        // Strip prerelease suffix (-beta.1, -rc.2, etc.)
        var prereleaseIndex = version.IndexOf('-');
        if (prereleaseIndex > 0)
        {
            version = version.Substring(0, prereleaseIndex);
        }

        if (Version.TryParse(version, out var v)) return v;
        return new Version(0, 0, 0, 0);
    }

    private class GitHubRelease
    {
        public string? TagName { get; set; }
    }
}
