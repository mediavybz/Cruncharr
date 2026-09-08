using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

[CollectionDefinition("ConfigurationEnvironment", DisableParallelization = true)]
public class ConfigurationEnvironmentCollection;

[Collection("ConfigurationEnvironment")]
public class SettingsAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");
    private readonly CruncharrConfig _config = new();
    private readonly Mock<IQueueService> _queue = new();
    private ConfigController Controller() => new(_config, NullLogger<ConfigController>.Instance,
        Mock.Of<IHttpClientFactory>(), Mock.Of<ISonarrService>(), Mock.Of<ILanguagePrefsService>(), _queue.Object);

    public SettingsAuditTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", Path.Combine(_root, "config.yaml"));
    }

    [Fact]
    public void SavingUnrelatedSettingsNeverAddsDefaultLanguages()
    {
        _config.Download.DubLanguages = ["en-US"];
        _config.Download.SoftSubs = ["none"];
        Assert.IsType<OkObjectResult>(Controller().UpdateConfig(new ConfigUpdateRequest { Queue = new QueueUpdateConfig { AutoDownload = false } }));
        Assert.Equal(["en-US"], _config.Download.DubLanguages);
        Assert.Equal(["none"], _config.Download.SoftSubs);
    }

    [Fact]
    public void HistoryQualityAndLanguagesApplyToTheDownloadWithoutChangingGlobalSettings()
    {
        _config.Download.DubLanguages = ["ja-JP"];
        var episode = new Cruncharr.Core.Models.EpisodeInfo { Id = "E", SeasonId = "S" };
        var series = new Cruncharr.Core.Models.HistorySeries
        {
            HistorySeriesVideoQualityOverride = "720",
            HistorySeriesDubLangOverride = ["fr-FR"],
            Seasons = [new Cruncharr.Core.Models.HistorySeason { SeasonId = "S", HistorySeasonVideoQualityOverride = "480", HistorySeasonDubLangOverride = ["en-US"] }]
        };
        var effective = DownloadService.ResolveEpisodeConfig(_config, episode, series);
        Assert.Equal("480", effective.Download.QualityVideo);
        Assert.Equal(["en-US"], episode.SelectedDubs);
        Assert.Equal(["ja-JP"], effective.Download.DubLanguages);
        Assert.Equal("best", _config.Download.QualityVideo);
        episode.VideoQuality = "1080";
        Assert.Equal("1080", DownloadService.ResolveEpisodeConfig(_config, episode, series).Download.QualityVideo);
    }

    [Fact]
    public void InvalidSettingsCannotPartiallyEnableAutomaticDownloads()
    {
        var result = Controller().UpdateConfig(new ConfigUpdateRequest
        {
            Queue = new QueueUpdateConfig { AutoDownload = true, SimultaneousProcessingJobs = 0 }
        });
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_config.Queue.AutoDownload);
        _queue.Verify(q => q.NotifyConfigChanged(), Times.Never);
    }

    [Fact]
    public void FailedPersistenceLeavesRuntimeSettingsAndLimitsUnchanged()
    {
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "file prevents directory creation");
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", Path.Combine(blocked, "config.yaml"));
        var result = Controller().UpdateConfig(new ConfigUpdateRequest
        {
            Queue = new QueueUpdateConfig { AutoDownload = true, SimultaneousProcessingJobs = 3 }
        });
        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(_config.Queue.AutoDownload);
        Assert.Equal(2, _config.Queue.SimultaneousProcessingJobs);
        _queue.Verify(q => q.NotifyConfigChanged(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothProcessingLimitControlsPersistTheSameEffectiveLimit(bool downloadTab)
    {
        var request = downloadTab
            ? new ConfigUpdateRequest { Download = new DownloadUpdateConfig { SimultaneousProcessingJobs = 4 } }
            : new ConfigUpdateRequest { Queue = new QueueUpdateConfig { SimultaneousProcessingJobs = 4 } };
        Assert.IsType<OkObjectResult>(Controller().UpdateConfig(request));
        var restored = CruncharrConfig.Load(Path.Combine(_root, "config.yaml"));
        Assert.Equal(4, restored.Queue.SimultaneousProcessingJobs);
        Assert.Equal(4, restored.Download.SimultaneousProcessingJobs);
        _queue.Verify(q => q.SetProcessingLimit(4), Times.Once);
        _queue.Verify(q => q.NotifyConfigChanged(), Times.Once);
    }

    [Fact]
    public void ClearedOptionalFieldsStayClearedAfterReload()
    {
        _config.Sonarr.UrlBase = "/sonarr";
        _config.Appearance.BackgroundImagePath = "/config/old.jpg";
        _config.Proxy.Username = "name";
        Assert.IsType<OkObjectResult>(Controller().UpdateConfig(new ConfigUpdateRequest
        {
            Sonarr = new SonarrUpdateConfig { UrlBase = "" },
            Appearance = new AppearanceUpdateConfig { BackgroundImagePath = "", BackgroundImageOpacity = 0 },
            Proxy = new ProxyUpdateConfig { Username = "", Password = "" }
        }));
        var restored = CruncharrConfig.Load(Path.Combine(_root, "config.yaml"));
        Assert.Empty(restored.Sonarr.UrlBase!);
        Assert.Empty(restored.Appearance.BackgroundImagePath!);
        Assert.Empty(restored.Proxy.Username!);
        Assert.Equal(0, restored.Appearance.BackgroundImageOpacity);
    }

    [Fact]
    public void CachedProxyCredentialsResolveCurrentSettingsAfterEnableAndPasswordChange()
    {
        var config = new ProxyConfig { Enabled = false };
        var proxy = new HttpClientWrapper.ConfiguredProxy(() => config);
        var cachedCredentials = proxy.Credentials!;
        var uri = new Uri("http://proxy.example:8080");
        Assert.Null(cachedCredentials.GetCredential(uri, "Basic"));
        config.Enabled = true; config.Username = "test-user"; config.Password = "first-test-password";
        Assert.Equal("first-test-password", cachedCredentials.GetCredential(uri, "Basic")!.Password);
        config.Password = "second-test-password";
        Assert.Equal("second-test-password", cachedCredentials.GetCredential(uri, "Basic")!.Password);
        config.Enabled = false;
        Assert.Null(cachedCredentials.GetCredential(uri, "Basic"));
    }

    [Fact]
    public void RuntimeProxyRespondsToSavedEnableHostAndScopeChanges()
    {
        var config = new ProxyConfig { Host = "proxy-one", Port = 8080, Enabled = false };
        var proxy = new HttpClientWrapper.ConfiguredProxy(() => config);
        var cr = new Uri("https://www.crunchyroll.com/");
        var other = new Uri("https://example.com/");
        Assert.True(proxy.IsBypassed(cr));
        config.Enabled = true;
        Assert.Equal("proxy-one", proxy.GetProxy(cr)!.Host);
        config.Host = "proxy-two";
        config.AllTraffic = false;
        Assert.True(proxy.IsBypassed(other));
        Assert.Equal("proxy-two", proxy.GetProxy(cr)!.Host);
        config.Enabled = false;
        Assert.True(proxy.IsBypassed(cr));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
