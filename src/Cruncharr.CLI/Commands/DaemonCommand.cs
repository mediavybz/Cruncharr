using System.CommandLine;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cruncharr.CLI;

public static class DaemonCommand
{
    public static Command Create(IServiceProvider provider)
    {
        var intervalOption = new Option<int>("--interval", () => 300, "Check interval in seconds");
        var command = new Command("daemon", "Run in daemon mode (continuously drain the download queue)"){
            intervalOption
        };

        command.SetHandler(async (int interval) =>
        {
            var logger = provider.GetRequiredService<ILogger<Program>>();
            var config = provider.GetRequiredService<CruncharrConfig>();
            var queueService = provider.GetRequiredService<IQueueService>();
            var downloadService = provider.GetRequiredService<IDownloadService>();
            var notificationService = provider.GetRequiredService<INotificationService>();

            logger.LogInformation("Cruncharr daemon starting. Interval: {Interval}s", interval);
            logger.LogInformation("Config file: {ConfigPath}", Program.GetConfigPath());
            logger.LogInformation("Output directory: {OutputDir}", config.Download.OutputDirectory);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogInformation("Shutdown signal received...");
                cts.Cancel();
            };

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Check for pending downloads in queue
                    var queue = queueService.GetQueue();
                    if (queue.Count > 0)
                    {
                        logger.LogInformation("Processing {Count} queued downloads", queue.Count);
                        await queueService.ProcessQueueAsync(config, null, cts.Token);
                    }

                    // Automatic history refresh / new-release checking is handled by the API
                    // server's AutoDownloadSchedulerService (the container's normal entrypoint),
                    // not by this CLI daemon — this loop only drains the download queue.

                    await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Daemon shutting down gracefully");
            }

        }, intervalOption);

        return command;
    }
}
