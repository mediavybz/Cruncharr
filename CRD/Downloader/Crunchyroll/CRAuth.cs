using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using CRD.Utils;
using CRD.Utils.Files;
using CRD.Utils.Http;
using CRD.Utils.Notifications;
using CRD.Utils.Structs;
using CRD.Utils.Structs.Crunchyroll;
using CRD.Views;
using Newtonsoft.Json;
using ReactiveUI;

namespace CRD.Downloader.Crunchyroll;

public class CrAuth(CrunchyrollManager crunInstance, CrAuthSettings authSettings){
    private const string UnknownUsername = "???";
    private const string DefaultAvatar = "crbrand_avatars_logo_marks_mangagirl_taupe.png";
    private const string CrunchyrollDomain = ".crunchyroll.com";
    private const string SsoDomain = "sso.crunchyroll.com";
    private const string DefaultAudioLanguage = "ja-JP";
    private const string DefaultAnonymousSubtitleLanguage = "de-DE";

    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(60);

    public CrToken? Token;
    public CrProfile Profile = new();
    public Subscription? Subscription{ get; set; }
    public CrMultiProfile MultiProfile = new();

    public CrunchyrollEndpoints EndpointEnum = CrunchyrollEndpoints.Unknown;

    public CrAuthSettings AuthSettings = authSettings;

    public Dictionary<string, CookieCollection> cookieStore = new();

    private string authCodeVerifier = string.Empty;
    private string authCode = string.Empty;

    private bool IsTokenExpiredOrNearExpiry(){
        return Token == null || DateTime.Now >= Token.expires - TokenRefreshBuffer;
    }

    public void Init(){
        Profile = CreateDefaultProfile(crunInstance.DefaultLocale, false);
    }

    private static CrProfile CreateDefaultProfile(string subtitleLanguage, bool hasPremium = false){
        return new CrProfile{
            Username = UnknownUsername,
            Avatar = DefaultAvatar,
            PreferredContentAudioLanguage = DefaultAudioLanguage,
            PreferredContentSubtitleLanguage = subtitleLanguage,
            HasPremium = hasPremium,
        };
    }

    private string GetTokenFilePath(){
        return AuthSettings.Endpoint switch{
            "tv/samsung" or "tv/vidaa" or "tv/android_tv" => CfgManager.PathCrToken.Replace(".json", "_tv.json"),
            "android/phone" or "android/tablet" => CfgManager.PathCrToken.Replace(".json", "_android.json"),
            "console/switch" or "console/ps4" or "console/ps5" or "console/xbox_one" => CfgManager.PathCrToken.Replace(".json", "_console.json"),
            "---" => CfgManager.PathCrToken.Replace(".json", "_guest.json"),
            _ => CfgManager.PathCrToken
        };
    }

    public async Task Auth(){
        var tokenFilePath = GetTokenFilePath();
        if (CfgManager.CheckIfFileExists(tokenFilePath)){
            Token = CfgManager.ReadJsonFromFile<CrToken>(tokenFilePath);
            await LoginWithToken();
        } else{
            await AuthAnonymous();
        }
    }

    public async Task Auth(AuthData authData){
        cookieStore.Clear();
        if (AuthSettings.Endpoint.StartsWith("tv")){
            await AuthOld(authData);
        } else{
            await AuthCode(authData);
        }
    }

    public void SetETPCookie(string refreshToken){
        HttpClientReq.Instance.AddCookie(CrunchyrollDomain, new Cookie("etp_rt", refreshToken), cookieStore);
        HttpClientReq.Instance.AddCookie(CrunchyrollDomain, new Cookie("c_locale", "en-US"), cookieStore);
    }

    public Task AuthAnonymous(){
        return AuthAnonymousInternal(true);
    }

    public Task AuthAnonymousFoxy(){
        return AuthAnonymousInternal(false);
    }

    private async Task AuthAnonymousInternal(bool includeDeviceMetadata){
        var uuid = ResolveDeviceId();

        var formData = new Dictionary<string, string>{
            { "grant_type", "client_id" },
        };

        if (includeDeviceMetadata){
            Subscription = new Subscription();
            formData["scope"] = "offline_access";
            AddDeviceMetadata(formData, uuid);
        }

        var request = CreateTokenRequest(formData);
        var response = await HttpClientReq.Instance.SendHttpRequest(request);

        if (response.IsOk){
            JsonTokenToFileAndVariable(response.ResponseContent, uuid);
        } else{
            Console.Error.WriteLine("Anonymous login failed");
        }

        Profile = CreateDefaultProfile(DefaultAnonymousSubtitleLanguage);
    }

    private void JsonTokenToFileAndVariable(string content, string deviceId){
        Token = Helpers.Deserialize<CrToken>(content, crunInstance.SettingsJsonSerializerSettings);

        if (Token is not{ expires_in: not null }){
            return;
        }

        Token.device_id = deviceId;
        Token.expires = DateTime.Now.AddSeconds((double)Token.expires_in);
        NotificationPublisher.Instance.ResetLoginExpiredNotification();

        if (EndpointEnum == CrunchyrollEndpoints.Guest){
            return;
        }

        CfgManager.WriteJsonToFile(GetTokenFilePath(), Token);
    }

    private async Task AuthCode(AuthData authData){
        var uuid = ResolveDeviceId();
        var loginPayload = JsonConvert.SerializeObject(new Dictionary<string, object>{
            { "email", authData.Username },
            { "password", authData.Password },
            { "eventSettings", new Dictionary<string, object>() }
        });
        var requestContent = new StringContent(loginPayload, Encoding.UTF8);
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain"){
            CharSet = "UTF-8"
        };

        var request = CreateRequest(HttpMethod.Post, "https://sso.crunchyroll.com/api/login", requestContent, includeAuthorization: false);
        var response = await HttpClientReq.Instance.SendHttpRequest(request, false, cookieStore);

        if (response.IsOk){
            var refreshToken = HttpClientReq.Instance.GetCookieValue(SsoDomain, "etp_rt", cookieStore);
            Token = new CrToken{ refresh_token = refreshToken, device_id = uuid };
            await GetCodeAuth();
            await LoginWithCode();
        } else{
            await PublishLoginFailureAsync(response.ResponseContent);
        }
    }

    private static string GenerateCodeVerifier(int length = 64){
        // RFC 7636: length between 43 and 128.
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var stringBuilder = new StringBuilder(length);

        foreach (var value in bytes){
            stringBuilder.Append(allowed[value % allowed.Length]);
        }

        return stringBuilder.ToString();
    }

    public async Task GetCodeAuth(){
        var uuid = Guid.NewGuid().ToString();
        NameValueCollection query = HttpUtility.ParseQueryString(new UriBuilder().Query);

        authCodeVerifier = GenerateCodeVerifier();
        var clientId = GetClientIdFromBasicHeader(AuthSettings.Authorization);

        query["client_id"] = clientId;
        query["redirect_uri"] = "sso.crunchyroll://auth";
        query["response_type"] = "code";
        query["scope"] = "offline_access";
        query["state"] = "{\"flow\":\"SIGN_IN\",\"flowRoot\":\"ONBOARDING\"}";
        query["code_challenge"] = authCodeVerifier;
        query["code_challenge_method"] = "plain";

        HttpClientReq.Instance.AddCookie(SsoDomain, new Cookie("client_id", clientId), cookieStore);
        HttpClientReq.Instance.AddCookie(SsoDomain, new Cookie("device_id", uuid), cookieStore);

        var uriBuilder = new UriBuilder("https://sso.crunchyroll.com/authorize"){
            Query = query.ToString()
        };

        var request = CreateRequest(HttpMethod.Get, uriBuilder.ToString(), includeAuthorization: false);
        var response = await HttpClientReq.Instance.SendHttpRequest(request);

        authCode = ExtractCode(response.ResponseContent);

        if (string.IsNullOrEmpty(authCode)){
            Console.Error.WriteLine("Auth code is empty");
        }
    }

    private static string GetClientIdFromBasicHeader(string authorizationHeader){
        if (string.IsNullOrWhiteSpace(authorizationHeader)){
            throw new ArgumentException("Authorization header is null/empty.", nameof(authorizationHeader));
        }

        const string prefix = "Basic ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)){
            throw new FormatException("Authorization header is not Basic.");
        }

        var base64 = authorizationHeader[prefix.Length..].Trim();

        byte[] bytes;
        try{
            bytes = Convert.FromBase64String(base64);
        } catch (FormatException ex){
            throw new FormatException("Basic token is not valid Base64.", ex);
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex <= 0){
            throw new FormatException("Decoded Basic value is not in 'clientId:clientSecret' format.");
        }

        return decoded[..separatorIndex];
    }

    private static string Normalize(string value){
        value = Regex.Unescape(value);
        value = value.Replace(@"\u0026", "&");
        value = value.Replace("\\\"", "\"");
        return value;
    }

    private static string ExtractCode(string body){
        var text = Normalize(body);

        var match = Regex.Match(text, @"(?:[?&]|\\u0026)code=([A-Za-z0-9\-_]+)");
        if (match.Success){
            return match.Groups[1].Value;
        }

        match = Regex.Match(text, @"code=([A-Za-z0-9\-_]+)");
        if (match.Success){
            return match.Groups[1].Value;
        }

        Console.Error.WriteLine("Authorization code not found in response body.");
        return string.Empty;
    }

    private async Task AuthOld(AuthData data){
        var uuid = Guid.NewGuid().ToString();
        var formData = new Dictionary<string, string>{
            { "username", data.Username },
            { "password", data.Password },
            { "grant_type", "password" },
            { "scope", "offline_access" },
        };
        AddDeviceMetadata(formData, uuid);

        var request = CreateTokenRequest(formData);
        var response = await HttpClientReq.Instance.SendHttpRequest(request);

        if (response.IsOk){
            JsonTokenToFileAndVariable(response.ResponseContent, uuid);
        } else{
            await PublishLoginFailureAsync(response.ResponseContent);
        }

        if (Token?.refresh_token != null){
            SetETPCookie(Token.refresh_token);
            await GetMultiProfile();
        }
    }

    public async Task ChangeProfile(string profileId){
        if (HasNoUsableRefreshToken()){
            await AuthAnonymous();
        }

        if (Profile.Username == UnknownUsername || string.IsNullOrEmpty(profileId) || Token?.refresh_token == null){
            return;
        }

        var uuid = ResolveDeviceId();
        SetETPCookie(Token.refresh_token);

        var formData = new Dictionary<string, string>{
            { "grant_type", "refresh_token_profile_id" },
            { "profile_id", profileId },
        };
        AddDeviceMetadata(formData, uuid);

        var request = CreateTokenRequest(formData);
        var response = await HttpClientReq.Instance.SendHttpRequest(request, false, cookieStore);

        if (response.IsOk){
            JsonTokenToFileAndVariable(response.ResponseContent, uuid);
            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
            }

            await GetMultiProfile();
        } else{
            Console.Error.WriteLine("Refresh Token Auth Failed");
        }
    }

    public async Task GetProfile(){
        if (Token?.access_token == null){
            Console.Error.WriteLine("Missing Access Token");
            return;
        }

        var request = HttpClientReq.CreateRequestMessage(ApiUrls.Profile, HttpMethod.Get, true, Token.access_token, null);
        var response = await HttpClientReq.Instance.SendHttpRequest(request);

        if (response.IsOk){
            var profileTemp = Helpers.Deserialize<CrProfile>(response.ResponseContent, crunInstance.SettingsJsonSerializerSettings);

            if (profileTemp != null){
                Profile = profileTemp;
                await GetSubscription();
            }
        }
    }

    private async Task GetSubscription(){
        var requestSubs = HttpClientReq.CreateRequestMessage(ApiUrls.Subscription + Token.account_id, HttpMethod.Get, true, Token.access_token, null);
        var responseSubs = await HttpClientReq.Instance.SendHttpRequest(requestSubs);

        if (!responseSubs.IsOk){
            Profile.HasPremium = false;
            Console.Error.WriteLine("Failed to check premium subscription status");
            return;
        }

        var subsc = Helpers.Deserialize<Subscription>(responseSubs.ResponseContent, crunInstance.SettingsJsonSerializerSettings);
        Subscription = subsc;
        if (subsc is{ SubscriptionProducts:{ Count: 0 }, ThirdPartySubscriptionProducts.Count: > 0 }){
            var thirdPartySub = subsc.ThirdPartySubscriptionProducts.First();
            var expiration = thirdPartySub.InGrace ? thirdPartySub.InGraceExpirationDate : thirdPartySub.ExpirationDate;
            var remaining = expiration - DateTime.Now;
            Profile.HasPremium = true;
            if (Subscription != null){
                Subscription.IsActive = remaining > TimeSpan.Zero;
                Subscription.NextRenewalDate = expiration;
            }
        } else if (subsc is{ SubscriptionProducts:{ Count: 0 }, NonrecurringSubscriptionProducts.Count: > 0 }){
            var nonRecurringSub = subsc.NonrecurringSubscriptionProducts.First();
            var remaining = nonRecurringSub.EndDate - DateTime.Now;
            Profile.HasPremium = true;
            if (Subscription != null){
                Subscription.IsActive = remaining > TimeSpan.Zero;
                Subscription.NextRenewalDate = nonRecurringSub.EndDate;
            }
        } else if (subsc is{ SubscriptionProducts:{ Count: 0 }, FunimationSubscriptions.Count: > 0 }){
            Profile.HasPremium = true;
        } else if (subsc is{ SubscriptionProducts.Count: > 0 }){
            Profile.HasPremium = true;
        } else{
            Profile.HasPremium = false;
            Console.Error.WriteLine($"No subscription available:\n {JsonConvert.SerializeObject(subsc, Formatting.Indented)} ");
        }
    }

    private async Task GetMultiProfile(){
        if (Token?.access_token == null){
            Console.Error.WriteLine("Missing Access Token");
            return;
        }

        var request = HttpClientReq.CreateRequestMessage(ApiUrls.MultiProfile, HttpMethod.Get, true, Token.access_token);
        var response = await HttpClientReq.Instance.SendHttpRequest(request, false, cookieStore);

        if (response.IsOk){
            MultiProfile = Helpers.Deserialize<CrMultiProfile>(response.ResponseContent, crunInstance.SettingsJsonSerializerSettings) ?? new CrMultiProfile();

            var selectedProfile = MultiProfile.Profiles.FirstOrDefault(e => e.IsSelected);
            if (selectedProfile != null){
                Profile = selectedProfile;
            }

            await GetSubscription();
        }
    }

    public async Task LoginWithCode(){
        if (string.IsNullOrEmpty(authCode)){
            Console.Error.WriteLine("Missing code");
            await AuthAnonymous();
            return;
        }

        var uuid = ResolveDeviceId();
        var formData = new Dictionary<string, string>{
            { "code", authCode },
            { "code_verifier", authCodeVerifier },
            { "grant_type", "authorization_code" },
            { "scope", "offline_access" },
        };
        AddDeviceMetadata(formData, uuid);

        var request = CreateTokenRequest(formData);
        SetETPCookie(Token?.refresh_token ?? string.Empty);

        var response = await HttpClientReq.Instance.SendHttpRequest(request);
        await CompleteInteractiveTokenLoginAsync(response, uuid);
    }

    public async Task LoginWithToken(){
        if (Token?.refresh_token == null){
            Console.Error.WriteLine("Missing Refresh Token");
            await AuthAnonymous();
            return;
        }

        var uuid = ResolveDeviceId();
        var request = CreateRefreshTokenRequest(uuid, Token.refresh_token);
        SetETPCookie(Token.refresh_token);

        var response = await HttpClientReq.Instance.SendHttpRequest(request);
        await CompleteInteractiveTokenLoginAsync(response, uuid);
    }

    public async Task RefreshToken(bool needsToken){
        if (EndpointEnum == CrunchyrollEndpoints.Guest){
            if (!IsTokenExpiredOrNearExpiry()){
                return;
            }

            await AuthAnonymousFoxy();
            return;
        }

        if (HasNoUsableRefreshToken()){
            await AuthAnonymous();
        } else if (!IsTokenExpiredOrNearExpiry() && needsToken){
            return;
        }

        if (Profile.Username == UnknownUsername){
            return;
        }

        var hadUserSession = !string.IsNullOrWhiteSpace(Token?.refresh_token) && !string.IsNullOrWhiteSpace(Profile.Username) && Profile.Username != UnknownUsername;
        var uuid = ResolveDeviceId();
        var refreshToken = Token?.refresh_token ?? string.Empty;
        var request = CreateRefreshTokenRequest(uuid, refreshToken);

        SetETPCookie(refreshToken);
        var response = await HttpClientReq.Instance.SendHttpRequest(request);

        if (response.IsOk){
            JsonTokenToFileAndVariable(response.ResponseContent, uuid);
        } else{
            Console.Error.WriteLine("Refresh Token Auth Failed");
            if (hadUserSession){
                await NotificationPublisher.Instance.PublishLoginExpiredAsync(crunInstance.CrunOptions.NotificationSettings, Profile.Username, AuthSettings.Endpoint);
            }
        }
    }

    private async Task CompleteInteractiveTokenLoginAsync((bool IsOk, string ResponseContent, string error, Dictionary<string, string> Headers) response, string uuid){
        if (IsCloudflareBlock(response.ResponseContent)){
            PublishCloudflareLoginFailure();
        }

        if (response.IsOk){
            JsonTokenToFileAndVariable(response.ResponseContent, uuid);

            if (Token?.refresh_token != null){
                SetETPCookie(Token.refresh_token);
                await GetMultiProfile();
            }
        } else{
            Console.Error.WriteLine("Token Auth Failed");
            await AuthAnonymous();
            MainWindow.Instance.ShowError("Login failed. Please check the log for more details.");
        }
    }

    private HttpRequestMessage CreateRefreshTokenRequest(string uuid, string refreshToken){
        var formData = new Dictionary<string, string>{
            { "refresh_token", refreshToken },
            { "scope", "offline_access" },
            { "grant_type", "refresh_token" },
        };
        AddDeviceMetadata(formData, uuid);
        return CreateTokenRequest(formData);
    }

    private HttpRequestMessage CreateTokenRequest(Dictionary<string, string> formData){
        return CreateRequest(HttpMethod.Post, ApiUrls.Auth, new FormUrlEncodedContent(formData));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, HttpContent? content = null, bool includeAuthorization = true){
        var request = new HttpRequestMessage(method, uri){
            Content = content
        };

        if (includeAuthorization){
            request.Headers.Add("Authorization", AuthSettings.Authorization);
        }

        request.Headers.Add("User-Agent", AuthSettings.UserAgent);
        return request;
    }

    private void AddDeviceMetadata(Dictionary<string, string> formData, string uuid){
        formData["device_id"] = uuid;
        formData["device_type"] = AuthSettings.Device_type;

        if (!string.IsNullOrEmpty(AuthSettings.Device_name)){
            formData["device_name"] = AuthSettings.Device_name;
        }
    }

    private string ResolveDeviceId(){
        return string.IsNullOrEmpty(Token?.device_id) ? Guid.NewGuid().ToString() : Token.device_id;
    }

    private bool HasNoUsableRefreshToken(){
        return Token?.access_token == null && Token?.refresh_token == null ||
               Token?.access_token != null && Token.refresh_token == null;
    }

    private async Task PublishLoginFailureAsync(string responseContent){
        if (responseContent.Contains("invalid_credentials")){
            MessageBus.Current.SendMessage(new ToastMessage("Login failed. Please check your username and password.", ToastType.Error, 5));
        } else if (IsCloudflareBlock(responseContent)){
            PublishCloudflareLoginFailure();
        } else{
            var previewLength = Math.Min(responseContent.Length, 200);
            MessageBus.Current.SendMessage(new ToastMessage($"Failed to login - {responseContent[..previewLength]}", ToastType.Error, 5));
            await Console.Error.WriteLineAsync("Full Response: " + responseContent);
        }
    }

    private void PublishCloudflareLoginFailure(){
        var betaApiHint = crunInstance.CrunOptions.UseCrBetaApi ? string.Empty : "try to change to BetaAPI in settings";
        var message = $"Failed to login - Cloudflare error {betaApiHint}";
        MessageBus.Current.SendMessage(new ToastMessage(message, ToastType.Error, 5));
        Console.Error.WriteLine(message);
    }

    private static bool IsCloudflareBlock(string responseContent){
        return responseContent.Contains("<title>Just a moment...</title>") ||
               responseContent.Contains("<title>Access denied</title>") ||
               responseContent.Contains("<title>Attention Required! | Cloudflare</title>") ||
               responseContent.Trim().Equals("error code: 1020") ||
               responseContent.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1;
    }
}
