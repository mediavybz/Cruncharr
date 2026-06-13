using Cruncharr.API.Diagnostics;
using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils.Muxing.Syncing;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Cruncharr.API;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Capture recent logs in memory so the diagnostics API can surface them
        // (download failures etc.) without shell access to the container.
        var logStore = new InMemoryLogStore();
        builder.Services.AddSingleton(logStore);
        builder.Logging.AddProvider(new InMemoryLoggerProvider(logStore));

        // Many download utilities (HLS segment downloader, MPD parser) log via
        // Console.Write* instead of ILogger. Tee the console into the log store so
        // their errors are visible through /api/v1/diagnostics/logs. stderr is
        // captured in full; stdout only for problem-looking lines (avoids spam).
        Console.SetError(new ConsoleTeeWriter(Console.Error, logStore, "Console.Error", "Warning", captureAll: true));
        Console.SetOut(new ConsoleTeeWriter(Console.Out, logStore, "Console.Out", "Information", captureAll: false));

        // Add services to the container
        builder.Services.AddControllers()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.Converters.Add(new StringEnumConverter());
                options.SerializerSettings.ContractResolver = new PreserveDictionaryKeysContractResolver();
                options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new()
            {
                Title = "Cruncharr API",
                Version = "v1",
                Description = "Crunchyroll Downloader API for *arr stack integration"
            });
        });

        // Load configuration
        var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
        var config = File.Exists(configPath) ? CruncharrConfig.Load(configPath) : new CruncharrConfig();
        config.ApplyEnvironmentVariables();
        builder.Services.AddSingleton(config);

        // Initialize log mode if enabled
        if (config.LogMode)
        {
            LogManager.EnableLogMode("/config/logfile.txt");
        }

        // Register Cruncharr services
        builder.Services.AddSingleton<ICrunchyrollAuthService>(sp =>
            new CrunchyrollAuthService(config, sp.GetService<ILogger<CrunchyrollAuthService>>()));
        builder.Services.AddSingleton<ICrunchyrollApiService, CrunchyrollApiService>();
        builder.Services.AddSingleton<IDownloadService, DownloadService>();
        builder.Services.AddSingleton<IHistoryService, HistoryService>();
        builder.Services.AddSingleton<IQueuePersistenceService>(sp =>
            new QueuePersistenceService(config.Queue.QueueFilePath, sp.GetService<ILogger<QueuePersistenceService>>()));
        builder.Services.AddSingleton<ICalendarService, CalendarService>();
        builder.Services.AddSingleton<IQueueService, QueueService>();
        builder.Services.AddSingleton<QueueBroadcastService>();
        builder.Services.AddSingleton<ISonarrService, SonarrService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<ISyncingService, SyncingService>();
        builder.Services.AddSingleton<IVideoSyncer, VideoSyncer>();
        builder.Services.AddSingleton<IMovieService, MovieService>();
        builder.Services.AddSingleton<IMusicService, MusicService>();
        builder.Services.AddSingleton<IEncodingService, EncodingService>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<AutoDownloadSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDownloadSchedulerService>());
        builder.Services.AddSingleton<UpdateCheckerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateCheckerService>());

        // Add CORS - configurable via environment variable
        var corsOrigins = Environment.GetEnvironmentVariable("CORS_ORIGINS")?.Split(',') ?? new[] { "http://localhost:8585" };
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecific", policy =>
            {
                policy.WithOrigins(corsOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Only use HTTPS redirection if HTTPS is configured
        if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Contains("https:") == true)
        {
            app.UseHttpsRedirection();
        }
        app.UseCors("AllowSpecific");
        app.UseAuthorization();
        app.MapControllers();

        // Serve static files for web UI. Force HTML to always revalidate: without an
        // explicit Cache-Control browsers apply heuristic caching and can serve a stale
        // index.html for hours after an update (UI appears "frozen"/half-modernized).
        // no-cache keeps the ETag (cheap 304s) but never serves stale markup.
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                }
            }
        });

        // Ensure config directory exists
        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }

        // Ensure directories exist with validation
        var appLogger = app.Services.GetService<ILogger<Program>>();
        if (!string.IsNullOrWhiteSpace(config.Download.OutputDirectory))
        {
            try
            {
                Directory.CreateDirectory(config.Download.OutputDirectory);
            }
            catch (Exception ex)
            {
                appLogger?.LogError(ex, "Failed to create output directory: {Path}", config.Download.OutputDirectory);
            }
        }
        if (!string.IsNullOrWhiteSpace(config.Download.TempDirectory))
        {
            try
            {
                Directory.CreateDirectory(config.Download.TempDirectory);
            }
            catch (Exception ex)
            {
                appLogger?.LogError(ex, "Failed to create temp directory: {Path}", config.Download.TempDirectory);
            }
        }

        // Initialize auth FIRST - queue auto-download must wait for auth to be ready
        using (var scope = app.Services.CreateScope())
        {
            var authService = scope.ServiceProvider.GetRequiredService<ICrunchyrollAuthService>();
            var logger = scope.ServiceProvider.GetService<ILogger<Program>>();
            try
            {
                logger?.LogInformation("Initializing authentication...");
                var authResult = await authService.AuthenticateAsync(config.Crunchyroll?.UseBetaApi ?? true);
                if (authResult)
                {
                    logger?.LogInformation("Authentication successful - logged in as {User}", authService.Profile.Username);
                }
                else
                {
                    logger?.LogWarning("Authentication failed - anonymous mode");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Authentication initialization failed");
            }
        }

        // Start queue processing AFTER auth is initialized
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        using (var scope = app.Services.CreateScope())
        {
            var queueService = scope.ServiceProvider.GetRequiredService<IQueueService>();
            var configService = scope.ServiceProvider.GetRequiredService<CruncharrConfig>();
            var queueLogger = scope.ServiceProvider.GetService<ILogger<Program>>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await queueService.ProcessQueueAsync(configService, null, lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown
                }
                catch (Exception ex)
                {
                    queueLogger?.LogError(ex, "Queue processor crashed");
                }
            });
            // Set initialized flag after ProcessQueueAsync has started and assigned _config
            // Small delay to ensure ProcessQueueAsync has entered its main loop
            await Task.Delay(100);
            queueService.SetInitialized(true);
        }

        app.Run();
    }
}
