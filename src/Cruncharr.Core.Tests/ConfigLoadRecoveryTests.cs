using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

/// <summary>
/// An existing config file that cannot be loaded must never look like a fresh install whose
/// defaults a later save writes over the user's real settings.
/// </summary>
[Collection("ConfigurationEnvironment")]
public class ConfigLoadRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "config-load-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");

    public ConfigLoadRecoveryTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("config.yaml", "download:\n  output_directory: /media/anime\n  dub_languages: [en-US\n")]
    [InlineData("config.json", "{ \"download\": { \"output_directory\": \"/media/anime\" ")]
    public void InvalidFile_RefusesSavesAndKeepsTheOriginal(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);

        Assert.Throws<ConfigLoadException>(() => CruncharrConfig.Load(path));

        var config = CruncharrConfig.LoadOrRecover(path, out var error);
        Assert.NotNull(error);
        Assert.Contains(path, error);
        Assert.Equal(error, CruncharrConfig.GetLoadError(path));

        config.LogMode = true;
        var refused = Assert.Throws<InvalidOperationException>(() => config.Save(path));
        Assert.Equal(error, refused.Message);
        Assert.Throws<InvalidOperationException>(() => config.Clone().Save(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableFile_RefusesSavesAndKeepsTheOriginal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs mandatory file locking");
        var path = Path.Combine(_root, "config.yaml");
        File.WriteAllText(path, "log_mode: true\n");

        string? error;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<ConfigLoadException>(() => CruncharrConfig.Load(path));
            CruncharrConfig.LoadOrRecover(path, out error);
        }

        Assert.NotNull(error);
        Assert.Throws<InvalidOperationException>(() => new CruncharrConfig().Save(path));
        Assert.Equal("log_mode: true\n", File.ReadAllText(path));
    }

    [Fact]
    public void SettingsSave_IsRejectedWhileTheConfigFileIsInvalid()
    {
        var path = Path.Combine(_root, "config.yaml");
        const string content = "crunchyroll:\n  email: [user@example.com\n";
        File.WriteAllText(path, content);
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", path);
        var config = CruncharrConfig.LoadOrRecover(path, out _);
        var controller = new ConfigController(config, NullLogger<ConfigController>.Instance,
            Mock.Of<IHttpClientFactory>(), Mock.Of<ISonarrService>(), Mock.Of<ILanguagePrefsService>(), Mock.Of<IQueueService>());

        var dto = Assert.IsType<OkObjectResult>(controller.GetConfig()).Value!;
        Assert.Equal(CruncharrConfig.GetLoadError(path), dto.GetType().GetProperty("LoadError")!.GetValue(dto));

        var save = controller.UpdateConfig(new ConfigUpdateRequest { General = new GeneralUpdateConfig { LogMode = true } });
        Assert.Equal(500, Assert.IsType<ObjectResult>(save).StatusCode);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(500, Assert.IsType<ObjectResult>(controller.ResetConfig()).StatusCode);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void EmptySections_TakeDefaultsAndKeepTheOtherSettings()
    {
        var path = Path.Combine(_root, "config.yaml");
        File.WriteAllText(path, "download:\nqueue:\nsonarr:\n  host: sonarr.local\nlog_mode: true\n");

        var config = CruncharrConfig.Load(path);

        Assert.NotNull(config.Download);
        Assert.NotNull(config.Queue);
        Assert.Equal("sonarr.local", config.Sonarr.Host);
        Assert.True(config.LogMode);
    }

    [Fact]
    public void MissingFile_IsAFreshInstallThatCanBeSaved()
    {
        var path = Path.Combine(_root, "config.yaml");

        var config = CruncharrConfig.LoadOrRecover(path, out var error);

        Assert.Null(error);
        Assert.Null(CruncharrConfig.GetLoadError(path));
        config.LogMode = true;
        Assert.True(config.Save(path));
        Assert.True(CruncharrConfig.Load(path).LogMode);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", _previous);
        try { Directory.Delete(_root, true); } catch { }
    }
}
