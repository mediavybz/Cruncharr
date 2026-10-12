using System.Net;
using System.Reflection;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Cruncharr.Core.Tests;

// Audit F06: a saved login whose refresh failed (and was kept for a retry) still reported
// "authenticated/Premium", while browsing failed because the stale account token was the only
// token on offer.
public class SessionHealthTests : IDisposable
{
    private const string Series = """
        {"total":1,"data":[{"id":"GSERIES01","title":"Naruto","series_metadata":{"episode_count":1}}]}
        """;
    private const string GuestToken = """{"access_token":"guest","expires_in":300,"token_type":"Bearer"}""";
    private const string AccountToken = """
        {"access_token":"account","refresh_token":"rotated","expires_in":300,"token_type":"Bearer","account_id":"acct"}
        """;

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("cruncharr-session-health-");
    private readonly CruncharrConfig _config;
    private readonly CrunchyrollAuthService _auth;
    private readonly CrunchyrollApiService _api;
    private readonly List<string> _catalogBearers = [];
    private Func<HttpResponseMessage> _refresh = () => Status(HttpStatusCode.ServiceUnavailable);

    public SessionHealthTests()
    {
        _config = new CruncharrConfig { TokenFilePath = Path.Combine(_directory.FullName, "token.json") };
        _auth = new CrunchyrollAuthService(_config);
        _api = new CrunchyrollApiService(_auth);
        var saved = new CrToken { access_token = "stale", refresh_token = "saved", account_id = "acct", device_id = "dev", expires = DateTime.Now.AddMinutes(-5) };
        File.WriteAllText(TokenFile, JsonConvert.SerializeObject(saved));
        typeof(CrunchyrollAuthService).GetProperty(nameof(_auth.Token))!.SetValue(_auth, saved);
        typeof(CrunchyrollAuthService).GetProperty(nameof(_auth.Profile))!.SetValue(_auth, new CrProfile { Username = "user", HasPremium = true });
        SetTransport(_auth.HttpClient);
        SetTransport((HttpClientWrapper)typeof(CrunchyrollApiService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_api)!);
    }

    private string TokenFile => Path.Combine(_directory.FullName, "token_tv.json");

    [Fact]
    public async Task TransientRefreshFailure_IsReportedAndBrowsingUsesAGuestToken()
    {
        var savedFile = File.ReadAllText(TokenFile);

        Assert.False(await _auth.RefreshTokenAsync(true, TestContext.Current.CancellationToken));
        Assert.False(_auth.IsAuthenticated);
        Assert.NotNull(_auth.SessionError);

        var status = Assert.IsType<AuthStatusResponse>(Assert.IsType<OkObjectResult>((await new AuthController(_auth, _config).GetStatus()).Result).Value);
        Assert.False(status.IsAuthenticated);
        Assert.False(status.HasPremium);
        Assert.Equal(_auth.SessionError, status.SessionError);

        Assert.Single(await _api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["Bearer guest"], _catalogBearers);
        // The account session is kept for the retry, in memory and on disk.
        Assert.Equal("saved", _auth.Token?.refresh_token);
        Assert.Equal(savedFile, File.ReadAllText(TokenFile));

        // A later successful refresh restores the account session.
        _refresh = () => Status(HttpStatusCode.OK, AccountToken);
        Assert.Single(await _api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["Bearer guest", "Bearer account"], _catalogBearers);
        Assert.True(_auth.IsAuthenticated);
        Assert.Null(_auth.SessionError);
    }

    [Fact]
    public async Task RejectedRefreshToken_DropsTheSessionAndBrowsesAsGuest()
    {
        _refresh = () => Status(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        Assert.Single(await _api.GetAllSeriesAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["Bearer guest"], _catalogBearers);
        Assert.False(_auth.IsAuthenticated);
        Assert.Null(_auth.SessionError);
        Assert.Null(_auth.Token?.account_id);
        Assert.DoesNotContain("saved", File.Exists(TokenFile) ? File.ReadAllText(TokenFile) : "");
    }

    private void SetTransport(HttpClientWrapper wrapper)
    {
        wrapper.Client.Dispose();
        typeof(HttpClientWrapper).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(wrapper, new HttpClient(new Handler(async (request, cancellation) =>
            {
                if (request.RequestUri!.AbsoluteUri != ApiUrls.Auth)
                {
                    _catalogBearers.Add(request.Headers.Authorization!.ToString());
                    return Status(HttpStatusCode.OK, Series);
                }
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellation));
                return form["grant_type"] switch
                {
                    "refresh_token" => _refresh(),
                    "client_id" => Status(HttpStatusCode.OK, GuestToken),
                    _ => Status(HttpStatusCode.NotFound)
                };
            })));
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    public void Dispose()
    {
        _api.Dispose();
        _auth.HttpClient.Dispose();
        _directory.Delete(recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
