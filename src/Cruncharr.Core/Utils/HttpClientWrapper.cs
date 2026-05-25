using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cruncharr.Core.Configuration;

namespace Cruncharr.Core.Utils;

public class HttpClientWrapper{
    private readonly HttpClient _client;
    private readonly CookieContainer _cookieContainer;
    private readonly Dictionary<string, CookieCollection> _cookieStore = new();
    private readonly CruncharrConfig? _config;
    private readonly HttpClient? _flareSolverrClient;
    
    public HttpClient Client => _client;
    public CookieContainer CookieContainer => _cookieContainer;
    
    public HttpClientWrapper(CruncharrConfig? config = null){
        _config = config;
        _cookieContainer = new CookieContainer();
        
        var handler = new SocketsHttpHandler{
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            CookieContainer = _cookieContainer,
            UseCookies = true,
            ConnectCallback = async (context, cancellationToken) => {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                try{
                    await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                } catch{
                    socket.Dispose();
                    throw;
                }
            }
        };
        
        // Configure proxy if enabled
        if (config?.Proxy?.Enabled == true){
            ConfigureProxy(handler, config.Proxy);
        }
        
        _client = new HttpClient(handler);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        _client.DefaultRequestHeaders.Connection.ParseAdd("keep-alive");
        
        // Setup FlareSolverr client if enabled
        if (config?.FlareSolverr?.Enabled == true){
            _flareSolverrClient = new HttpClient();
            _flareSolverrClient.Timeout = TimeSpan.FromMinutes(2);
        }
    }
    
    private void ConfigureProxy(SocketsHttpHandler handler, ProxyConfig proxy){
        try{
            var proxyUri = $"{(proxy.Socks ? "socks5" : "http")}://{proxy.Host}:{proxy.Port}";
            var webProxy = new WebProxy(proxyUri);
            
            if (!string.IsNullOrEmpty(proxy.Username)){
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }
            
            handler.Proxy = webProxy;
            handler.UseProxy = true;
            Console.WriteLine($"Proxy configured: {proxyUri}");
        } catch (Exception ex){
            Console.Error.WriteLine($"Failed to configure proxy: {ex.Message}");
        }
    }
    
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default){
        // Use FlareSolverr if enabled and request is for Crunchyroll
        if (_flareSolverrClient != null && request.RequestUri?.Host.Contains("crunchyroll") == true){
            return await SendViaFlareSolverrAsync(request, cancellationToken);
        }
        
        AttachCookies(request);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        CaptureResponseCookies(response, request.RequestUri!);
        return response;
    }
    
    private async Task<HttpResponseMessage> SendViaFlareSolverrAsync(HttpRequestMessage request, CancellationToken cancellationToken){
        if (_config?.FlareSolverr == null) throw new InvalidOperationException("FlareSolverr not configured");
        
        var scheme = _config.FlareSolverr.UseSsl ? "https" : "http";
        var flareSolverrUrl = $"{scheme}://{_config.FlareSolverr.Host}:{_config.FlareSolverr.Port}/v1";
        
        var payload = new{
            cmd = "request.get",
            url = request.RequestUri!.ToString(),
            maxTimeout = 60000
        };
        
        var flareRequest = new HttpRequestMessage(HttpMethod.Post, flareSolverrUrl){
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        
        var flareResponse = await _flareSolverrClient!.SendAsync(flareRequest, cancellationToken);
        flareResponse.EnsureSuccessStatusCode();
        
        // Return a synthetic response
        return flareResponse;
    }
    
    public async Task<(bool IsOk, string ResponseContent, string Error)> SendRequestAsync(HttpRequestMessage request, bool suppressError = false){
        var result = await SendRequestWithHeadersAsync(request, suppressError);
        return (result.IsOk, result.ResponseContent, result.Error);
    }
    
    public async Task<(bool IsOk, string ResponseContent, string Error, Dictionary<string, string> Headers)> SendRequestWithHeadersAsync(HttpRequestMessage request, bool suppressError = false){
        string content = string.Empty;
        var headers = new Dictionary<string, string>();
        try{
            AttachCookies(request);
            var response = await _client.SendAsync(request);
            content = await response.Content.ReadAsStringAsync();
            foreach (var header in response.Headers){
                headers[header.Key.ToLower()] = string.Join(", ", header.Value);
            }
            response.EnsureSuccessStatusCode();
            CaptureResponseCookies(response, request.RequestUri!);
            return (true, content, "", headers);
        } catch (Exception e){
            if (!suppressError){
                Console.Error.WriteLine($"Error: {e.Message}");
            }
            return (false, content, e.Message, headers);
        }
    }
    
    public static HttpRequestMessage CreateRequest(string uri, HttpMethod method, bool authHeader, string? accessToken = ""){
        if (string.IsNullOrEmpty(uri)){
            Console.Error.WriteLine("Request URI is empty");
            return new HttpRequestMessage(HttpMethod.Get, "about:blank");
        }
        
        var request = new HttpRequestMessage(method, uri);
        if (authHeader && !string.IsNullOrEmpty(accessToken)){
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return request;
    }
    
    private void AttachCookies(HttpRequestMessage request){
        var cookieHeader = new StringBuilder();
        if (request.Headers.TryGetValues("Cookie", out var existingCookies)){
            cookieHeader.Append(string.Join("; ", existingCookies));
        }
        foreach (var cookie in _cookieStore.SelectMany(kvp => kvp.Value)){
            string cookieString = $"{cookie.Name}={cookie.Value}";
            if (!cookieHeader.ToString().Contains(cookieString)){
                if (cookieHeader.Length > 0) cookieHeader.Append("; ");
                cookieHeader.Append(cookieString);
            }
        }
        if (cookieHeader.Length > 0){
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", cookieHeader.ToString());
        }
    }
    
    private void CaptureResponseCookies(HttpResponseMessage response, Uri requestUri){
        if (response.Headers.TryGetValues("Set-Cookie", out var cookieHeaders)){
            string domain = requestUri.Host.StartsWith("www.") ? requestUri.Host.Substring(4) : requestUri.Host;
            foreach (var header in cookieHeaders){
                var cookies = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var nameValue = cookies[0].Split('=', 2);
                if (nameValue.Length != 2) continue;
                var cookie = new Cookie(nameValue[0].Trim(), nameValue[1].Trim()){
                    Domain = domain,
                    Path = "/"
                };
                AddCookie(domain, cookie);
            }
        }
    }
    
    public void AddCookie(string domain, Cookie cookie){
        if (!_cookieStore.ContainsKey(domain)){
            _cookieStore[domain] = new CookieCollection();
        }
        var existing = _cookieStore[domain].FirstOrDefault(c => c.Name == cookie.Name);
        if (existing != null) _cookieStore[domain].Remove(existing);
        _cookieStore[domain].Add(cookie);
        
        try{
            _cookieContainer.Add(new Uri($"https://{domain}"), cookie);
        } catch{
            // Ignore invalid cookie domains
        }
    }
    
    public string? GetCookieValue(string domain, string cookieName){
        if (_cookieStore.TryGetValue(domain, out var cookies)){
            var cookie = cookies.FirstOrDefault(c => c.Name == cookieName);
            return cookie?.Value;
        }
        return null;
    }
}
