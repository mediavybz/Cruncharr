using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.Muxing.Syncing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cruncharr.CLI;

/// <summary>
/// The CLI's service graph. Every command resolves its services from here, so this has to cover
/// everything those services need (the HTTP client factory, the loaded config) and point the
/// optional download services (queue file, History, encoding presets) at the chosen config dir.
/// </summary>
public static class CliServices
{
    public static IServiceCollection AddCruncharrCli(this IServiceCollection services, string configPath)
    {
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";

        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddHttpClient();
        services.AddHttpClient("CruncharrWebhooks", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(WebhookUrlValidator.CreateHttpMessageHandler);

        // Loaded on first use, so `--help` and `config` still work when the file is broken.
        services.AddSingleton(_ =>
        {
            var config = CruncharrConfig.Load(configPath);
            config.ApplyEnvironmentVariables();
            config.KeepStateNextToConfig(configPath);
            return config;
        });
        services.AddSingleton<ICrunchyrollAuthService>(sp => new CrunchyrollAuthService(
            sp.GetRequiredService<CruncharrConfig>(), sp.GetService<ILogger<CrunchyrollAuthService>>(), sp.GetService<INotificationService>()));
        services.AddSingleton<ICrunchyrollApiService>(sp => new CrunchyrollApiService(
            sp.GetRequiredService<ICrunchyrollAuthService>(), sp.GetRequiredService<CruncharrConfig>(), sp.GetService<ILogger<CrunchyrollApiService>>()));
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IQueuePersistenceService>(sp =>
        {
            var config = sp.GetRequiredService<CruncharrConfig>();
            return new QueuePersistenceService(config.Queue.QueueFilePath, sp.GetService<ILogger<QueuePersistenceService>>(), config);
        });
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<IHistoryService>(sp => new HistoryService(
            Path.Combine(configDir, "history.json"), sp.GetService<ILogger<HistoryService>>(), sp.GetService<ISonarrService>(),
            sp.GetService<ICrunchyrollApiService>(), sp.GetService<ICrunchyrollAuthService>(), sp.GetRequiredService<CruncharrConfig>()));
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ISonarrService, SonarrService>();
        services.AddSingleton<ISyncingService, SyncingService>();
        services.AddSingleton<IVideoSyncer, VideoSyncer>();
        services.AddSingleton<IEncodingService>(sp => new EncodingService(
            sp.GetService<ILogger<EncodingService>>(), Path.Combine(configDir, "encoding-presets")));
        return services;
    }
}
