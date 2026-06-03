using System.Net;
using System.Net.Http.Headers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;

namespace Cruncharr.Core.Services;

public interface ICrunchyrollAuthService{
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
    Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, CancellationToken cancellationToken = default);
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

public class CrunchyrollAuthService : ICrunchyrollAuthService{
    private readonly ILogger<CrunchyrollAuthService>? _logger;
    private readonly HttpClientWrapper _httpClient;
    private readonly CrAuthSettings _authSettings;
    private readonly string _tokenFilePath;
    private readonly CruncharrConfig? _config;
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

    private static readonly CrAuthSettings DefaultAndroidTvAuthSettings = new(){
        Endpoint = "tv/android_tv",
        Authorization = "Basic bm1oaGcwbDZ4eXhjZm02aHQ2aGY6SjR6bU1mdjNkMVFkWHk4dDk2d1NjeDdoUnkzclBHLTM=",
        UserAgent = "ANDROIDTV/3.61.0_22341 Android/16",
        Device_name = "Android TV",
        Device_type = "Android TV",
        Video = true,
        Audio = true
    };

    private static readonly CrAuthSettings DefaultAndroidAuthSettings = new(){
        Endpoint = "android/phone",
        Authorization = "Basic Z24wdTU4dGNoMXRxaXZwNHlsbG46TXFoTlFpRnlHSEZKblNRYjZHTjlRQjhENVNTbUllVVQ=",
        UserAgent = "Crunchyroll/3.109.2 Android/16 okhttp/4.12.0",
        Device_name = "CPH2449",
        Device_type = "OnePlus CPH2449",
        Video = true,
        Audio = true
    };

    private const string EmbeddedAuthData = @"[{""type"":""tv"",""authorization"":""Basic bm1oaGcwbDZ4eXhjZm02aHQ2aGY6SjR6bU1mdjNkMVFkWHk4dDk2d1NjeDdoUnkzclBHLTM="",""versionName"":""3.61.0""},{""type"":""mobile"",""authorization"":""Basic Z24wdTU4dGNoMXRxaXZwNHlsbG46TXFoTlFpRnlHSEZKblNRYjZHTjlRQjhENVNTbUllVVQ="",""versionName"":""3.97.0""}]";

    public CrunchyrollAuthService(CruncharrConfig? config = null, ILogger<CrunchyrollAuthService>? logger = null){
        _logger = logger;
        _httpClient = new HttpClientWrapper(config);
        _authSettings = new CrAuthSettings();
        _config = config;
        
        var streamEndpointConfig = config?.Crunchyroll?.StreamEndpoint;
        var streamEndpointSecondaryConfig = config?.Crunchyroll?.StreamEndpointSecondary;
        
        StreamEndpoint = new CrAuthSettings();
        StreamEndpointSecondary = new CrAuthSettings();
        
        if (streamEndpointConfig != null){
            StreamEndpoint.Endpoint = !string.IsNullOrEmpty(streamEndpointConfig.Endpoint) ? streamEndpointConfig.Endpoint : DefaultAndroidTvAuthSettings.Endpoint;
            StreamEndpoint.Authorization = !string.IsNullOrEmpty(streamEndpointConfig.Authorization) ? streamEndpointConfig.Authorization : DefaultAndroidTvAuthSettings.Authorization;
            StreamEndpoint.UserAgent = !string.IsNullOrEmpty(streamEndpointConfig.UserAgent) ? streamEndpointConfig.UserAgent : DefaultAndroidTvAuthSettings.UserAgent;
            StreamEndpoint.Device_type = !string.IsNullOrEmpty(streamEndpointConfig.DeviceType) ? streamEndpointConfig.DeviceType : DefaultAndroidTvAuthSettings.Device_type;
            StreamEndpoint.Device_name = !string.IsNullOrEmpty(streamEndpointConfig.DeviceName) ? streamEndpointConfig.DeviceName : DefaultAndroidTvAuthSettings.Device_name;
            StreamEndpoint.Video = streamEndpointConfig.Video;
            StreamEndpoint.Audio = streamEndpointConfig.Audio;
            StreamEndpoint.UseDefault = streamEndpointConfig.UseDefault;
        } else {
            StreamEndpoint.Authorization = DefaultAndroidTvAuthSettings.Authorization;
            StreamEndpoint.UserAgent = DefaultAndroidTvAuthSettings.UserAgent;
            StreamEndpoint.Device_name = DefaultAndroidTvAuthSettings.Device_name;
            StreamEndpoint.Device_type = DefaultAndroidTvAuthSettings.Device_type;
            StreamEndpoint.Endpoint = DefaultAndroidTvAuthSettings.Endpoint;
            StreamEndpoint.Video = true;
            StreamEndpoint.Audio = true;
        }
        
        if (streamEndpointSecondaryConfig != null){
            StreamEndpointSecondary.Endpoint = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.Endpoint) ? streamEndpointSecondaryConfig.Endpoint : DefaultAndroidAuthSettings.Endpoint;
            StreamEndpointSecondary.Authorization = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.Authorization) ? streamEndpointSecondaryConfig.Authorization : DefaultAndroidAuthSettings.Authorization;
            StreamEndpointSecondary.UserAgent = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.UserAgent) ? streamEndpointSecondaryConfig.UserAgent : DefaultAndroidAuthSettings.UserAgent;
            StreamEndpointSecondary.Device_type = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.DeviceType) ? streamEndpointSecondaryConfig.DeviceType : DefaultAndroidAuthSettings.Device_type;
            StreamEndpointSecondary.Device_name = !string.IsNullOrEmpty(streamEndpointSecondaryConfig.DeviceName) ? streamEndpointSecondaryConfig.DeviceName : DefaultAndroidAuthSettings.Device_name;
            StreamEndpointSecondary.Video = streamEndpointSecondaryConfig.Video;
            StreamEndpointSecondary.Audio = streamEndpointSecondaryConfig.Audio;
            StreamEndpointSecondary.UseDefault = streamEndpointSecondaryConfig.UseDefault;
        } else {
            StreamEndpointSecondary.Authorization = DefaultAndroidAuthSettings.Authorization;
            StreamEndpointSecondary.UserAgent = DefaultAndroidAuthSettings.UserAgent;
            StreamEndpointSecondary.Device_name = DefaultAndroidAuthSettings.Device_name;
            StreamEndpointSecondary.Device_type = DefaultAndroidAuthSettings.Device_type;
            StreamEndpointSecondary.Endpoint = DefaultAndroidAuthSettings.Endpoint;
            StreamEndpointSecondary.Video = true;
            StreamEndpointSecondary.Audio = true;
        }
        
        _tokenFilePath = !string.IsNullOrEmpty(config?.TokenFilePath) ? config!.TokenFilePath : GetDefaultTokenPath();
        
        Init();
        LoadToken();
        
        // [PT] Lazy auth update - will be called on first token refresh instead of sync-over-async in constructor
    }
    
    private static string GetDefaultTokenPath(){
        // Use /config for Docker/container environments, fallback to AppData for desktop
        if (Directory.Exists("/config")){
            return "/config/token.json";
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cruncharr", "token.json");
    }
    
    public void Init(){
        Profile = new CrProfile{
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "en-US",
            HasPremium = false,
        };
    }
    
    private string GetTokenFilePath(){
        switch (StreamEndpoint.Endpoint){
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
    
    public async Task<bool> AuthenticateAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Authenticating with Crunchyroll...");
        
        if (File.Exists(GetTokenFilePath())){
            var content = File.ReadAllText(GetTokenFilePath());
            Token = JsonConvert.DeserializeObject<CrToken>(content);
            if (Token?.refresh_token != null){
                await LoginWithTokenAsync(useBetaApi, cancellationToken);
                return IsAuthenticated;
            }
        }
        
        await AuthAnonymousAsync(useBetaApi, cancellationToken);
        return false;
    }
    
    public async Task AuthAnonymousAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        string uuid = string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
        
        Subscription = new Subscription();
        
        var formData = new Dictionary<string, string>{
            { "grant_type", "client_id" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type },
        };
        
        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name)){
            formData.Add("device_name", StreamEndpoint.Device_name);
        }
        
        var requestContent = new FormUrlEncodedContent(formData);
        
        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
        } else{
            _logger?.LogError("Anonymous login failed: {Error}", error);
        }
        
        Profile = new CrProfile{
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "de-DE"
        };
    }
    
    // Alternative anonymous auth using Foxy endpoint (guest auth variation)
    public async Task AuthAnonymousFoxyAsync(bool useBetaApi, CancellationToken cancellationToken = default){
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in foxyAuthSettings){
            request.Headers.Add(header.Key, header.Value);
        }
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
        } else{
            _logger?.LogError("Anonymous Foxy login failed: {Error}", error);
        }
        
        Profile = new CrProfile{
            Username = "???",
            Avatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "de-DE"
        };
    }
    
    // Legacy method - delegates to UpdateAuthCredentialsAsync
    public async Task<bool> CheckStreamEndpointUpdateAsync(CancellationToken cancellationToken = default){
        return await UpdateAuthCredentialsAsync(cancellationToken);
    }
    
    // Fetches updated auth credentials from upstream data endpoint
    public async Task<bool> UpdateAuthCredentialsAsync(CancellationToken cancellationToken = default){
        const string dataUrl = "https://crunchy-dl.github.io/Crunchy-Downloader/data.json";
        const string fallbackUrl = "https://raw.githubusercontent.com/Crunchy-DL/Crunchy-Downloader/main/data.json";
        
        string? authResponse = null;
        
        // Try original URL first
        try{
            _logger?.LogInformation("Checking for auth credential updates from {Url}...", dataUrl);
            
            var request = new HttpRequestMessage(HttpMethod.Get, dataUrl);
            request.Headers.Add("User-Agent", "Cruncharr/1.0");
            
            var response = await _httpClient.Client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode){
                authResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            } else{
                _logger?.LogWarning("Failed to fetch auth data from primary URL: {Status}", response.StatusCode);
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to fetch auth data from primary URL");
        }
        
        // Try fallback GitHub raw URL
        if (authResponse == null){
            try{
                _logger?.LogInformation("Trying fallback URL {Url}...", fallbackUrl);
                
                var request = new HttpRequestMessage(HttpMethod.Get, fallbackUrl);
                request.Headers.Add("User-Agent", "Cruncharr/1.0");
                
                var response = await _httpClient.Client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode){
                    authResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                } else{
                    _logger?.LogWarning("Failed to fetch auth data from fallback URL: {Status}", response.StatusCode);
                }
            } catch (Exception ex){
                _logger?.LogWarning(ex, "Failed to fetch auth data from fallback URL");
            }
        }
        
        // Use embedded fallback data if all URLs fail
        if (authResponse == null){
            _logger?.LogInformation("Using embedded auth credentials fallback...");
            authResponse = EmbeddedAuthData;
        }
        
        try{
            var authEntries = JsonConvert.DeserializeObject<List<GhAuthEntry>>(authResponse);
            
            if (authEntries == null || authEntries.Count == 0){
                _logger?.LogWarning("No auth entries found in auth data");
                // Return true if current credentials are already set (non-empty)
                return !string.IsNullOrEmpty(StreamEndpoint.Authorization) && !string.IsNullOrEmpty(StreamEndpointSecondary.Authorization);
            }
            
            var ghAuthTv = authEntries.FirstOrDefault(e => e.Type?.Equals("tv", StringComparison.OrdinalIgnoreCase) == true);
            var ghAuthMobile = authEntries.FirstOrDefault(e => e.Type?.Equals("mobile", StringComparison.OrdinalIgnoreCase) == true);
            
            // Update TV endpoint
            if (StreamEndpoint.UseDefault && ghAuthTv != null &&
                !string.IsNullOrEmpty(ghAuthTv.Authorization) &&
                !string.IsNullOrEmpty(ghAuthTv.VersionName)){
                
                var currentVersion = ExtractClientVersion(StreamEndpoint.UserAgent);
                if (CompareVersions(ghAuthTv.VersionName, currentVersion) > 0){
                    _logger?.LogInformation("Updating TV auth from version {Old} to {New}", currentVersion, ghAuthTv.VersionName);
                    StreamEndpoint.Authorization = ghAuthTv.Authorization;
                    StreamEndpoint.UserAgent = $"ANDROIDTV/{ghAuthTv.VersionName} Android/16";
                }
            }
            
            // Update Mobile endpoint
            if (StreamEndpointSecondary.UseDefault && ghAuthMobile != null &&
                !string.IsNullOrEmpty(ghAuthMobile.Authorization) &&
                !string.IsNullOrEmpty(ghAuthMobile.VersionName)){
                
                var currentVersion = ExtractClientVersion(StreamEndpointSecondary.UserAgent);
                if (CompareVersions(ghAuthMobile.VersionName, currentVersion) > 0){
                    _logger?.LogInformation("Updating Mobile auth from version {Old} to {New}", currentVersion, ghAuthMobile.VersionName);
                    StreamEndpointSecondary.Authorization = ghAuthMobile.Authorization;
                    StreamEndpointSecondary.UserAgent = $"Crunchyroll/{ghAuthMobile.VersionName} Android/16 okhttp/4.12.0";
                }
            }
            
            return true;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse auth credentials");
            // Return true if current credentials are already set (non-empty)
            return !string.IsNullOrEmpty(StreamEndpoint.Authorization) && !string.IsNullOrEmpty(StreamEndpointSecondary.Authorization);
        }
    }
    
    private static string ExtractClientVersion(string userAgent){
        if (string.IsNullOrEmpty(userAgent)) return "0.0.0";
        
        // Extract version from strings like "ANDROIDTV/3.59.0 Android/16" or "Crunchyroll/3.97.0 Android/16"
        var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"[\w]+/(\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : "0.0.0";
    }
    
    private static int CompareVersions(string versionA, string versionB){
        try{
            var partsA = versionA.Split('.');
            var partsB = versionB.Split('.');
            
            for (int i = 0; i < Math.Max(partsA.Length, partsB.Length); i++){
                int a = i < partsA.Length && int.TryParse(partsA[i], out int pa) ? pa : 0;
                int b = i < partsB.Length && int.TryParse(partsB[i], out int pb) ? pb : 0;
                
                if (a > b) return 1;
                if (a < b) return -1;
            }
            
            return 0;
        } catch{
            return 0;
        }
    }
    
    public async Task<bool> LoginAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Logging in as {Email} (BetaAPI: {UseBeta})", email, useBetaApi);
        
        string uuid = Guid.NewGuid().ToString();
        
        var formData = new Dictionary<string, string>{
            { "username", email },
            { "password", password },
            { "grant_type", "password" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", StreamEndpoint.Device_type }
        };
        
        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name)){
            formData.Add("device_name", StreamEndpoint.Device_name);
        }
        
        var requestContent = new FormUrlEncodedContent(formData);
        
        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        _logger?.LogDebug("Login request to {Url} with auth: {Auth}", ApiUrls.Auth, StreamEndpoint.Authorization.Substring(0, 20) + "...");
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, suppressError: false, attachCookies: false);
        
        _logger?.LogDebug("Login response: IsOk={IsOk}, Error={Error}, Content={Content}", isOk, error, content?.Substring(0, Math.Min(500, content?.Length ?? 0)));
        
        if (isOk && content != null){
            JsonTokenToFileAndVariable(content, uuid);
            _logger?.LogInformation("Login successful, token received");
        } else{
            _logger?.LogError("Login failed: HTTP Error={Error}, Response={Response}", error, content);
            
            // If client is inactive, try to update auth credentials and retry once
            if (content?.Contains("client_inactive") == true || content?.Contains("invalid_client") == true){
                _logger?.LogWarning("Auth client inactive, attempting to fetch updated credentials...");
                var updated = await UpdateAuthCredentialsAsync(cancellationToken);
                
                if (updated){
                    _logger?.LogInformation("Auth credentials updated, retrying login...");
                    // Retry login with updated credentials
                    crunchyAuthHeaders["Authorization"] = StreamEndpoint.Authorization;
                    crunchyAuthHeaders["User-Agent"] = StreamEndpoint.UserAgent;
                    
                    request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
                        Content = requestContent
                    };
                    
                    foreach (var header in crunchyAuthHeaders){
                        request.Headers.Add(header.Key, header.Value);
                    }
                    
                    var (retryIsOk, retryContent, retryError) = await _httpClient.SendRequestAsync(request, suppressError: false, attachCookies: false);
                    
                    if (retryIsOk){
                        JsonTokenToFileAndVariable(retryContent, uuid);
                        _logger?.LogInformation("Login successful after auth update");
                        isOk = true;
                    } else{
                        content = retryContent;
                        error = retryError;
                    }
                }
            }
            
            if (!isOk){
                string errorMessage;
                if (string.IsNullOrEmpty(content)){
                    errorMessage = $"Login failed: {error}";
                } else if (content.Contains("invalid_credentials")){
                    errorMessage = "Invalid credentials - please check your email and password";
                    _logger?.LogError("Invalid credentials");
                } else if (content.Contains("<title>Just a moment...</title>") ||
                           content.Contains("<title>Access denied</title>") ||
                           content.Contains("<title>Attention Required! | Cloudflare</title>") ||
                           content.Trim().Equals("error code: 1020") ||
                           content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1){
                    errorMessage = "Cloudflare/DDOS protection detected - try enabling Beta API in settings";
                    _logger?.LogError("Cloudflare/DDOS protection detected during login");
                } else if (content.Contains("client_inactive")){
                    errorMessage = "Login failed: Crunchyroll has deactivated this client. Please check for application updates.";
                    _logger?.LogError("Client deactivated by Crunchyroll");
                } else{
                    var responsePreview = content.Substring(0, Math.Min(500, content.Length));
                    errorMessage = $"Login failed: {responsePreview}";
                    _logger?.LogError("Login error response: {Response}", responsePreview);
                }
                throw new Exception(errorMessage);
            }
        }
        
        if (Token?.refresh_token != null){
            SetETPCookie(Token.refresh_token);
            SaveToken();
            await GetMultiProfileAsync(useBetaApi, cancellationToken);
            return true;
        }
        
        throw new Exception("Login failed - no refresh token received from Crunchyroll");
    }
    
    public async Task<bool> LoginWithTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.refresh_token == null){
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
        
        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name)){
            formData.Add("device_name", StreamEndpoint.Device_name);
        }
        
        var requestContent = new FormUrlEncodedContent(formData);
        
        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        SetETPCookie(Token.refresh_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (content.Contains("<title>Just a moment...</title>") ||
            content.Contains("<title>Access denied</title>") ||
            content.Contains("<title>Attention Required! | Cloudflare</title>") ||
            content.Trim().Equals("error code: 1020") ||
            content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1){
            _logger?.LogError("Cloudflare error during token login");
        }
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                SaveToken();
                await GetMultiProfileAsync(useBetaApi, cancellationToken);
                return true;
            }
        } else{
            _logger?.LogError("Token Auth Failed: {Error}", error);
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
        }
        
        return false;
    }
    
    public Task LogoutAsync(){
        Token = null;
        Init();
        DeleteToken();
        return Task.CompletedTask;
    }
    
    public async Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (EndpointEnum == CrunchyrollEndpoints.Guest){
            if (!IsTokenExpiredOrNearExpiry()){
                return true;
            }
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return true;
        }
        
        if (Token?.access_token == null && Token?.refresh_token == null ||
            Token?.access_token != null && Token?.refresh_token == null){
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return false;
        } else{
            if (!IsTokenExpiredOrNearExpiry()){
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
        
        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name)){
            formData.Add("device_name", StreamEndpoint.Device_name);
        }
        
        var requestContent = new FormUrlEncodedContent(formData);
        
        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        SetETPCookie(Token?.refresh_token ?? string.Empty);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            return true;
        } else{
            _logger?.LogError("Refresh Token Auth Failed: {Error}", error);
            if (hadUserSession){
                _logger?.LogWarning("User session expired - login required");
            }
            return false;
        }
    }
    
    public async Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null){
            _logger?.LogWarning("Missing Access Token for multi-profile");
            return;
        }
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.MultiProfile, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (isOk){
            var multiProfile = JsonConvert.DeserializeObject<CrMultiProfile>(content);
            if (multiProfile != null){
                MultiProfile = multiProfile;
                _logger?.LogInformation("Loaded {Count} profiles", MultiProfile.Profiles.Count);
                
                var selectedProfile = MultiProfile.Profiles.FirstOrDefault(e => e.IsSelected);
                if (selectedProfile != null){
                    Profile = selectedProfile;
                }
                
                await GetSubscriptionAsync(useBetaApi, cancellationToken);
            }
        } else{
            _logger?.LogError("Failed to get multi-profile: {Error}", error);
        }
    }
    
    public async Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null && Token?.refresh_token == null ||
            Token?.access_token != null && Token?.refresh_token == null){
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
        }
        
        // Verify token was obtained after anonymous auth fallback
        if (Token == null){
            _logger?.LogError("ChangeProfileAsync failed: Token is null after AuthAnonymousAsync");
            return false;
        }
        
        if (Profile.Username == "???"){
            return false;
        }
        
        if (string.IsNullOrEmpty(profileId) || Token?.refresh_token == null){
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
        
        if (!string.IsNullOrEmpty(StreamEndpoint.Device_name)){
            formData.Add("device_name", StreamEndpoint.Device_name);
        }
        
        var requestContent = new FormUrlEncodedContent(formData);
        
        var crunchyAuthHeaders = new Dictionary<string, string>{
            { "Authorization", StreamEndpoint.Authorization },
            { "User-Agent", StreamEndpoint.UserAgent }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        if (Token?.refresh_token != null){
            SetETPCookie(Token.refresh_token);
        }
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                SaveToken();
            }
            
            await GetMultiProfileAsync(useBetaApi, cancellationToken);
            return true;
        } else{
            _logger?.LogError("Change profile failed: {Error}", error);
        }
        
        return false;
    }
    
    private async Task GetProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null){
            _logger?.LogWarning("Missing Access Token");
            return;
        }
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Profile, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (isOk){
            var profileTemp = JsonConvert.DeserializeObject<CrProfile>(content);
            if (profileTemp != null){
                Profile = profileTemp;
                _logger?.LogInformation("Logged in as {Username}", Profile.Username);
                await GetSubscriptionAsync(useBetaApi, cancellationToken);
            }
        } else{
            _logger?.LogError("Failed to get profile: {Error}", error);
        }
    }
    
    private async Task GetSubscriptionAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null || Token?.account_id == null){
            _logger?.LogWarning("Missing access token or account ID for subscription check");
            return;
        }
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Subscription + Token.account_id, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (isOk){
            var subsc = JsonConvert.DeserializeObject<Subscription>(content);
            Subscription = subsc;
            
            if (subsc is{ SubscriptionProducts:{ Count: 0 }, ThirdPartySubscriptionProducts:{ Count: > 0 } }){
                var thirdPartySub = subsc.ThirdPartySubscriptionProducts.First();
                var expiration = thirdPartySub.InGrace ? thirdPartySub.InGraceExpirationDate : thirdPartySub.ExpirationDate;
                var remaining = expiration - DateTime.Now;
                Profile.HasPremium = true;
                if (Subscription != null){
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = expiration;
                }
            } else if (subsc is{ SubscriptionProducts:{ Count: 0 }, NonrecurringSubscriptionProducts:{ Count: > 0 } }){
                var nonRecurringSub = subsc.NonrecurringSubscriptionProducts.First();
                var remaining = nonRecurringSub.EndDate - DateTime.Now;
                Profile.HasPremium = true;
                if (Subscription != null){
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = nonRecurringSub.EndDate;
                }
            } else if (subsc is{ SubscriptionProducts:{ Count: 0 }, FunimationSubscriptions:{ Count: > 0 } }){
                Profile.HasPremium = true;
            } else if (subsc is{ SubscriptionProducts.Count: > 0 }){
                Profile.HasPremium = true;
            } else{
                Profile.HasPremium = false;
                _logger?.LogWarning("No subscription available: {Subscription}", JsonConvert.SerializeObject(subsc, Formatting.Indented));
            }
        } else{
            Profile.HasPremium = false;
            _logger?.LogError("Failed to check premium subscription status: {Error}", error);
        }
    }
    
    private void SetETPCookie(string refreshToken){
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("etp_rt", refreshToken));
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("c_locale", "en-US"));
    }
    
    private void JsonTokenToFileAndVariable(string content, string deviceId){
        Token = JsonConvert.DeserializeObject<CrToken>(content);
        
        if (Token is{ expires_in: not null }){
            Token.device_id = deviceId;
            Token.expires = DateTime.Now.AddSeconds((double)Token.expires_in);
            
            if (EndpointEnum == CrunchyrollEndpoints.Guest){
                return;
            }
            
            SaveToken();
        }
    }
    
    private bool IsTokenExpiredOrNearExpiry(){
        return Token == null || DateTime.Now >= Token.expires - TokenRefreshBuffer;
    }

    public void LoadToken(){
        try{
            var tokenFile = GetTokenFilePath();
            if (File.Exists(tokenFile)){
                var content = File.ReadAllText(tokenFile);
                Token = JsonConvert.DeserializeObject<CrToken>(content);
                if (Token != null && Token.refresh_token != null){
                    SetETPCookie(Token.refresh_token);
                    _logger?.LogInformation("Loaded token from {Path}", tokenFile);
                }
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to load token from file");
        }
    }

    public void SaveToken(){
        try{
            if (Token != null){
                var tokenFile = GetTokenFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
                File.WriteAllText(tokenFile, JsonConvert.SerializeObject(Token, Formatting.Indented));
                _logger?.LogDebug("Saved token to {Path}", tokenFile);
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to save token to file");
        }
    }

    public void DeleteToken(){
        try{
            var tokenFile = GetTokenFilePath();
            if (File.Exists(tokenFile)){
                File.Delete(tokenFile);
                _logger?.LogInformation("Deleted token from {Path}", tokenFile);
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to delete token from file");
        }
    }
    
    // Ported from upstream CrunchyrollManager.GetBase64EncodedTokenAsync
    // Fetches and extracts the client token from Crunchyroll's JS bundle
    // Simple model for GitHub release API response
    private class GitHubRelease{
        [JsonProperty("tag_name")]
        public string? TagName { get; set; }
        
        [JsonProperty("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }
    
    private class GitHubAsset{
        [JsonProperty("name")]
        public string? Name { get; set; }
        
        [JsonProperty("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
    
    private class GhAuthEntry{
        [JsonProperty("type")]
        public string? Type { get; set; }
        
        [JsonProperty("authorization")]
        public string? Authorization { get; set; }
        
        [JsonProperty("versionName")]
        public string? VersionName { get; set; }
    }

    public async Task<string> GetBase64EncodedTokenAsync(CancellationToken cancellationToken = default){
        const string url = "https://static.crunchyroll.com/vilos-v2/web/vilos/js/bundle.js";
        
        try{
            _logger?.LogInformation("Fetching client token from {Url}", url);
            var response = await _httpClient.Client.GetStringAsync(url, cancellationToken);
            
            var match = System.Text.RegularExpressions.Regex.Match(response, @"prod=""([\w-]+:[\w-]+)""");
            
            if (!match.Success){
                _logger?.LogError("Token not found in JS bundle");
                return "";
            }
            
            var token = match.Groups[1].Value;
            var base64Token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token));
            
            _logger?.LogInformation("Successfully extracted client token");
            return base64Token;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to fetch client token from JS bundle");
            return "";
        }
    }
}
