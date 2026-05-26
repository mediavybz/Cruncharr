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
        Authorization = "Basic eTJhcnZqYjBoMHJndnRpemxvdnk6SlZMdndkSXBYdnhVLXFJQnZUMU04b1FUcjFxbFFKWDI=",
        UserAgent = "ANDROIDTV/3.59.0 Android/16",
        Device_name = "Android TV",
        Device_type = "Android TV",
        Video = true,
        Audio = true
    };

    private static readonly CrAuthSettings DefaultAndroidAuthSettings = new(){
        Endpoint = "android/phone",
        Authorization = "Basic bzJhNndsamdub3FtdjloMWJ5bHI6Ujk3S3ExZm5faExZVFk0bDJxTjJIT2lDQnpfYnpBSUU=",
        UserAgent = "Crunchyroll/3.97.0 Android/16 okhttp/4.12.0",
        Device_name = "CPH2449",
        Device_type = "OnePlus CPH2449",
        Video = true,
        Audio = true
    };

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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
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
    
    // Checks GitHub releases for newer auth endpoint versions
    public async Task<bool> CheckStreamEndpointUpdateAsync(CancellationToken cancellationToken = default){
        const string releasesUrl = "https://api.github.com/repos/Crunchy-DL/Crunchy-Downloader/releases/latest";
        
        try{
            _logger?.LogInformation("Checking for stream endpoint updates from GitHub releases...");
            
            var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            request.Headers.Add("User-Agent", "Cruncharr/1.0");
            
            var response = await _httpClient.Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode){
                _logger?.LogWarning("Failed to check GitHub releases: {Status}", response.StatusCode);
                return false;
            }
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonConvert.DeserializeObject<GitHubRelease>(content);
            
            if (release?.TagName != null){
                _logger?.LogInformation("Latest upstream release: {Tag}", release.TagName);
                // In a full implementation, this would compare versions and update endpoints
                // For now, just log that an update is available
                return true;
            }
            
            return false;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to check for stream endpoint updates");
            return false;
        }
    }
    
    public async Task<bool> LoginAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Logging in as {Email}", email);
        
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = requestContent
        };
        
        foreach (var header in crunchyAuthHeaders){
            request.Headers.Add(header.Key, header.Value);
        }
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
        } else{
            _logger?.LogError("Login failed: {Error}", error);
            string errorMessage;
            if (content.Contains("invalid_credentials")){
                errorMessage = "Invalid credentials - please check your email and password";
                _logger?.LogError("Invalid credentials");
            } else if (content.Contains("<title>Just a moment...</title>") ||
                       content.Contains("<title>Access denied</title>") ||
                       content.Contains("<title>Attention Required! | Cloudflare</title>") ||
                       content.Trim().Equals("error code: 1020") ||
                       content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1){
                errorMessage = "Cloudflare/DDOS protection detected - try enabling Beta API in settings";
                _logger?.LogError("Cloudflare/DDOS protection detected during login");
            } else{
                var responsePreview = content.Substring(0, content.Length < 200 ? content.Length : 200);
                errorMessage = $"Login failed: {responsePreview}";
                _logger?.LogError("Login error response: {Response}", responsePreview);
            }
            throw new Exception(errorMessage);
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
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
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.MultiProfile(useBetaApi), HttpMethod.Get, true, Token.access_token);
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
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
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
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Profile(useBetaApi), HttpMethod.Get, true, Token.access_token);
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
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Subscription(useBetaApi) + Token.account_id, HttpMethod.Get, true, Token.access_token);
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
