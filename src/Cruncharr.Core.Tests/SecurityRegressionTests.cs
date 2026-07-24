using System.Net;
using System.Reflection;
using Cruncharr.API.Controllers;
using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

public class SecurityRegressionTests
{
    [Fact]
    public async Task ImageProxy_DoesNotFollowRedirectToUntrustedHost()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler);
        var controller = new ImagesController(
            NullLogger<ImagesController>.Instance,
            new StubHttpClientFactory(client));

        var result = await controller.Get(
            "https://www.crunchyroll.com/catalog/image.jpg",
            TestContext.Current.CancellationToken);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void WebhookTransport_DisablesRedirectsAndPinsValidatedDns()
    {
        using var handler = WebhookUrlValidator.CreateHttpMessageHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task WebhookTransport_BlocksPrivateAddressAtConnectTime()
    {
        using var handler = WebhookUrlValidator.CreateHttpMessageHandler();
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(
                "http://127.0.0.1/",
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://224.0.0.1/hook")]
    [InlineData("http://240.0.0.1/hook")]
    [InlineData("http://192.0.2.1/hook")]
    [InlineData("http://198.18.0.1/hook")]
    [InlineData("http://[::]/hook")]
    [InlineData("http://[2001:db8::1]/hook")]
    public void WebhookValidation_RejectsNonPublicSpecialUseAddresses(string url)
    {
        Assert.False(WebhookUrlValidator.IsValidWebhookUrl(url, out _));
    }

    [Fact]
    public void NotificationService_UsesHardenedWebhookClient()
    {
        using var client = new HttpClient(new RedirectHandler());
        var factory = new StubHttpClientFactory(client);

        _ = new NotificationService(
            factory,
            NullLogger<NotificationService>.Instance);

        Assert.Equal("CruncharrWebhooks", factory.LastClientName);
    }

    [Fact]
    public async Task NotificationService_RetriesFailedUpdateWebhookBeforeDeduplicating()
    {
        var config = new CruncharrConfig();
        config.Notifications.WebhookEnabled = true;
        config.Notifications.WebhookUrl = "https://93.184.216.34/hook";
        config.Notifications.NotifyUpdateAvailable = true;
        var handler = new SequenceHandler(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var service = new NotificationService(
            new StubHttpClientFactory(client),
            NullLogger<NotificationService>.Instance);

        await service.NotifyUpdateAvailableAsync("1.0.0", "1.0.1", config);
        await service.NotifyUpdateAvailableAsync("1.0.0", "1.0.1", config);
        await service.NotifyUpdateAvailableAsync("1.0.0", "1.0.1", config);

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AuthRefresh_WaitsOnSharedCancellationAwareGate()
    {
        var auth = new CrunchyrollAuthService(new CruncharrConfig());
        var gateField = typeof(CrunchyrollAuthService).GetField(
            "_refreshTokenGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(gateField?.GetValue(auth));
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var refresh = auth.RefreshTokenAsync(true, cancellation.Token);
            Assert.False(refresh.IsCompleted);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task AuthLoginLogoutAndProfileSwitch_WaitOnSharedStateGate()
    {
        var config = new CruncharrConfig
        {
            TokenFilePath = Path.Combine(
                Path.GetTempPath(),
                $"cruncharr-auth-gate-{Guid.NewGuid():N}.json")
        };
        var auth = new CrunchyrollAuthService(config);
        var gateField = typeof(CrunchyrollAuthService).GetField(
            "_refreshTokenGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(gateField?.GetValue(auth));
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var login = auth.LoginAsync("user@example.test", "password", true, cancellation.Token);
            var profileSwitch = auth.ChangeProfileAsync("profile-id", true, cancellationToken: cancellation.Token);
            var logout = auth.LogoutAsync();
            Assert.False(login.IsCompleted);
            Assert.False(profileSwitch.IsCompleted);
            Assert.False(logout.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profileSwitch);

            gate.Release();
            await logout;
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
    }

    [Fact]
    public void HttpClientWrapper_DoesNotAttachCrunchyrollCookiesCrossDomain()
    {
        using var wrapper = new HttpClientWrapper();
        wrapper.AddCookie(
            ".crunchyroll.com",
            new Cookie("session", "secret"));
        using var externalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://graphql.anilist.co");
        using var crunchyrollRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.crunchyroll.com/");
        var attachMethod = typeof(HttpClientWrapper).GetMethod(
            "AttachCookies",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(attachMethod);
        attachMethod.Invoke(wrapper, [externalRequest]);
        attachMethod.Invoke(wrapper, [crunchyrollRequest]);

        Assert.False(externalRequest.Headers.Contains("Cookie"));
        Assert.Equal(
            "session=secret",
            Assert.Single(crunchyrollRequest.Headers.GetValues("Cookie")));
    }

    [Theory]
    [InlineData("EPISODEJAJP", "EPISODE", true)]
    [InlineData("EPISODEES419", "EPISODE", true)]
    [InlineData("EPISODEOTHER", "EPISODE", false)]
    [InlineData("EPISODE2", "EPISODE", false)]
    public void QueueFileLookup_OnlyAcceptsKnownLegacyAudioSuffix(
        string historyEpisodeId,
        string baseEpisodeId,
        bool expected)
    {
        var matchMethod = typeof(QueueController).GetMethod(
            "IsLegacyAudioSuffixedEpisodeId",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(matchMethod);
        Assert.Equal(
            expected,
            matchMethod.Invoke(null, [historyEpisodeId, baseEpisodeId]));
    }

    [Fact]
    public void ConfigUpdate_DoesNotOverwriteStoredSecretsWithConfiguredMask()
    {
        var config = new CruncharrConfig();
        config.Sonarr.ApiKey = "stored-sonarr-key";
        config.Proxy.Password = "stored-proxy-password";
        config.Crunchyroll.StreamEndpoint.Authorization = "stored-primary-auth";
        config.Crunchyroll.StreamEndpointSecondary.Authorization = "stored-secondary-auth";
        using var client = new HttpClient(new RedirectHandler());
        var controller = new ConfigController(
            config,
            NullLogger<ConfigController>.Instance,
            new StubHttpClientFactory(client),
            Mock.Of<ISonarrService>(),
            Mock.Of<ILanguagePrefsService>(),
            Mock.Of<IQueueService>());
        var updateMethod = typeof(ConfigController).GetMethod(
            "UpdateConfigFromRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(updateMethod);
        updateMethod.Invoke(controller,
        [
            new ConfigUpdateRequest
            {
                Crunchyroll = new CrunchyrollUpdateConfig
                {
                    StreamEndpoint = new StreamEndpointConfig { Authorization = "" },
                    StreamEndpointSecondary = new StreamEndpointConfig { Authorization = "" }
                },
                Sonarr = new SonarrUpdateConfig { ApiKey = "[configured]" },
                Proxy = new ProxyUpdateConfig { Password = "[configured]" }
            }
        ]);

        Assert.Equal("stored-sonarr-key", config.Sonarr.ApiKey);
        Assert.Equal("stored-proxy-password", config.Proxy.Password);
        Assert.Equal("stored-primary-auth", config.Crunchyroll.StreamEndpoint.Authorization);
        Assert.Equal("stored-secondary-auth", config.Crunchyroll.StreamEndpointSecondary.Authorization);
    }

    [Fact]
    public void ConfigUpdate_AllowsExplicitlyClearingClearableFilenameFields()
    {
        var config = new CruncharrConfig();
        config.Download.FilenameTemplate = "{SeriesTitle}-custom";
        config.Download.FilenameWhitespaceSubstitute = "_";
        using var client = new HttpClient(new RedirectHandler());
        var controller = new ConfigController(
            config,
            NullLogger<ConfigController>.Instance,
            new StubHttpClientFactory(client),
            Mock.Of<ISonarrService>(),
            Mock.Of<ILanguagePrefsService>(),
            Mock.Of<IQueueService>());
        var updateMethod = typeof(ConfigController).GetMethod(
            "UpdateConfigFromRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(updateMethod);
        updateMethod.Invoke(controller,
        [
            new ConfigUpdateRequest
            {
                Download = new DownloadUpdateConfig
                {
                    FilenameTemplate = "",
                    FilenameWhitespaceSubstitute = ""
                }
            }
        ]);

        Assert.Empty(config.Download.FilenameTemplate);
        Assert.Empty(config.Download.FilenameWhitespaceSubstitute);
    }

    [Fact]
    public void ConfigReset_AppliesDefaultQueueLimitsToRunningService()
    {
        var config = new CruncharrConfig();
        config.Queue.SimultaneousProcessingJobs = 8;
        config.Queue.MaxSimultaneousTranscodes = 4;
        using var client = new HttpClient(new RedirectHandler());
        var queue = new Mock<IQueueService>();
        var controller = new ConfigController(
            config,
            NullLogger<ConfigController>.Instance,
            new StubHttpClientFactory(client),
            Mock.Of<ISonarrService>(),
            Mock.Of<ILanguagePrefsService>(),
            queue.Object);
        var configPath = Path.Combine(
            Path.GetTempPath(),
            $"cruncharr-reset-{Guid.NewGuid():N}.yaml");
        var previousConfigPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");

        try
        {
            Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", configPath);

            var result = controller.ResetConfig();

            Assert.IsType<OkObjectResult>(result);
            queue.Verify(q => q.SetProcessingLimit(2), Times.Once);
            queue.Verify(q => q.SetTranscodeLimit(1), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", previousConfigPath);
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(configPath + ".tmp")) File.Delete(configPath + ".tmp");
        }
    }

    [Fact]
    public async Task UpdateChecker_RemainsAliveWhenDisabled()
    {
        var config = new CruncharrConfig();
        config.Notifications.NotifyUpdateAvailable = false;
        using var client = new HttpClient(new RedirectHandler());
        var service = new UpdateCheckerService(
            null,
            new StubHttpClientFactory(client),
            config,
            Mock.Of<INotificationService>());

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask.IsCompleted);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationSaves_ToSharedPath_AreSerialized()
    {
        var configPath = Path.Combine(
            Path.GetTempPath(),
            $"cruncharr-concurrent-save-{Guid.NewGuid():N}.json");
        using var start = new ManualResetEventSlim(false);
        var saves = Enumerable.Range(0, 16)
            .Select(index => Task.Run(() =>
            {
                var config = new CruncharrConfig
                {
                    LogMode = index % 2 == 0,
                    Download = new DownloadConfig
                    {
                        OutputDirectory = $"{index}-" + new string('x', 100_000)
                    }
                };
                start.Wait(TestContext.Current.CancellationToken);
                return config.Save(configPath);
            }, TestContext.Current.CancellationToken))
            .ToArray();

        try
        {
            start.Set();
            Assert.All(await Task.WhenAll(saves), Assert.True);
            Assert.NotNull(CruncharrConfig.Load(configPath));
            Assert.False(File.Exists(configPath + ".tmp"));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(configPath + ".tmp")) File.Delete(configPath + ".tmp");
        }
    }

    [Fact]
    public void ConfigurationSave_AcceptsRelativePathWithoutDirectory()
    {
        var configPath = $"cruncharr-relative-{Guid.NewGuid():N}.json";
        try
        {
            Assert.True(new CruncharrConfig().Save(configPath));
            Assert.True(File.Exists(configPath));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(configPath + ".tmp")) File.Delete(configPath + ".tmp");
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://127.0.0.1/private") }
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? LastClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return client;
        }
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;
        public int RequestCount => _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            var status = statuses[Math.Min(index, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
