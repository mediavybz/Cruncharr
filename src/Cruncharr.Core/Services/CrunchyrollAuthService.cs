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
    Task<bool> AuthenticateAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(string email, string password, bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> LoginWithTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task LogoutAsync();
    Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default);
    Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, CancellationToken cancellationToken = default);
    void LoadToken();
    void SaveToken();
    void DeleteToken();
}

public class CrunchyrollAuthService : ICrunchyrollAuthService{
    private readonly ILogger<CrunchyrollAuthService>? _logger;
    private readonly HttpClientWrapper _httpClient;
    private readonly CrAuthSettings _authSettings;
    private readonly string _tokenFilePath;
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(60);

    public CrToken? Token { get; private set; }
    public CrProfile Profile { get; private set; } = new();
    public CrMultiProfile MultiProfile { get; private set; } = new();
    public Subscription? Subscription { get; private set; }
    public bool IsAuthenticated => Token?.access_token != null;
    public HttpClientWrapper HttpClient => _httpClient;

    public CrunchyrollAuthService(CruncharrConfig? config = null, ILogger<CrunchyrollAuthService>? logger = null){
        _logger = logger;
        _httpClient = new HttpClientWrapper();
        _authSettings = new CrAuthSettings();
        _tokenFilePath = config?.TokenFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cruncharr", "token.json");
        Profile = new CrProfile{
            Username = "???",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "en-US",
            HasPremium = false
        };
        LoadToken();
    }
    
    public async Task<bool> AuthenticateAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Authenticating with Crunchyroll...");
        
        if (Token != null && !IsTokenExpiredOrNearExpiry()){
            _logger?.LogInformation("Token still valid, using existing token");
            return true;
        }
        
        await AuthAnonymousAsync(useBetaApi, cancellationToken);
        return IsAuthenticated;
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
            { "device_type", _authSettings.Device_type }
        };
        
        if (!string.IsNullOrEmpty(_authSettings.Device_name)){
            formData.Add("device_name", _authSettings.Device_name);
        }
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = new FormUrlEncodedContent(formData)
        };
        
        request.Headers.Add("Authorization", _authSettings.Authorization);
        request.Headers.Add("User-Agent", _authSettings.UserAgent);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                SaveToken();
                await GetMultiProfileAsync(useBetaApi, cancellationToken);
                return true;
            }
        } else{
            _logger?.LogError("Login failed: {Error}", error);
            if (content.Contains("invalid_credentials")){
                _logger?.LogError("Invalid credentials");
            } else if (CheckForCloudflare(content)){
                _logger?.LogError("Cloudflare/DDOS protection detected during login");
            }
        }
        
        return false;
    }
    
    public Task LogoutAsync(){
        Token = null;
        Profile = new CrProfile{
            Username = "???",
            PreferredContentAudioLanguage = "ja-JP",
            PreferredContentSubtitleLanguage = "en-US",
            HasPremium = false
        };
        DeleteToken();
        return Task.CompletedTask;
    }
    
    public async Task<bool> RefreshTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.refresh_token == null){
            _logger?.LogWarning("No refresh token available");
            return false;
        }
        
        if (!IsTokenExpiredOrNearExpiry()){
            return true;
        }
        
        string uuid = string.IsNullOrEmpty(Token.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
        
        var formData = new Dictionary<string, string>{
            { "refresh_token", Token.refresh_token },
            { "grant_type", "refresh_token" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", _authSettings.Device_type }
        };
        
        if (!string.IsNullOrEmpty(_authSettings.Device_name)){
            formData.Add("device_name", _authSettings.Device_name);
        }
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = new FormUrlEncodedContent(formData)
        };
        
        request.Headers.Add("Authorization", _authSettings.Authorization);
        request.Headers.Add("User-Agent", _authSettings.UserAgent);
        
        SetETPCookie(Token.refresh_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                SaveToken();
                await GetProfileAsync(useBetaApi, cancellationToken);
            }
            return true;
        } else{
            _logger?.LogError("Token refresh failed: {Error}", error);
            return false;
        }
    }
    
    private async Task AuthAnonymousAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        string uuid = string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
        
        var formData = new Dictionary<string, string>{
            { "grant_type", "client_id" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", _authSettings.Device_type }
        };
        
        if (!string.IsNullOrEmpty(_authSettings.Device_name)){
            formData.Add("device_name", _authSettings.Device_name);
        }
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = new FormUrlEncodedContent(formData)
        };
        
        request.Headers.Add("Authorization", _authSettings.Authorization);
        request.Headers.Add("User-Agent", _authSettings.UserAgent);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            _logger?.LogInformation("Anonymous authentication successful");
        } else{
            _logger?.LogError("Anonymous authentication failed: {Error}", error);
        }
    }
    
    private async Task GetProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null) return;
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Profile(useBetaApi), HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (isOk){
            var profile = JsonConvert.DeserializeObject<CrProfile>(content);
            if (profile != null){
                Profile = profile;
                _logger?.LogInformation("Logged in as {Username}", Profile.Username);
                await GetSubscriptionAsync(useBetaApi, cancellationToken);
            }
        }
    }
    
    private async Task GetSubscriptionAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null || Token?.account_id == null) return;
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.Subscription(useBetaApi) + Token.account_id, HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
        
        if (isOk){
            var sub = JsonConvert.DeserializeObject<Subscription>(content);
            if (sub != null){
                Subscription = sub;
                
                // Check third-party subscriptions (e.g., Apple, Google, Roku)
                if (sub.ThirdPartySubscriptionProducts?.Count > 0){
                    var thirdPartySub = sub.ThirdPartySubscriptionProducts.First();
                    var expiration = thirdPartySub.InGrace 
                        ? thirdPartySub.InGraceExpirationDate 
                        : thirdPartySub.ExpirationDate;
                    var remaining = expiration - DateTime.Now;
                    Profile.HasPremium = true;
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = expiration;
                }
                // Check non-recurring subscriptions (e.g., gift cards)
                else if (sub.NonrecurringSubscriptionProducts?.Count > 0){
                    var nonRecurringSub = sub.NonrecurringSubscriptionProducts.First();
                    var remaining = nonRecurringSub.EndDate - DateTime.Now;
                    Profile.HasPremium = true;
                    Subscription.IsActive = remaining > TimeSpan.Zero;
                    Subscription.NextRenewalDate = nonRecurringSub.EndDate;
                }
                // Check Funimation migration subscriptions
                else if (sub.FunimationSubscriptions?.Count > 0){
                    Profile.HasPremium = true;
                }
                // Check direct Crunchyroll subscriptions
                else if (sub.SubscriptionProducts?.Count > 0){
                    var directSub = sub.SubscriptionProducts.First();
                    Profile.HasPremium = !directSub.IsCancelled;
                    Subscription.IsActive = Profile.HasPremium;
                    Subscription.NextRenewalDate = directSub.EffectiveDate;
                }
                else{
                    Profile.HasPremium = false;
                    _logger?.LogWarning("No subscription available for account {AccountId}", sub.AccountId);
                }
                
                _logger?.LogInformation("Premium status: {HasPremium}, Active: {IsActive}, Next renewal: {NextRenewal}", 
                    Profile.HasPremium, Subscription.IsActive, Subscription.NextRenewalDate);
            }
        } else{
            Profile.HasPremium = false;
            _logger?.LogError("Failed to check premium subscription status: {Error}", error);
        }
    }
    
    public async Task<bool> LoginWithTokenAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.refresh_token == null){
            _logger?.LogWarning("No refresh token available for login with token");
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
            return false;
        }
        
        string uuid = string.IsNullOrEmpty(Token.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
        
        var formData = new Dictionary<string, string>{
            { "refresh_token", Token.refresh_token },
            { "grant_type", "refresh_token" },
            { "scope", "offline_access" },
            { "device_id", uuid },
            { "device_type", _authSettings.Device_type }
        };
        
        if (!string.IsNullOrEmpty(_authSettings.Device_name)){
            formData.Add("device_name", _authSettings.Device_name);
        }
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = new FormUrlEncodedContent(formData)
        };
        
        request.Headers.Add("Authorization", _authSettings.Authorization);
        request.Headers.Add("User-Agent", _authSettings.UserAgent);
        
        SetETPCookie(Token.refresh_token);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (CheckForCloudflare(content)){
            _logger?.LogError("Cloudflare/DDOS protection detected during token login");
            return false;
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
            _logger?.LogError("Token login failed: {Error}", error);
            await AuthAnonymousAsync(useBetaApi, cancellationToken);
        }
        
        return false;
    }
    
    public async Task GetMultiProfileAsync(bool useBetaApi, CancellationToken cancellationToken = default){
        if (Token?.access_token == null){
            _logger?.LogWarning("Missing access token for multi-profile");
            return;
        }
        
        var request = HttpClientWrapper.CreateRequest(ApiUrls.MultiProfile(useBetaApi), HttpMethod.Get, true, Token.access_token);
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            var multiProfile = JsonConvert.DeserializeObject<CrMultiProfile>(content);
            if (multiProfile != null){
                MultiProfile = multiProfile;
                var selectedProfile = MultiProfile.Profiles.FirstOrDefault(p => p.IsSelected);
                if (selectedProfile != null){
                    Profile = selectedProfile;
                    _logger?.LogInformation("Using profile: {ProfileName}", Profile.ProfileName ?? Profile.Username);
                }
                await GetSubscriptionAsync(useBetaApi, cancellationToken);
            }
        } else{
            _logger?.LogError("Failed to get multi-profile: {Error}", error);
        }
    }
    
    public async Task<bool> ChangeProfileAsync(string profileId, bool useBetaApi, CancellationToken cancellationToken = default){
        if (string.IsNullOrEmpty(profileId) || Token?.refresh_token == null){
            _logger?.LogWarning("Cannot change profile: missing profileId or refresh token");
            return false;
        }
        
        string uuid = string.IsNullOrEmpty(Token.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
        
        SetETPCookie(Token.refresh_token);
        
        var formData = new Dictionary<string, string>{
            { "grant_type", "refresh_token_profile_id" },
            { "profile_id", profileId },
            { "device_id", uuid },
            { "device_type", _authSettings.Device_type }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Auth(useBetaApi)){
            Content = new FormUrlEncodedContent(formData)
        };
        
        request.Headers.Add("Authorization", _authSettings.Authorization);
        request.Headers.Add("User-Agent", _authSettings.UserAgent);
        
        var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);
        
        if (isOk){
            JsonTokenToFileAndVariable(content, uuid);
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                SaveToken();
                await GetMultiProfileAsync(useBetaApi, cancellationToken);
                return true;
            }
        } else{
            _logger?.LogError("Profile change failed: {Error}", error);
        }
        
        return false;
    }
    
    private bool CheckForCloudflare(string content){
        if (string.IsNullOrEmpty(content)) return false;
        
        return content.Contains("\u003ctitle\u003eJust a moment...\u003c/title\u003e") ||
               content.Contains("\u003ctitle\u003eAccess denied\u003c/title\u003e") ||
               content.Contains("\u003ctitle\u003eAttention Required! | Cloudflare\u003c/title\u003e") ||
               content.Trim().Equals("error code: 1020") ||
               content.IndexOf("\u003ctitle\u003eDDOS-GUARD\u003c/title\u003e", StringComparison.OrdinalIgnoreCase) > -1;
    }
    
    private void SetETPCookie(string refreshToken){
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("etp_rt", refreshToken));
        _httpClient.AddCookie(".crunchyroll.com", new Cookie("c_locale", "en-US"));
    }
    
    private void JsonTokenToFileAndVariable(string content, string deviceId){
        Token = JsonConvert.DeserializeObject<CrToken>(content);
        if (Token?.expires_in != null){
            Token.device_id = deviceId;
            Token.expires = DateTime.Now.AddSeconds((double)Token.expires_in);
        }
    }
    
    private bool IsTokenExpiredOrNearExpiry(){
        return Token == null || DateTime.Now >= Token.expires - TokenRefreshBuffer;
    }

    public void LoadToken(){
        try{
            if (File.Exists(_tokenFilePath)){
                var content = File.ReadAllText(_tokenFilePath);
                Token = JsonConvert.DeserializeObject<CrToken>(content);
                if (Token != null && Token.refresh_token != null){
                    SetETPCookie(Token.refresh_token);
                    _logger?.LogInformation("Loaded token from {Path}", _tokenFilePath);
                }
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to load token from {Path}", _tokenFilePath);
        }
    }

    public void SaveToken(){
        try{
            if (Token != null){
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenFilePath)!);
                File.WriteAllText(_tokenFilePath, JsonConvert.SerializeObject(Token, Formatting.Indented));
                _logger?.LogDebug("Saved token to {Path}", _tokenFilePath);
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to save token to {Path}", _tokenFilePath);
        }
    }

    public void DeleteToken(){
        try{
            if (File.Exists(_tokenFilePath)){
                File.Delete(_tokenFilePath);
                _logger?.LogInformation("Deleted token from {Path}", _tokenFilePath);
            }
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to delete token from {Path}", _tokenFilePath);
        }
    }
}
