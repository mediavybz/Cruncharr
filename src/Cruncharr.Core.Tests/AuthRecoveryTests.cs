using System.Net;
using System.Reflection;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Newtonsoft.Json;

namespace Cruncharr.Core.Tests;

// Regressions from the 2026-10-10 outage: a retired TV client, a login chain that skipped the
// clients that still worked, and a partial SSO token whose unset expiry crashed every request.
public class AuthRecoveryTests
{
    private const string MobileAuth = "Basic YnFjaGljMmc3aTJzcnQ5cXU1c2I6NkVKT0tQLXNxU3hEb3RXdVgwZmVnV3pNX2FiTWRNWUo=";
    private const string MaintainedAuth = "Basic dC1rZGdwMmg4YzNqdWI4Zm4wZnE6eWZMRGZNZnJZdktYaDRKWFMxTEVJMmNDcXUxdjVXYW4=";
    private const string AccountToken = """
        {"access_token":"account","refresh_token":"rotated","expires_in":300,"token_type":"Bearer","account_id":"acct"}
        """;

    [Fact]
    public void ExpiryCheck_TreatsAnUnsetExpiryAsExpiredInsteadOfThrowing()
    {
        using var scope = new AuthScope();
        scope.SetToken(new CrToken { access_token = "partial", refresh_token = "sso" });
        var expired = (bool)typeof(CrunchyrollAuthService)
            .GetMethod("IsTokenExpiredOrNearExpiry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(scope.Auth, null)!;
        Assert.True(expired);
    }

    [Fact]
    public async Task PartialInMemoryToken_ReloadsTheSavedSessionInsteadOfThrowing()
    {
        using var scope = new AuthScope();
        File.WriteAllText(scope.TokenFile, JsonConvert.SerializeObject(new CrToken
        {
            access_token = "old", refresh_token = "saved", account_id = "acct", device_id = "dev", expires = DateTime.Now.AddMinutes(-5)
        }));
        scope.SetToken(new CrToken { refresh_token = "sso", device_id = "dev" });
        var refreshTokens = new List<string?>();
        scope.Respond(async (request, cancellation) =>
        {
            if (request.RequestUri!.AbsoluteUri != ApiUrls.Auth) return Status(HttpStatusCode.NotFound);
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellation));
            refreshTokens.Add(form["refresh_token"]);
            return Status(HttpStatusCode.OK, AccountToken);
        });

        await scope.Auth.AuthenticateAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(["saved"], refreshTokens);
        Assert.Equal("account", scope.Auth.Token?.access_token);
    }

    [Fact]
    public async Task RetiredTvClient_RetriesTheUpdatedTvClientBeforeMobile()
    {
        using var scope = new AuthScope();
        var retired = scope.Auth.StreamEndpoint.Authorization;
        const string updated = "Basic dXBkYXRlZC10djpzZWNyZXQ=";
        var grants = new List<string>();
        scope.Respond((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "codeberg.org" => Status(HttpStatusCode.OK, $$"""[{"type":"tv","version_name":"99.0.0","version_code":"1","Authorization":"{{updated}}"}]"""),
            _ when request.RequestUri.AbsoluteUri == ApiUrls.Auth => Grant(grants, request, auth => auth == retired
                ? Status(HttpStatusCode.Unauthorized, """{"code":"auth.obtain_access_token.client_inactive","error":"invalid_client"}""")
                : Status(HttpStatusCode.OK, AccountToken)),
            _ => Status(HttpStatusCode.NotFound)
        }));

        Assert.True(await scope.Auth.LoginAsync("user@example.test", "test-password", true, TestContext.Current.CancellationToken));

        Assert.Equal([retired!, updated], grants);
        Assert.Equal(updated, scope.Auth.StreamEndpoint.Authorization);
        Assert.Equal("account", scope.Auth.Token?.access_token);
    }

    [Fact]
    public async Task MobileUnsupportedGrant_ContinuesToTheMaintainedClient()
    {
        using var scope = new AuthScope();
        var retired = scope.Auth.StreamEndpoint.Authorization;
        var grants = new List<string>();
        scope.Respond((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri == ApiUrls.Auth
            ? Grant(grants, request, auth => auth switch
            {
                MaintainedAuth => Status(HttpStatusCode.OK, AccountToken),
                MobileAuth => Status(HttpStatusCode.BadRequest, """{"error":"unsupported_grant_type"}"""),
                _ => Status(HttpStatusCode.Unauthorized, """{"code":"auth.obtain_access_token.client_inactive","error":"invalid_client"}""")
            })
            : Status(HttpStatusCode.NotFound)));

        Assert.True(await scope.Auth.LoginAsync("user@example.test", "test-password", true, TestContext.Current.CancellationToken));

        Assert.Equal([retired!, MobileAuth, MaintainedAuth], grants);
        Assert.Equal(MaintainedAuth, scope.Auth.StreamEndpoint.Authorization);
    }

    [Theory]
    [InlineData("""{"code":"auth.obtain_access_token.invalid_credentials","error":"invalid_grant"}""")]
    [InlineData("""{"status":"error","error":"too_many_requests"}""")]
    public async Task TerminalFailures_DoNotTryMorePasswordClients(string terminal)
    {
        using var scope = new AuthScope();
        var retired = scope.Auth.StreamEndpoint.Authorization;
        const string updated = "Basic dXBkYXRlZC10djpzZWNyZXQ=";
        var grants = new List<string>();
        scope.Respond((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "codeberg.org" => Status(HttpStatusCode.OK, $$"""[{"type":"tv","version_name":"99.0.0","version_code":"1","Authorization":"{{updated}}"}]"""),
            _ when request.RequestUri.AbsoluteUri == ApiUrls.Auth => Grant(grants, request, auth => auth == retired
                ? Status(HttpStatusCode.Unauthorized, """{"code":"auth.obtain_access_token.client_inactive","error":"invalid_client"}""")
                : Status(HttpStatusCode.Unauthorized, terminal)),
            _ => Status(HttpStatusCode.NotFound)
        }));

        await Assert.ThrowsAnyAsync<Exception>(() => scope.Auth.LoginAsync("user@example.test", "test-password", true, TestContext.Current.CancellationToken));

        Assert.Equal([retired!, updated], grants);
    }

    [Fact]
    public async Task FailedSsoExchange_LeavesTheExistingSessionInMemory()
    {
        using var scope = new AuthScope();
        // A non-TV endpoint goes straight to the SSO code flow.
        scope.Auth.StreamEndpoint.Endpoint = "android/phone";
        scope.Auth.StreamEndpoint.Authorization = MobileAuth;
        var session = new CrToken { access_token = "account", refresh_token = "saved", account_id = "acct", expires = DateTime.Now.AddMinutes(5) };
        scope.SetToken(session);
        scope.Respond((request, _) =>
        {
            var response = Status(HttpStatusCode.NotFound);
            if (request.RequestUri!.AbsolutePath == "/api/login")
            {
                response = Status(HttpStatusCode.OK, "{}");
                response.Headers.Add("Set-Cookie", "etp_rt=sso-cookie; Domain=sso.crunchyroll.com; Path=/");
            }
            return Task.FromResult(response);
        });

        await Assert.ThrowsAnyAsync<Exception>(() => scope.Auth.LoginAsync("user@example.test", "test-password", true, TestContext.Current.CancellationToken));

        Assert.Same(session, scope.Auth.Token);
    }

    private static HttpResponseMessage Grant(List<string> grants, HttpRequestMessage request, Func<string, HttpResponseMessage> respond)
    {
        var form = System.Web.HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        if (form["grant_type"] != "password") return Status(HttpStatusCode.NotFound);
        var auth = request.Headers.GetValues("Authorization").Single();
        grants.Add(auth);
        return respond(auth);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    private sealed class AuthScope : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("cruncharr-auth-recovery-");

        public AuthScope()
        {
            Auth = new CrunchyrollAuthService(new CruncharrConfig { TokenFilePath = Path.Combine(_directory.FullName, "token.json") });
        }

        public CrunchyrollAuthService Auth { get; }
        public string TokenFile => Path.Combine(_directory.FullName, "token_tv.json");

        public void SetToken(CrToken token) =>
            typeof(CrunchyrollAuthService).GetProperty(nameof(Auth.Token))!.SetValue(Auth, token);

        public void Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            var wrapper = Auth.HttpClient;
            wrapper.Client.Dispose();
            typeof(HttpClientWrapper).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(wrapper, new HttpClient(new Handler(send)));
        }

        public void Dispose()
        {
            Auth.HttpClient.Dispose();
            _directory.Delete(recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
