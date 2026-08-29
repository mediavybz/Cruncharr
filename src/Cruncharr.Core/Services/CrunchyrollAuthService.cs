using System.Net;
using System.Net.Http.Headers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;

namespace Cruncharr.Core.Services;

public interface ICrunchyrollAuthService
{
    CrToken? Token { get; }
    CrProfile Profile { get; }
    CrMultiProfile MultiProfile { get; }
    Subscription? Subscription { get; }
    bool IsAuthenticated { get; }
    HttpClientWrapper HttpClient { get; }
    CrAuthSettings AuthSettings { get; }
    CrAuthSettings StreamEndpoint { get; }
    CrAuthSettings StreamEndpointSecondary { get; }
    CrunchyrollEndpoints EndpointEnum { get; }
    Task<bool> AuthenticateAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> LoginWithTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task LogoutAsync();
    Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default, bool force = false);
    Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, string? pin = null, CancellationToken cancellationToken = default);
    string? LastProfileSwitchError { get; }
    Task AuthAnonymousAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task AuthAnonymousFoxyAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> CheckStreamEndpointUpdateAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateAuthCredentialsAsync(CancellationToken cancellationToken = default);
    void Init();
    void LoadToken();
    void SaveToken();
    void DeleteToken();
    Task<string> GetBase64EncodedTokenAsync(CancellationToken cancellationToken = default);
}

public class CrunchyrollAuthService : ICrunchyrollAuthService
{
    private readonly ILogger<CrunchyrollAuthService>? _logger;
    // Multi-profile is fetched on every login/refresh; when the account/region doesn't expose it
    // (often 403) that warning would repeat dozens of times. Log it once, then stay quiet.
    private bool _multiProfileUnavailableLogged;
    private readonly HttpClientWrapper _httpClient;
    private readonly CrAuthSettings _authSettings;
    private readonly string _tokenFilePath;
    private readonly CruncharrConfig? _config;
    private readonly INotificationService? _notification;
    private readonly SemaphoreSlim _refreshTokenGate = new(1, 1);
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(60);

    public CrToken? Token { get; private set; }
    public CrProfile Profile { get; private set; } = new();
    public CrMultiProfile MultiProfile { get; private set; } = new();
    public Subscription? Subscription { get; private set; }
    public bool IsAuthenticated => Token?.access_token != null && Profile.Username != "???";
    public HttpClientWrapper HttpClient => _httpClient;
    public CrAuthSettings AuthSettings => _authSettings;
    public CrAuthSettings StreamEndpoint { get; private set; }
    public CrAuthSettings StreamEndpointSecondary { get; private set; }
    public CrunchyrollEndpoints EndpointEnum { get; set; } = CrunchyrollEndpoints.Unknown;

    private static readonly CrAuthSettings DefaultAndroidTvAuthSettings = new()
    {
        Endpoint = "tv/android_tv",
        // Fresh TV client (ANDROIDTV 3.66.0) - from upstream CRD Codeberg data.json, verified active
        // and password-grant capable. Crunchyroll deactivated the older 3.59/3.61/3.65 TV clients.
        Authorization = "Basic bGFzcnF6eGJlbXZvcWlveTU2bTA6ZHlodDVSWVYyXzIyUm4xaWF0X29YV0c2ejBUWUswazE=",
        UserAgent = "ANDROIDTV/3.66.0_22348 Android/16",
        Device_name = "Android TV",
        Device_type = "Android TV",
        Video = true,
        Audio = true
    };

    private static readonly CrAuthSettings DefaultAndroidAuthSettings = new()
    {
        Endpoint = "android/phone",
        // Fresh mobile client (Crunchyroll 3.110.0) - active (SSO flow). Used as fallback if
        // the TV password-grant client is ever deactivated.
        Authorization = "Basic YnFjaGljMmc3aTJzcnQ5cXU1c2I6NkVKT0tQLXNxU3hEb3RXdVgwZmVnV3pNX2FiTWRNWUo=",
        UserAgent = "Crunchyroll/3.110.0 Android/16 okhttp/4.12.0",
        Device_name = "CPH2449",
        Device_type = "OnePlus CPH2449",
        Video = true,
        Audio = true
    };

    // Alternate android client (upstream's), used as an automatic fallback if the primary
    // android client above is deactivated by Crunchyroll. Both are currently active. Add more
    // here as they're discovered; LoginAsync tries each on a client failure.
    private static readonly CrAuthSettings AlternateAndroidAuthSettings = new()
    {
        Endpoint = "android/phone",
        Authorization = "Basic bzJhNndsamdub3FtdjloMWJ5bHI6Ujk3S3ExZm5faExZVFk0bDJxTjJIT2lDQnpfYnpBSUU=",
        UserAgent = "Crunchyroll/3.97.0 Android/16 okhttp/4.12.0",
        Device_name = "CPH2449",
        Device_type = "OnePlus CPH2449",
        Video = true,
        Audio = true
    };

    // Maintained fallback client from crunchy-cli / crunchyroll-rs (client_id t-kdgp2h8c3jub8fn0fq),
    // kept fresh by an active upstream (our original upstream, Crunchy-Downloader, was removed).
    // Verified live + password-grant capable on beta-api AND www (2026-06-19). Tried ONLY as a last
    // resort, when BOTH the TV and mobile clients above are deactivated. Play endpoint set to the
    // proven android/phone path as a best guess — the client's native play device is unverified, so
    // if downloads ever 40016 once this activates, adjust Endpoint.
    private static readonly CrAuthSettings MaintainedFallbackAuthSettings = new()
    {
        Endpoint = "android/phone",
        Authorization = "Basic dC1rZGdwMmg4YzNqdWI4Zm4wZnE6eWZMRGZNZnJZdktYaDRKWFMxTEVJMmNDcXUxdjVXYW4=",
        UserAgent = "Crunchyroll/3.110.0 Android/16 okhttp/4.12.0",
        Device_name = "CPH2449",
        Device_type = "OnePlus CPH2449",
        Video = true,
        Audio = true
    };

    private const string EmbeddedAuthData = @"[{""type"":""tv"",""Authorization"":""Basic bGFzcnF6eGJlbXZvcWlveTU2bTA6ZHlodDVSWVYyXzIyUm4xaWF0X29YV0c2ejBUWUswazE="",""version_name"":""3.66.0"",""version_code"":""22348""},{""type"":""mobile"",""Authorization"":""Basic YnFjaGljMmc3aTJzcnQ5cXU1c2I6NkVKT0tQLXNxU3hEb3RXdVgwZmVnV3pNX2FiTWRNWUo="",""version_name"":""3.110.0"",""version_code"":""101143""}]";

    public CrunchyrollAuthService(CruncharrConfig? config = null, ILogger<CrunchyrollAuthService>? logger = null, INotificationService? notification = null)
    {
        _logger = logger;
        _notification = notification;
        _httpClient = new HttpClientWrapper(config);
        _authSettings = new CrAuthSettings();
        _config = config;

        var streamEndpointConfig = config?.Crunchyroll?.StreamEndpoint;
        var streamEndpointSecondaryConfig = config?.Crunchyroll?.StreamEndpointSecondary;

        StreamEndpoint = new CrAuthSettings();
        StreamEndpointSecondary = new CrAuthSettings();

        if (streamEndpointConfig != null)
        {
            StreamEndpoint.Endpoint = !string.IsNullOrEmpty(streamEndpointConfig.Endpoint) ? streamEndpointConfig.Endpoint : DefaultAndroidTvAuthSettings.Endpoint;
            StreamEndpoint.Authorization = !string.IsNullOrEmpty(streamEndpointConfig.Authorization) ? streamEndpointConfig.Authorization : DefaultAndroidTvAuthSettings.Authorization;
            StreamEndpoint.UserAgent = !string.IsNullOrEmpty(streamEndpointConfig.UserAgent) ? streamEndpointConfig.UserAgent : DefaultAndroidTvAuthSettings.UserAgent;
            StreamEndpoint.Device_type = !string.IsNullOrEmpty(streamEndpointConfig.DeviceType) ? streamEndpointConfig.DeviceType : DefaultAndroidTvAuthSettings.Device_type;
            StreamEndpoint.Device_name = !string.IsNullOrEmpty(streamEndpointConfig.DeviceName) ? streamEndpointConfig.DeviceName : DefaultAndroidTvAuthSettings.Device_name;
            StreamEndpoint.Video = streamEndpointConfig.Video;
            StreamEndpoint.Audio = streamEndpointConfig.Audio;
            StreamEndpoint.UseDefault = streamEndpointConfig.UseDefault;
        }
        else
        {
            StreamEndpoint.Authorization = DefaultAndroidTvAuthSettings.Authorization;
            StreamEndpoint.UserAgent = DefaultAndroidTvAuthSettings.UserAgent;
            StreamEndpoint.Device_name = DefaultAndroidTvAuthSettings.Device_name;
            StreamEndpoint.Device_type = DefaultAndroidTvAuthSettings.Device_type;
            StreamEndpoint.Endpoint = DefaultAndroidTvAuthSettings.Endpoint;
            StreamEndpoint.Video = true;
            StreamEndpoint.Audio = true;
        }

        if (streamEndpointSecondaryConfig != null)
        {
            StreamEndpointSecondary.Endpoint = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.Endpoint) ? streamEndpointSecondaryConfig.Endpoint : DefaultAndroidAuthSettings.Endpoint;
            StreamEndpointSecondary.Authorization = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.Authorization) ? streamEndpointSecondaryConfig.Authorization : DefaultAndroidAuthSettings.Authorization;
            StreamEndpointSecondary.UserAgent = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.UserAgent) ? streamEndpointSecondaryConfig.UserAgent : DefaultAndroidAuthSettings.UserAgent;
            StreamEndpointSecondary.Device_type = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.DeviceType) ? streamEndpointSecondaryConfig.DeviceType : DefaultAndroidAuthSettings.Device_type;
            StreamEndpointSecondary.Device_name = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.DeviceName) ? streamEndpointSecondaryConfig.DeviceName : DefaultAndroidAuthSettings.Device_name;
            StreamEndpointSecondary.Video = streamEndpointSecondaryConfig.Video;
            StreamEndpointSecondary.Audio = streamEndpointSecondaryConfig.Audio;
            StreamEndpointSecondary.UseDefault = streamEndpointSecondaryConfig.UseDefault;
        }
        else
        {
            StreamEndpointSecondary.Authorization = DefaultAndroidAuthSettings.Authorization;
            StreamEndpointSecondary.UserAgent = DefaultAndroidAuthSettings.UserAgent;
            StreamEndpointSecondary.Device_name = DefaultAndroidAuthSettings.Device_name;
            StreamEndpointSecondary.Device_type = DefaultAndroidAuthSettings.Device_type;
            StreamEndpointSecondary.Endpoint = DefaultAndroidAuthSettings.Endpoint;
            StreamEndpointSecondary.Video = true;
            StreamEndpointSecondary.Audio = true;
        }

        // NOTE: the embedded TV client (DefaultAndroidTvAuthSettings) is the fresh, active
        // ANDROIDTV 3.65.0 client which supports the password grant, so the original TV flow
        // (password grant + tv/android_tv play URL) works directly - no client swap needed.
        // If Crunchyroll deactivates it later, LoginAsync falls back to the SSO/mobile flow
        // (and the alternate android client) automatically.

        _tokenFilePath = !string.IsNullOrEmpty(config?.TokenFilePath) ? config!.TokenFilePath : GetDefaultTokenPath();

        Init();
        LoadToken();

        // [PT] Lazy auth update - will be called on first token refresh instead of sync-over-async in constructor
    }

    private static string GetDefaultTokenPath()
    {
        // Use /config for Docker/container environments, fallback to AppData for desktop
        if (Directory.Exists("/config"))
        {
            return "/config/token.json";
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cruncharr", "token.json");
    }

    public void Init()
    {
        Profile = new CrProfile
        {
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "en-US",
            HasPremium = false,
        };
    }

    private string GetTokenFilePath()
    {
        switch (StreamEndpoint.Endpoint)
        {
            case "tv/samsung":
            case "tv/vidaa":
            case "tv/android_tv":
                return _tokenFilePath.Replace(".json", "_tv.json");
            case "android/phone":
            case "android/tablet":
                return _tokenFilePath.Replace(".json", "_android.json");
            case "console/switch":
            case "console/ps4":
            case "console/ps5":
            case "console/xbox_one":
                return _tokenFilePath.Replace(".json", "_console.json");
            case "---":
                return _tokenFilePath.Replace(".json", "_guest.json");
            default:
                return _tokenFilePath;
        }
    }

    public async Task<bool> AuthenticateAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        // Serialize with RefreshTokenAsync/LoginAsync: concurrent token loads used to refresh
        // with an already-rotated refresh_token, which Crunchyroll rejects with 400 and treats
        // as reuse - revoking the whole session family (the "flaky logout").
        await _refreshTokenGate.WaitAsync(cancellationToken);
        try
        {
            return await AuthenticateCoreAsync(useBetaApi, cancellationToken);
        }
        finally
        {
            _refreshTokenGate.Release();
        }
    }

    private async Task<bool> AuthenticateCoreAsync(bool useBetaApi, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Authenticating with Crunchyroll...");

        // Prefer the in-memory session: it is always at least as fresh as the token file
        // (every rotation saves both), while re-reading the file can pick up a refresh token
        // that has already been rotated and invalidated.
        if (Token?.refresh_token == null && File.Exists(GetTokenFilePath()))
        {
            var content = await File.ReadAllTextAsync(GetTokenFilePath());
            Token = JsonConvert.DeserializeObject<CrToken>(content);
        }

        if (Token?.refresh_token != null)
        {
            await LoginWithTokenAsync(useBetaApi, cancellationToken);
            return IsAuthenticated;
        }

        await AuthAnonymousAsync(useBetaApi, cancellationToken);
        return false;
    }

    public async Task AuthAnonymousAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        string uuid = string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;

        Subscription = new Subscription();

        var formData = new Dictionary<string, string>{
            { "grant_type", "client_id" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type },
        };

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var requestContent = new FormUrlEncodedContent(formData);

        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in crunchyAuthHeaders)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);

        if (isOk)
        {
            JsonTokenToFileAndVariable(content, uuid);
        }
        else
        {
            _logger?.LogError("Anonymous login failed: {Error}", error);
        }

        Profile = new CrProfile
        {
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "de-DE"
        };
    }

    // Alternative anonymous auth using Foxy endpoint (guest auth variation)
    public async Task AuthAnonymousFoxyAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        string uuid = string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;

        Subscription = new Subscription();

        var formData = new Dictionary<string, string>{
            { "grant_type", "client_id" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", "adobe" },
        };

        var requestContent = new FormUrlEncodedContent(formData);

        // Use a different auth profile for Foxy
        var foxyAuthSettings = new Dictionary<string, string>{
            { "Authorization", "Basic bm9haWhudm5wd2t6cnl0d3J0YW46eW5hbmhnZ3dtZmpsYXR0c3RiaGE=" },
            { "User-Agent", "Crunchyroll/1.4.0 Nintendo Switch/12.3.11" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in foxyAuthSettings)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);

        if (isOk)
        {
            JsonTokenToFileAndVariable(content, uuid);
        }
        else
        {
            _logger?.LogError("Anonymous Foxy login failed: {Error}", error);
        }

        Profile = new CrProfile
        {
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "de-DE"
        };
    }

    // Legacy method - delegates to UpdateAuthCredentialsAsync
    public async Task<bool> CheckStreamEndpointUpdateAsync(CancellationToken cancellationToken = default)
    {
        return await UpdateAuthCredentialsAsync(cancellationToken);
    }

    // Fetches updated auth credentials from upstream data endpoint
    public async Task<bool> UpdateAuthCredentialsAsync(CancellationToken cancellationToken = default)
    {
        // Upstream CRD moved off GitHub to Codeberg (v1.6.14: "Changed GitHub URLs to Codeberg
        // URLs"). The old Crunchy-Downloader GitHub endpoints are dead; auth data now lives on the
        // CRD repo's Codeberg pages branch.
        const string dataUrl = "https://codeberg.org/YomuLoad/CRD/raw/branch/pages/data.json";
        const string fallbackUrl = "https://yomuload.codeberg.page/CRD/data.json";

        string? authResponse = null;

        // Try original URL first
        try
        {
            _logger?.LogInformation("Checking for auth credential updates from {Url}...", dataUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, dataUrl);
            request.Headers.Add("User-Agent", "Cruncharr/1.0");

            using var response = await _httpClient.Client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                authResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                _logger?.LogWarning("Failed to fetch auth data from primary URL: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch auth data from primary URL");
        }

        // Try fallback GitHub raw URL
        if (authResponse == null)
        {
            try
            {
                _logger?.LogInformation("Trying fallback URL {Url}...", fallbackUrl);

                using var request = new HttpRequestMessage(HttpMethod.Get, fallbackUrl);
                request.Headers.Add("User-Agent", "Cruncharr/1.0");

                using var response = await _httpClient.Client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    authResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                else
                {
                    _logger?.LogWarning("Failed to fetch auth data from fallback URL: {Status}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch auth data from fallback URL");
            }
        }

        // Use embedded fallback data if all URLs fail
        if (authResponse == null)
        {
            _logger?.LogInformation("Using embedded auth credentials fallback...");
            authResponse = EmbeddedAuthData;
        }

        try
        {
            var authEntries = JsonConvert.DeserializeObject<List<GhAuthEntry>>(authResponse);

            if (authEntries == null || authEntries.Count == 0)
            {
                _logger?.LogWarning("No auth entries found in auth data");
                // Return true if current credentials are already set (non-empty)
                return !string.IsNullOrEmpty(StreamEndpoint.Authorization) && !string.IsNullOrEmpty(StreamEndpointSecondary.Authorization);
            }

            var ghAuthTv = authEntries.FirstOrDefault(e => e.Type?.Equals("tv", StringComparison.OrdinalIgnoreCase) == true);
            var ghAuthMobile = authEntries.FirstOrDefault(e => e.Type?.Equals("mobile", StringComparison.OrdinalIgnoreCase) == true);

            // Update TV endpoint
            if (StreamEndpoint.UseDefault && ghAuthTv != null &&
                !string.IsNullOrEmpty(ghAuthTv.Authorization) &&
                !string.IsNullOrEmpty(ghAuthTv.VersionName))
            {

                var currentVersion = ExtractClientVersion(StreamEndpoint.UserAgent);
                if (CompareVersions(ghAuthTv.VersionName, currentVersion) > 0)
                {
                    _logger?.LogInformation("Updating TV auth from version {Old} to {New}", currentVersion, ghAuthTv.VersionName);
                    StreamEndpoint.Authorization = ghAuthTv.Authorization;
                    // Real Android TV UA carries the build code: "ANDROIDTV/3.66.0_22348 Android/16".
                    var tvVersion = !string.IsNullOrEmpty(ghAuthTv.VersionCode)
                        ? $"{ghAuthTv.VersionName}_{ghAuthTv.VersionCode}"
                        : ghAuthTv.VersionName;
                    StreamEndpoint.UserAgent = $"ANDROIDTV/{tvVersion} Android/16";
                }
            }

            // Update Mobile endpoint
            if (StreamEndpointSecondary.UseDefault && ghAuthMobile != null &&
                !string.IsNullOrEmpty(ghAuthMobile.Authorization) &&
                !string.IsNullOrEmpty(ghAuthMobile.VersionName))
            {

                var currentVersion = ExtractClientVersion(StreamEndpointSecondary.UserAgent);
                if (CompareVersions(ghAuthMobile.VersionName, currentVersion) > 0)
                {
                    _logger?.LogInformation("Updating Mobile auth from version {Old} to {New}", currentVersion, ghAuthMobile.VersionName);
                    StreamEndpointSecondary.Authorization = ghAuthMobile.Authorization;
                    StreamEndpointSecondary.UserAgent = $"Crunchyroll/{ghAuthMobile.VersionName} Android/16 okhttp/4.12.0";
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse auth credentials");
            // Return true if current credentials are already set (non-empty)
            return !string.IsNullOrEmpty(StreamEndpoint.Authorization) && !string.IsNullOrEmpty(StreamEndpointSecondary.Authorization);
        }
    }

    private static string ExtractClientVersion(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return "0.0.0";

        // Extract version from strings like "ANDROIDTV/3.59.0 Android/16" or "Crunchyroll/3.97.0 Android/16"
        var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"[\w]+/(\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : "0.0.0";
    }

    private static int CompareVersions(string versionA, string versionB)
    {
        try
        {
            var partsA = versionA.Split('.');
            var partsB = versionB.Split('.');

            for (int i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
            {
                int a = i < partsA.Length && int.TryParse(partsA[i], out int pa) ? pa : 0;
                int b = i < partsB.Length && int.TryParse(partsB[i], out int pb) ? pb : 0;

                if (a > b) return 1;
                if (a < b) return -1;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<bool> LoginAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        await _refreshTokenGate.WaitAsync(cancellationToken);
        try
        {
            return await LoginCoreAsync(email, password, useBetaApi, cancellationToken);
        }
        finally
        {
            _refreshTokenGate.Release();
        }
    }

    private async Task<bool> LoginCoreAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken)
    {
        try
        {
            return await LoginInternalAsync(email, password, useBetaApi, cancellationToken);
        }
        catch (Exception ex)
        {
            // Final SELF-RELIANT fallback: Crunchyroll's OWN web client, scraped live from the
            // homepage (accountAuthClientId), used via the etp_rt_cookie flow. Reached only when
            // every embedded client (TV/mobile/alternate) has already failed, so it cannot regress a
            // working login. The web client can't be deactivated without breaking crunchyroll.com,
            // so login stops depending on shipping a fresh embedded client.
            _logger?.LogWarning(ex, "All embedded clients failed; trying self-reliant web-client fallback");
            return await LoginWithWebClientAsync(email, password, useBetaApi, cancellationToken);
        }
    }

    private async Task<bool> LoginInternalAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        // [PT] Upstream CrAuth.Auth(AuthData): TV endpoints keep the password grant,
        // other endpoints use the SSO authorization-code (PKCE) flow
        if (StreamEndpoint.Endpoint.StartsWith("tv", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await LoginPasswordGrantAsync(email, password, useBetaApi, cancellationToken);
            }
            catch (Exception ex)
            {
                // The password grant is dead (Crunchyroll deactivated those clients). Fall back
                // to the SSO authorization-code (PKCE) flow, which the mobile client supports.
                // StreamEndpoint is already pinned to the mobile client in the constructor, so
                // login / refresh / profile switch all use the same client_id. This only runs
                // after the password grant has already failed, so it cannot regress a working login.
                _logger?.LogWarning(ex, "Password grant failed; falling back to SSO code flow");
                return await LoginWithCodeFlowAsync(email, password, useBetaApi, cancellationToken);
            }
        }

        try
        {
            return await LoginWithCodeFlowAsync(email, password, useBetaApi, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SSO code login failed with primary client");

            // AUTO-SWITCH: if using the default android client and it failed (e.g. Crunchyroll
            // deactivated it), try the alternate embedded android client before giving up. Only
            // runs after the primary already failed, so it cannot regress a working login, and it
            // is skipped when the user configured their own client (respect the override).
            if (StreamEndpoint.Authorization == DefaultAndroidAuthSettings.Authorization
                && !string.IsNullOrEmpty(AlternateAndroidAuthSettings.Authorization))
            {
                _logger?.LogWarning("Trying alternate embedded android client...");
                StreamEndpoint.Authorization = AlternateAndroidAuthSettings.Authorization;
                StreamEndpoint.UserAgent = AlternateAndroidAuthSettings.UserAgent;
                StreamEndpoint.Device_type = AlternateAndroidAuthSettings.Device_type;
                StreamEndpoint.Device_name = AlternateAndroidAuthSettings.Device_name;
                StreamEndpoint.Endpoint = AlternateAndroidAuthSettings.Endpoint;
                try
                {
                    return await LoginWithCodeFlowAsync(email, password, useBetaApi, cancellationToken);
                }
                catch (Exception altEx)
                {
                    _logger?.LogWarning(altEx, "Alternate android client login also failed");
                }
            }

            _logger?.LogWarning("Falling back to password grant");
            return await LoginPasswordGrantAsync(email, password, useBetaApi, cancellationToken);
        }
    }

    // [PT] Ported from upstream CrAuth.AuthCode / GetCodeAuth / LoginWithCode (PKCE flow)
    private string _authCodeVerifier = string.Empty;
    private string _authCode = string.Empty;
    private const string SsoDomain = "sso.crunchyroll.com";

    private async Task<bool> LoginWithCodeFlowAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Logging in via SSO code flow"); // email omitted: logs are diagnostics-readable

        // Start from a clean cookie jar (matches upstream CrAuth.Auth). A leftover etp_rt from a
        // prior session otherwise gets sent to the SSO /authorize step and makes it return no
        // code ("Missing authorization code from SSO flow") on re-login.
        _httpClient.ClearCookies();

        var uuid = ResolveDeviceId();
        var loginPayload = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            { "email", email },
            { "password", password },
            { "eventSettings", new Dictionary<string, object>() }
        });
        var requestContent = new StringContent(loginPayload, System.Text.Encoding.UTF8);
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "UTF-8" };

        var ssoRequest = new HttpRequestMessage(HttpMethod.Post, $"https://{SsoDomain}/api/login")
        {
            Content = requestContent
        };
        ssoRequest.Headers.Add("User-Agent", StreamEndpoint.UserAgent);

        var (ssoOk, ssoContent, ssoError) = await _httpClient.SendRequestAsync(ssoRequest);

        if (!ssoOk)
        {
            throw new Exception(BuildLoginErrorMessage(ssoContent, ssoError));
        }

        var refreshToken = _httpClient.GetCookieValue(SsoDomain, "etp_rt");
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new Exception("SSO login did not return a refresh token cookie");
        }

        Token = new CrToken { refresh_token = refreshToken, device_id = uuid };

        await GetCodeAuthAsync(cancellationToken);
        return await LoginWithCodeAsync(useBetaApi, uuid, cancellationToken);
    }

    private async Task GetCodeAuthAsync(CancellationToken cancellationToken)
    {
        var uuid = Guid.NewGuid().ToString();

        _authCodeVerifier = GenerateCodeVerifier();
        var clientId = GetClientIdFromBasicHeader(StreamEndpoint.Authorization);

        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = clientId;
        query["redirect_uri"] = "sso.crunchyroll://auth";
        query["response_type"] = "code";
        query["scope"] = "offline_access";
        query["state"] = "{\"flow\":\"SIGN_IN\",\"flowRoot\":\"ONBOARDING\"}";
        query["code_challenge"] = _authCodeVerifier;
        query["code_challenge_method"] = "plain";

        _httpClient.AddCookie(SsoDomain, new Cookie("client_id", clientId));
        _httpClient.AddCookie(SsoDomain, new Cookie("device_id", uuid));

        var uriBuilder = new UriBuilder($"https://{SsoDomain}/authorize")
        {
            Query = query.ToString()
        };

        var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.ToString());
        request.Headers.Add("User-Agent", StreamEndpoint.UserAgent);

        var (_, content, _) = await _httpClient.SendRequestAsync(request);

        _authCode = ExtractCode(content ?? string.Empty);

        if (string.IsNullOrEmpty(_authCode))
        {
            _logger?.LogError("Auth code is empty");
        }
    }

    private async Task<bool> LoginWithCodeAsync(bool useBetaApi, string uuid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_authCode))
        {
            throw new Exception("Missing authorization code from SSO flow");
        }

        var formData = new Dictionary<string, string>
        {
            { "code", _authCode },
            { "code_verifier", _authCodeVerifier },
            { "grant_type", "authorization_code" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type }
        };

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("Authorization", StreamEndpoint.Authorization);
        request.Headers.Add("User-Agent", StreamEndpoint.UserAgent);

        SetETPCookie(Token?.refresh_token ?? string.Empty);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            throw new Exception(BuildLoginErrorMessage(content, error));
        }

        JsonTokenToFileAndVariable(content, uuid);

        if (Token?.refresh_token != null)
        {
            SetETPCookie(Token.refresh_token);
            SaveToken();
            await GetMultiProfileAsync(useBetaApi, cancellationToken);
            return true;
        }

        throw new Exception("Login failed - no refresh token received from Crunchyroll");
    }

    // ---- Self-reliant web-client fallback --------------------------------------------------
    // Crunchyroll's website embeds its client id in the homepage HTML (accountAuthClientId). It is
    // a PUBLIC value (not a secret) and the one CR's own site uses, so it cannot be deactivated
    // without breaking crunchyroll.com. We scrape it live and authenticate via the etp_rt_cookie
    // grant (no /authorize redirect needed), giving a login path that doesn't depend on shipping a
    // fresh embedded client. Verified end-to-end against beta-api.
    private const string FallbackWebClientId = "kmj7imhjt_q90lcbzzsj"; // last-known; used only if the live scrape is blocked
    private const string WebClientUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string WebClientDeviceType = "com.crunchyroll.static";
    private static string? _cachedWebClientBasic;

    private async Task<string> FetchWebClientBasicAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_cachedWebClientBasic)) return _cachedWebClientBasic!;
        string? clientId = null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://www.crunchyroll.com/");
            req.Headers.Add("User-Agent", WebClientUserAgent);
            var (ok, html, _) = await _httpClient.SendRequestAsync(req, suppressError: true, attachCookies: false);
            if (ok && !string.IsNullOrEmpty(html))
            {
                var m = System.Text.RegularExpressions.Regex.Match(html, "accountAuthClientId\"\\s*:\\s*\"([A-Za-z0-9_-]+)\"");
                if (m.Success) clientId = m.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to scrape Crunchyroll web client id; using last-known fallback");
        }
        clientId ??= FallbackWebClientId;
        _logger?.LogInformation("Using Crunchyroll web client id {ClientId}", clientId);
        _cachedWebClientBasic = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(clientId + ":"));
        return _cachedWebClientBasic;
    }

    private async Task<bool> LoginWithWebClientAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken)
    {
        var webBasic = await FetchWebClientBasicAsync(cancellationToken);

        // 1) SSO login (email/password -> etp_rt cookie). Client-independent.
        _httpClient.ClearCookies();
        var uuid = ResolveDeviceId();
        var loginPayload = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            { "email", email },
            { "password", password },
            { "eventSettings", new Dictionary<string, object>() }
        });
        var requestContent = new StringContent(loginPayload, System.Text.Encoding.UTF8);
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "UTF-8" };
        var ssoRequest = new HttpRequestMessage(HttpMethod.Post, $"https://{SsoDomain}/api/login") { Content = requestContent };
        ssoRequest.Headers.Add("User-Agent", WebClientUserAgent);
        var (ssoOk, ssoContent, ssoError) = await _httpClient.SendRequestAsync(ssoRequest);
        if (!ssoOk) throw new Exception(BuildLoginErrorMessage(ssoContent, ssoError));

        var refreshToken = _httpClient.GetCookieValue(SsoDomain, "etp_rt");
        if (string.IsNullOrEmpty(refreshToken)) throw new Exception("SSO login did not return a refresh token cookie");

        // 2) Token exchange via the etp_rt_cookie grant with the WEB client. Send ONLY the etp_rt
        //    cookie (SetETPCookie + attachCookies:false) to avoid cookie-pollution information_mismatch.
        var formData = new Dictionary<string, string>
        {
            { "grant_type", "etp_rt_cookie" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", WebClientDeviceType }
        };
        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth) { Content = new FormUrlEncodedContent(formData) };
        tokenRequest.Headers.Add("Authorization", webBasic);
        tokenRequest.Headers.Add("User-Agent", WebClientUserAgent);
        // Send ONLY the etp_rt cookie. The wrapper has UseCookies=false: attachCookies:false sends
        // nothing, attachCookies:true sends the whole store (cross-domain pollution ->
        // information_mismatch). So hand it exactly one cookie via a manual header.
        tokenRequest.Headers.TryAddWithoutValidation("Cookie", "etp_rt=" + refreshToken);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(tokenRequest, suppressError: false, attachCookies: false);
        if (!isOk) throw new Exception(BuildLoginErrorMessage(content, error));

        JsonTokenToFileAndVariable(content, uuid);
        if (Token?.refresh_token == null) throw new Exception("Web-client login failed - no refresh token received");

        // Pin the session to the web client so refresh + profile switch present the SAME client_id
        // that minted the token (Crunchyroll validates this).
        StreamEndpoint.Authorization = webBasic;
        StreamEndpoint.UserAgent = WebClientUserAgent;
        StreamEndpoint.Device_type = WebClientDeviceType;
        StreamEndpoint.Device_name = string.Empty;
        StreamEndpoint.Endpoint = "web/chrome"; // play-URL device for the web client

        SetETPCookie(Token.refresh_token);
        SaveToken();
        await GetMultiProfileAsync(useBetaApi, cancellationToken);
        _logger?.LogInformation("Login successful via self-reliant web client");
        return true;
    }

    private string ResolveDeviceId()
    {
        return string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token!.device_id!;
    }

    // [PT] Ported from upstream CrAuth.GenerateCodeVerifier (RFC 7636)
    private static string GenerateCodeVerifier(int length = 64)
    {
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        var stringBuilder = new System.Text.StringBuilder(length);

        foreach (var value in bytes)
        {
            stringBuilder.Append(allowed[value % allowed.Length]);
        }

        return stringBuilder.ToString();
    }

    // [PT] Ported from upstream CrAuth.GetClientIdFromBasicHeader
    private static string GetClientIdFromBasicHeader(string authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            throw new ArgumentException("Authorization header is null/empty.", nameof(authorizationHeader));
        }

        const string prefix = "Basic ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Authorization header is not Basic.");
        }

        var base64 = authorizationHeader[prefix.Length..].Trim();

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Basic token is not valid Base64.", ex);
        }

        var decoded = System.Text.Encoding.UTF8.GetString(bytes);
        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex <= 0)
        {
            throw new FormatException("Decoded Basic value is not in 'clientId:clientSecret' format.");
        }

        return decoded[..separatorIndex];
    }

    // [PT] Ported from upstream CrAuth.Normalize / ExtractCode
    private static string NormalizeAuthBody(string value)
    {
        value = System.Text.RegularExpressions.Regex.Unescape(value);
        value = value.Replace(@"&", "&");
        value = value.Replace("\\\"", "\"");
        return value;
    }

    private static string ExtractCode(string body)
    {
        var text = NormalizeAuthBody(body);

        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:[?&]|\\u0026)code=([A-Za-z0-9\-_]+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = System.Text.RegularExpressions.Regex.Match(text, @"code=([A-Za-z0-9\-_]+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }

    private static string BuildLoginErrorMessage(string? content, string? error)
    {
        if (string.IsNullOrEmpty(content))
        {
            return $"Login failed: {error}";
        }
        if (content.Contains("invalid_credentials"))
        {
            return "Invalid credentials - please check your email and password";
        }
        if (content.Contains("<title>Just a moment...</title>") ||
            content.Contains("<title>Access denied</title>") ||
            content.Contains("<title>Attention Required! | Cloudflare</title>") ||
            content.Trim().Equals("error code: 1020") ||
            content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1)
        {
            return "Cloudflare/DDOS protection detected - try enabling Beta API in settings";
        }
        var responsePreview = content.Substring(0, Math.Min(500, content.Length));
        return $"Login failed: {responsePreview}";
    }

    private async Task<bool> LoginPasswordGrantAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Logging in (BetaAPI: {UseBeta})", useBetaApi); // email omitted: logs are diagnostics-readable

        string uuid = Guid.NewGuid().ToString();

        var formData = new Dictionary<string, string>{
            { "username", email },
            { "password", password },
            { "grant_type", "password" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type }
        };

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var requestContent = new FormUrlEncodedContent(formData);

        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in crunchyAuthHeaders)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        var authPreview = StreamEndpoint.Authorization?.Length >= 20 ? StreamEndpoint.Authorization.Substring(0, 20) + "..." : StreamEndpoint.Authorization ?? "(null)";
        _logger?.LogDebug("Login request to {Url} with auth: {Auth}", ApiUrls.Auth, authPreview);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, suppressError: false, attachCookies: false);

        _logger?.LogDebug("Login response: IsOk={IsOk}, Error={Error}, Content={Content}", isOk, error, content?.Substring(0, Math.Min(500, content?.Length ?? 0)));

        if (isOk && content != null)
        {
            JsonTokenToFileAndVariable(content, uuid);
            _logger?.LogInformation("Login successful, token received");
        }
        else
        {
            _logger?.LogError("Login failed: HTTP Error={Error}, Response={Response}", error, content);

            // If the primary (TV) client is inactive, fall back to the secondary
            // (mobile/android) client, which uses a different client_id that may still
            // be active. The hosted credential update is also attempted in case Crunchyroll
            // re-publishes data.json.
            if (content?.Contains("client_inactive") == true || content?.Contains("invalid_client") == true)
            {
                _logger?.LogWarning("Primary (TV) auth client inactive; attempting credential update + secondary-client fallback...");
                await UpdateAuthCredentialsAsync(cancellationToken);

                // Use the secondary (mobile) client token if it differs from the dead primary.
                var fallbackAuth = !string.IsNullOrEmpty(StreamEndpointSecondary.Authorization)
                    ? StreamEndpointSecondary.Authorization
                    : StreamEndpoint.Authorization;

                if (!string.IsNullOrEmpty(fallbackAuth))
                {
                    // IMPORTANT: build a NEW request body. FormUrlEncodedContent is consumed
                    // by the first SendAsync and cannot be reused - reusing it throws
                    // "An error occurred while sending the request" and masks the real cause.
                    var retryFormData = new Dictionary<string, string>
                    {
                        { "username", email },
                        { "password", password },
                        { "grant_type", "password" },
                        { "scope", "offline_access" },
                        { "device_id", uuid },
                        { "device_type", StreamEndpointSecondary.Device_type ?? StreamEndpoint.Device_type }
                    };
                    if (!string.IsNullOrEmpty(StreamEndpointSecondary.Device_name))
                    {
                        retryFormData["device_name"] = StreamEndpointSecondary.Device_name;
                    }

                    var retryRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
                    {
                        Content = new FormUrlEncodedContent(retryFormData)
                    };
                    retryRequest.Headers.Add("Authorization", fallbackAuth);
                    retryRequest.Headers.Add("User-Agent", StreamEndpointSecondary.UserAgent ?? StreamEndpoint.UserAgent);

                    _logger?.LogInformation("Retrying login with secondary (mobile) client...");
                    var (retryIsOk, retryContent, retryError) = await _httpClient.SendRequestAsync(retryRequest, suppressError: false, attachCookies: false);

                    if (retryIsOk)
                    {
                        JsonTokenToFileAndVariable(retryContent, uuid);
                        _logger?.LogInformation("Login successful with secondary (mobile) client");
                        isOk = true;
                        // Keep using the working client for the rest of this session
                        // (profile switches/refresh must use the same client_id).
                        StreamEndpoint.Authorization = StreamEndpointSecondary.Authorization ?? string.Empty;
                        StreamEndpoint.UserAgent = StreamEndpointSecondary.UserAgent ?? string.Empty;
                        StreamEndpoint.Device_type = StreamEndpointSecondary.Device_type ?? string.Empty;
                        StreamEndpoint.Device_name = StreamEndpointSecondary.Device_name ?? string.Empty;
                    }
                    else
                    {
                        content = retryContent;
                        error = retryError;
                    }
                }

                // Tier-2 fallback: the maintained crunchy-cli client (t-kdgp...). Reached ONLY when
                // BOTH the primary (TV) and secondary (mobile) clients are inactive — login is
                // already broken at this point — so this can only help, never regress a working login.
                if (!isOk && (content?.Contains("client_inactive") == true || content?.Contains("invalid_client") == true))
                {
                    var maintainedForm = new Dictionary<string, string>
                    {
                        { "username", email },
                        { "password", password },
                        { "grant_type", "password" },
                        { "scope", "offline_access" },
                        { "device_id", uuid },
                        { "device_type", MaintainedFallbackAuthSettings.Device_type }
                    };
                    if (!string.IsNullOrEmpty(MaintainedFallbackAuthSettings.Device_name))
                    {
                        maintainedForm["device_name"] = MaintainedFallbackAuthSettings.Device_name;
                    }
                    var maintainedRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
                    {
                        Content = new FormUrlEncodedContent(maintainedForm)
                    };
                    maintainedRequest.Headers.Add("Authorization", MaintainedFallbackAuthSettings.Authorization);
                    maintainedRequest.Headers.Add("User-Agent", MaintainedFallbackAuthSettings.UserAgent);

                    _logger?.LogInformation("Retrying login with maintained (crunchy-cli) client...");
                    var (maintainedOk, maintainedContent, maintainedError) = await _httpClient.SendRequestAsync(maintainedRequest, suppressError: false, attachCookies: false);

                    if (maintainedOk)
                    {
                        JsonTokenToFileAndVariable(maintainedContent, uuid);
                        _logger?.LogInformation("Login successful with maintained (crunchy-cli) client");
                        isOk = true;
                        StreamEndpoint.Authorization = MaintainedFallbackAuthSettings.Authorization;
                        StreamEndpoint.UserAgent = MaintainedFallbackAuthSettings.UserAgent;
                        StreamEndpoint.Device_type = MaintainedFallbackAuthSettings.Device_type;
                        StreamEndpoint.Device_name = MaintainedFallbackAuthSettings.Device_name;
                        StreamEndpoint.Endpoint = MaintainedFallbackAuthSettings.Endpoint;
                    }
                    else
                    {
                        content = maintainedContent;
                        error = maintainedError;
                    }
                }
            }

            if (!isOk)
            {
                string errorMessage;
                if (string.IsNullOrEmpty(content))
                {
                    errorMessage = $"Login failed: {error}";
                }
                else if (content.Contains("invalid_credentials"))
                {
                    errorMessage = "Invalid credentials - please check your email and password";
                    _logger?.LogError("Invalid credentials");
                }
                else if (content.Contains("<title>Just a moment...</title>") ||
                           content.Contains("<title>Access denied</title>") ||
                           content.Contains("<title>Attention Required! | Cloudflare</title>") ||
                           content.Trim().Equals("error code: 1020") ||
                           content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1)
                {
                    errorMessage = "Cloudflare/DDOS protection detected - try enabling Beta API in settings";
                    _logger?.LogError("Cloudflare/DDOS protection detected during login");
                }
                else if (content.Contains("client_inactive"))
                {
                    errorMessage = "Login failed: Crunchyroll has deactivated this client. Please check for application updates.";
                    _logger?.LogError("Client deactivated by Crunchyroll");
                }
                else
                {
                    var responsePreview = content.Substring(0, Math.Min(500, content.Length));
                    errorMessage = $"Login failed: {responsePreview}";
                    _logger?.LogError("Login error response: {Response}", responsePreview);
                }
                throw new Exception(errorMessage);
            }
        }

        if (Token?.refresh_token != null)
        {
            SetETPCookie(Token.refresh_token);
            SaveToken();
            await GetMultiProfileAsync(useBetaApi, cancellationToken);
            return true;
        }

        throw new Exception("Login failed - no refresh token received from Crunchyroll");
    }

    public async Task<bool> LoginWithTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        if (Token?.refresh_token == null)
        {
            _logger?.LogWarning("Missing Refresh Token");
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return false;
        }

        string uuid = string.IsNullOrEmpty(Token.device_id) ? Guid.NewGuid().ToString() : Token.device_id;

        var formData = new Dictionary<string, string>{
            { "refresh_token", Token.refresh_token },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "grant_type", "refresh_token" },
            { "device_type", StreamEndpoint.Device_type },
        };

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var requestContent = new FormUrlEncodedContent(formData);

        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in crunchyAuthHeaders)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        SetETPCookie(Token.refresh_token);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);

        if (!string.IsNullOrEmpty(content) && (
            content.Contains("<title>Just a moment...</title>") ||
            content.Contains("<title>Access denied</title>") ||
            content.Contains("<title>Attention Required! | Cloudflare</title>") ||
            content.Trim().Equals("error code: 1020") ||
            content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1))
        {
            _logger?.LogError("Cloudflare error during token login");
        }

        if (isOk)
        {
            JsonTokenToFileAndVariable(content, uuid);

            if (Token?.refresh_token != null)
            {
                SetETPCookie(Token.refresh_token);
                SaveToken();
                await GetMultiProfileAsync(useBetaApi, cancellationToken);
                return true;
            }
        }
        else
        {
            // Keep the session on a failed token refresh: this used to fall back to
            // AuthAnonymousAsync, which overwrote the user's token in memory AND on disk with
            // a guest token - one transient error logged the account out permanently. The
            // refresh token stays valid for a later retry (RefreshTokenAsync preflights).
            _logger?.LogError("Token Auth Failed: {Error}", error);
        }

        return false;
    }

    public async Task LogoutAsync()
    {
        await _refreshTokenGate.WaitAsync();
        try
        {
            Token = null;
            Init();
            DeleteToken();
        }
        finally
        {
            _refreshTokenGate.Release();
        }
    }

    public async Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default, bool force = false)
    {
        await _refreshTokenGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshTokenCoreAsync(useBetaApi, cancellationToken, force);
        }
        finally
        {
            _refreshTokenGate.Release();
        }
    }

    private async Task<bool> RefreshTokenCoreAsync(bool useBetaApi, CancellationToken cancellationToken, bool force)
    {
        if (EndpointEnum == CrunchyrollEndpoints.Guest)
        {
            if (!force && !IsTokenExpiredOrNearExpiry())
            {
                return true;
            }
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return true;
        }

        if (Token?.access_token == null && Token?.refresh_token == null ||
            Token?.access_token != null && Token?.refresh_token == null)
        {
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return false;
        }
        else
        {
            if (!force && !IsTokenExpiredOrNearExpiry())
            {
                return true;
            }
        }

        var hadUserSession = !string.IsNullOrWhiteSpace(Token?.refresh_token) && !string.IsNullOrWhiteSpace(Profile.Username) && Profile.Username != "???";

        string uuid = string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;

        var formData = new Dictionary<string, string>{
            { "refresh_token", Token?.refresh_token ?? "" },
            { "grant_type", "refresh_token" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type },
        };

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var requestContent = new FormUrlEncodedContent(formData);

        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in crunchyAuthHeaders)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        SetETPCookie(Token?.refresh_token ?? string.Empty);

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);

        if (isOk)
        {
            JsonTokenToFileAndVariable(content, uuid);
            _notification?.ResetLoginExpiredNotification();
            return true;
        }
        else
        {
            _logger?.LogError("Refresh Token Auth Failed: {Error}", error);
            if (hadUserSession)
            {
                _logger?.LogWarning("User session expired - login required");
                if (_notification != null && _config != null)
                {
                    try { await _notification.NotifyLoginExpiredAsync(Profile?.Username, _config); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Login-expired notification failed"); }
                }
            }
            return false;
        }
    }

    public async Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        if (Token?.access_token == null)
        {
            _logger?.LogWarning("Missing Access Token for multi-profile");
            return;
        }

        var request = HttpClientWrapper.CreateRequest(ApiUrls.MultiProfile, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (isOk)
        {
            var multiProfile = JsonConvert.DeserializeObject<CrMultiProfile>(content);
            if (multiProfile != null)
            {
                MultiProfile = multiProfile;
                _logger?.LogInformation("Loaded {Count} profiles", MultiProfile.Profiles.Count);

                var selectedProfile = MultiProfile.Profiles.FirstOrDefault(e => e.IsSelected);
                if (selectedProfile != null)
                {
                    Profile = selectedProfile;
                }

                await GetSubscriptionAsync(useBetaApi, cancellationToken);
                // Profile (with its preferred languages + id) is now finalized -> sync defaults.
                SyncDefaultLanguagesFromProfile();
            }
        }
        else
        {
            // Non-fatal: some accounts/regions don't expose the multi-profile endpoint
            // (often 403). The app continues with the single active profile. Warn once, then
            // demote to Debug so it doesn't flood the log on every refresh.
            if (!_multiProfileUnavailableLogged)
            {
                _multiProfileUnavailableLogged = true;
                _logger?.LogWarning("Multi-profile unavailable (continuing with single profile): {Error}", error);
            }
            else
            {
                _logger?.LogDebug("Multi-profile still unavailable: {Error}", error);
            }
        }
    }

    public async Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, string? pin = null, CancellationToken cancellationToken = default)
    {
        await _refreshTokenGate.WaitAsync(cancellationToken);
        try
        {
            return await ChangeProfileCoreAsync(profileId, useBetaApi, pin, cancellationToken);
        }
        finally
        {
            _refreshTokenGate.Release();
        }
    }

    private async Task<bool> ChangeProfileCoreAsync(string profileId, bool useBetaApi, string? pin, CancellationToken cancellationToken)
    {
        if (Token?.access_token == null && Token?.refresh_token == null ||
            Token?.access_token != null && Token?.refresh_token == null)
        {
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
        }

        // Verify token was obtained after anonymous auth fallback
        if (Token == null)
        {
            _logger?.LogError("ChangeProfileAsync failed: Token is null after AuthAnonymousAsync");
            return false;
        }

        if (Profile.Username == "???")
        {
            return false;
        }

        if (string.IsNullOrEmpty(profileId) || Token?.refresh_token == null)
        {
            return false;
        }

        string uuid = string.IsNullOrEmpty(Token.device_id) ? Guid.NewGuid().ToString() : Token.device_id;

        SetETPCookie(Token.refresh_token);

        var formData = new Dictionary<string, string>{
            { "grant_type", "refresh_token_profile_id" },
            { "profile_id", profileId },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type }
        };

        // PIN-protected profiles require the account PIN to switch into them.
        if (!string.IsNullOrWhiteSpace(pin))
        {
            formData["profile_pin"] = pin.Trim();
        }

        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name))
        {
            formData.Add("device_name", StreamEndpoint.Device_name);
        }

        var requestContent = new FormUrlEncodedContent(formData);

        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth)
        {
            Content = requestContent
        };

        foreach (var header in crunchyAuthHeaders)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        // Send ONLY the etp_rt cookie for the refresh_token_profile_id grant, and do not let
        // the shared cookie store attach anything else. The store also holds
        // sso.crunchyroll.com cookies (a second etp_rt + client_id + device_id from the SSO
        // login flow); leaking those onto this beta-api request makes Crunchyroll reject the
        // grant with a client_id "information_mismatch" (verified: an etp_rt-only request
        // succeeds, the same request plus the SSO cookies fails).
        if (Token?.refresh_token != null)
        {
            SetETPCookie(Token.refresh_token);
            request.Headers.Add("Cookie", $"etp_rt={Token.refresh_token}");
        }

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, suppressError: false, attachCookies: false);

        if (isOk)
        {
            JsonTokenToFileAndVariable(content, uuid);
            if (Token?.refresh_token != null)
            {
                SetETPCookie(Token.refresh_token);
                SaveToken();
            }

            await GetMultiProfileAsync(useBetaApi, cancellationToken);
            return true;
        }
        else
        {
            LastProfileSwitchError = !string.IsNullOrWhiteSpace(content) ? content : error;
            _logger?.LogError("Change profile failed: {Error}; Response={Content}", error, content);
        }

        return false;
    }

    /// <summary>Crunchyroll's raw error from the most recent failed profile switch (for diagnostics/UI).</summary>
    public string? LastProfileSwitchError { get; private set; }

    private async Task GetProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        if (Token?.access_token == null)
        {
            _logger?.LogWarning("Missing Access Token");
            return;
        }

        var request = HttpClientWrapper.CreateRequest(ApiUrls.Profile, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (isOk)
        {
            var profileTemp = JsonConvert.DeserializeObject<CrProfile>(content);
            if (profileTemp != null)
            {
                Profile = profileTemp;
                _logger?.LogInformation("Logged in as {Username}", Profile.Username);
                await GetSubscriptionAsync(useBetaApi, cancellationToken);
                // Fallback sync for accounts where the multi-profile endpoint is unavailable; no-ops
                // if GetMultiProfileAsync already synced this profile (same key).
                SyncDefaultLanguagesFromProfile();
            }
        }
        else
        {
            _logger?.LogError("Failed to get profile: {Error}", error);
        }
    }

    // The factory "select everything" language lists. A config still holding the full set was never
    // customised by the user, so the profile sync may narrow it to the profile's language.
    private static readonly List<string> _factoryDubLanguages = new DownloadConfig().DubLanguages;
    private static readonly List<string> _factorySoftSubs = new DownloadConfig().SoftSubs;
    private static readonly List<string> _factorySubtitleLanguages = new DownloadConfig().SubtitleLanguages;

    private static bool IsUntouchedFullSet(List<string>? current, List<string> factory)
        => current != null && current.Count == factory.Count && new HashSet<string>(current).SetEquals(factory);

    // Pure decision+mutation for the profile->defaults sync (unit-testable). Sets DefaultAudio/
    // DefaultSub AND the dub/sub language lists from the profile's preferred languages.
    //   - Scalars (DefaultAudio/DefaultSub) sync only when the active profile actually changes
    //     (profileKey != LastSyncedProfileId), so a manual default change made while on one profile
    //     is preserved.
    //   - The multi-select lists (DubLanguages/SoftSubs/SubtitleLanguages) sync when the profile
    //     changes OR when they are still the untouched factory "all languages" set — the latter
    //     self-heals an existing install whose lists were never narrowed (the user's complaint:
    //     every language pre-selected, unaffected by the profile). A list the user has edited (any
    //     selection other than the full factory set) is left alone.
    // Returns true if the config changed and should be persisted.
    internal static bool ApplyProfileLanguageDefaults(CruncharrConfig config, string? profileKey, string? prefAudio, string? prefSub)
    {
        if (config?.Download?.SyncDefaultsFromProfile != true) return false;
        if (string.IsNullOrWhiteSpace(profileKey)) return false;

        bool profileChanged = !string.Equals(profileKey, config.Crunchyroll?.LastSyncedProfileId, StringComparison.Ordinal);
        bool dubUntouched = IsUntouchedFullSet(config.Download.DubLanguages, _factoryDubLanguages);
        bool softUntouched = IsUntouchedFullSet(config.Download.SoftSubs, _factorySoftSubs);
        bool subUntouched = IsUntouchedFullSet(config.Download.SubtitleLanguages, _factorySubtitleLanguages);

        if (!profileChanged && !dubUntouched && !softUntouched && !subUntouched) return false;

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(prefAudio))
        {
            if (profileChanged && config.Download.DefaultAudio != prefAudio) { config.Download.DefaultAudio = prefAudio!; changed = true; }
            if (profileChanged || dubUntouched) { config.Download.DubLanguages = new List<string> { prefAudio! }; changed = true; }
        }
        if (!string.IsNullOrWhiteSpace(prefSub))
        {
            if (profileChanged && config.Download.DefaultSub != prefSub) { config.Download.DefaultSub = prefSub!; changed = true; }
            if (profileChanged || softUntouched) { config.Download.SoftSubs = new List<string> { prefSub! }; changed = true; }
            if (profileChanged || subUntouched) { config.Download.SubtitleLanguages = new List<string> { prefSub! }; changed = true; }
        }
        if (profileChanged) { config.Crunchyroll!.LastSyncedProfileId = profileKey!; changed = true; }
        return changed;
    }

    private void SyncDefaultLanguagesFromProfile()
    {
        try
        {
            if (_config == null || Profile == null || Profile.Username == "???") return;
            var key = !string.IsNullOrWhiteSpace(Profile.ProfileId) ? Profile.ProfileId : Profile.Username;
            if (ApplyProfileLanguageDefaults(_config, key, Profile.PreferredContentAudioLanguage, Profile.PreferredContentSubtitleLanguage))
            {
                var path = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
                _config.Save(path);
                _logger?.LogInformation("Synced default languages from profile {Key}: audio={Audio} sub={Sub}",
                    key, _config.Download.DefaultAudio, _config.Download.DefaultSub);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Profile language sync failed");
        }
    }

    private async Task GetSubscriptionAsync(bool useBetaApi, CancellationToken cancellationToken = default)
    {
        if (Token?.access_token == null || Token?.account_id == null)
        {
            _logger?.LogWarning("Missing access token or account ID for subscription check");
            return;
        }

        var request = HttpClientWrapper.CreateRequest(ApiUrls.Subscription + Token.account_id, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (isOk)
        {
            var subsc = JsonConvert.DeserializeObject<Subscription>(content);
            Subscription = subsc;

            if (subsc is { SubscriptionProducts: { Count: 0 }, ThirdPartySubscriptionProducts: { Count: > 0 } })
            {
                var thirdPartySub = subsc.ThirdPartySubscriptionProducts.First();
                var expiration = thirdPartySub.InGrace ? thirdPartySub.InGraceExpirationDate : thirdPartySub.ExpirationDate;
                var remaining = expiration - DateTime.Now;
                Profile.HasPremium = true;
                if (Subscription != null)
                {
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = expiration;
                }
            }
            else if (subsc is { SubscriptionProducts: { Count: 0 }, NonrecurringSubscriptionProducts: { Count: > 0 } })
            {
                var nonRecurringSub = subsc.NonrecurringSubscriptionProducts.First();
                var remaining = nonRecurringSub.EndDate - DateTime.Now;
                Profile.HasPremium = true;
                if (Subscription != null)
                {
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = nonRecurringSub.EndDate;
                }
            }
            else if (subsc is { SubscriptionProducts: { Count: 0 }, FunimationSubscriptions: { Count: > 0 } })
            {
                Profile.HasPremium = true;
            }
            else if (subsc is { SubscriptionProducts.Count: > 0 })
            {
                Profile.HasPremium = true;
            }
            else
            {
                Profile.HasPremium = false;
                _logger?.LogWarning("No subscription available: {Subscription}", JsonConvert.SerializeObject(subsc, Formatting.Indented));
            }
        }
        else
        {
            Profile.HasPremium = false;
            _logger?.LogError("Failed to check premium subscription status: {Error}", error);
        }
    }

    private void SetETPCookie(string refreshToken)
    {
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("etp_rt", refreshToken));
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("c_locale", "en-US"));
    }

    private void JsonTokenToFileAndVariable(string content, string deviceId)
    {
        Token = JsonConvert.DeserializeObject<CrToken>(content);

        if (Token is { expires_in: not null })
        {
            Token.device_id = deviceId;
            Token.expires = DateTime.Now.AddSeconds((double)Token.expires_in);

            if (EndpointEnum == CrunchyrollEndpoints.Guest)
            {
                return;
            }

            SaveToken();
        }
    }

    private bool IsTokenExpiredOrNearExpiry()
    {
        return Token == null || DateTime.Now >= Token.expires - TokenRefreshBuffer;
    }

    public void LoadToken()
    {
        try
        {
            var tokenFile = GetTokenFilePath();
            if (File.Exists(tokenFile))
            {
                var content = File.ReadAllText(tokenFile);
                Token = JsonConvert.DeserializeObject<CrToken>(content);
                if (Token != null && Token.refresh_token != null)
                {
                    SetETPCookie(Token.refresh_token);
                    _logger?.LogInformation("Loaded token from {Path}", tokenFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load token from file");
        }
    }

    public void SaveToken()
    {
        try
        {
            if (Token != null)
            {
                var tokenFile = GetTokenFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
                // Atomic write (temp + rename) so a crash mid-write can't corrupt the token file.
                var tmp = tokenFile + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(Token, Formatting.Indented));
                File.Move(tmp, tokenFile, overwrite: true);
                Cruncharr.Core.Utils.SecureFile.Restrict(tokenFile);
                _logger?.LogDebug("Saved token to {Path}", tokenFile);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save token to file");
        }
    }

    public void DeleteToken()
    {
        try
        {
            var tokenFile = GetTokenFilePath();
            if (File.Exists(tokenFile))
            {
                File.Delete(tokenFile);
                _logger?.LogInformation("Deleted token from {Path}", tokenFile);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete token from file");
        }
    }

    // Ported from upstream CrunchyrollManager.GetBase64EncodedTokenAsync
    // Fetches and extracts the client token from Crunchyroll's JS bundle

    private class GhAuthEntry
    {
        [JsonProperty("type")]
        public string? Type { get; set; }

        // Upstream CRD's Codeberg data.json uses capital "Authorization"; old Crunchy-Downloader
        // data.json used lowercase "authorization". Newtonsoft matches case-insensitively.
        [JsonProperty("Authorization")]
        public string? Authorization { get; set; }

        // New schema: "version_name" + "version_code"; old schema used "versionName".
        [JsonProperty("version_name")]
        public string? VersionName { get; set; }

        [JsonProperty("version_code")]
        public string? VersionCode { get; set; }
    }

    public async Task<string> GetBase64EncodedTokenAsync(CancellationToken cancellationToken = default)
    {
        const string url = "https://static.crunchyroll.com/vilos-v2/web/vilos/js/bundle.js";

        try
        {
            _logger?.LogInformation("Fetching client token from {Url}", url);
            var response = await _httpClient.Client.GetStringAsync(url, cancellationToken);

            var match = System.Text.RegularExpressions.Regex.Match(response, @"prod=""([\w-]+:[\w-]+)""");

            if (!match.Success)
            {
                _logger?.LogError("Token not found in JS bundle");
                return "";
            }

            var token = match.Groups[1].Value;
            var base64Token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token));

            _logger?.LogInformation("Successfully extracted client token");
            return base64Token;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch client token from JS bundle");
            return "";
        }
    }
}
