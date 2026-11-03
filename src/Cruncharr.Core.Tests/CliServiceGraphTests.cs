using System.Reflection;
using Cruncharr.CLI;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cruncharr.Core.Tests;

// Audit F08: the CLI registered services that need IHttpClientFactory without registering it, so
// `search` (and every command reaching NotificationService/SonarrService) failed at runtime. It
// also never registered the loaded config, so services ran on defaults and ApplicationData paths.
public class CliServiceGraphTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-cli-graph-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void EveryCommandService_ResolvesWithoutNetworkAndUsesTheChosenConfig()
    {
        var configPath = Path.Combine(_root.FullName, "cruncharr.yaml");
        var media = Path.Combine(_root.FullName, "media");
        File.WriteAllText(configPath, $"download:\n  output_dir: '{media}'\n");

        using var provider = new ServiceCollection().AddCruncharrCli(configPath)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // login/logout, search, series, download and daemon.
        foreach (var service in new[]
                 {
                     typeof(IAuthenticationService), typeof(ISearchService), typeof(ICrunchyrollApiService),
                     typeof(IDownloadService), typeof(IQueueService), typeof(INotificationService), typeof(ISonarrService)
                 })
        {
            Assert.NotNull(provider.GetRequiredService(service));
        }

        var config = provider.GetRequiredService<CruncharrConfig>();
        Assert.Equal(media, config.Download.OutputDirectory);
        Assert.Equal(Path.Combine(_root.FullName, "token.json"), config.TokenFilePath);

        // The auth service saves its session next to the chosen config.
        var auth = provider.GetRequiredService<ICrunchyrollAuthService>();
        typeof(CrunchyrollAuthService).GetProperty(nameof(auth.Token))!.SetValue(auth, new CrToken { access_token = "a", refresh_token = "r" });
        auth.SaveToken();
        Assert.True(File.Exists(Path.Combine(_root.FullName, "token_tv.json")));
    }
}
