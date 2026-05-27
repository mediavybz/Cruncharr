using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils.Muxing.Syncing;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Cruncharr.API;

public class Program{
    public static async Task Main(string[] args){
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container
        builder.Services.AddControllers()
            .AddNewtonsoftJson(options =>{
                options.SerializerSettings.Converters.Add(new StringEnumConverter());
                options.SerializerSettings.ContractResolver = new PreserveDictionaryKeysContractResolver();
                options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>{
            c.SwaggerDoc("v1", new(){
                Title = "Cruncharr API",
                Version = "v1",
                Description = "Crunchyroll Downloader API for *arr stack integration"
            });
        });

        // Load configuration
        var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
        var config = File.Exists(configPath) ? CruncharrConfig.Load(configPath) : new CruncharrConfig();
        config.ValidateAndFix(); // Fix any corrupted config values from old bugs
        config.ApplyEnvironmentVariables();
        builder.Services.AddSingleton(config);
        
        // Initialize log mode if enabled
        if (config.LogMode){
            LogManager.EnableLogMode("/config/logfile.txt");
        }

        // Register Cruncharr services
        builder.Services.AddSingleton<ICrunchyrollAuthService>(sp =>
            new CrunchyrollAuthService(config, sp.GetService<ILogger<CrunchyrollAuthService>>()));
        builder.Services.AddSingleton<ICrunchyrollApiService, CrunchyrollApiService>();
        builder.Services.AddSingleton<IDownloadService, DownloadService>();
        builder.Services.AddSingleton<IHistoryService>(sp =>
            new HistoryService(
                null,
                sp.GetService<ILogger<HistoryService>>(),
                sp.GetService<ISonarrService>(),
                sp.GetService<ICrunchyrollApiService>(),
                sp.GetService<IMusicService>(),
                sp.GetService<ICrunchyrollAuthService>(),
                config));
        builder.Services.AddSingleton<IQueuePersistenceService>(
            _ => new QueuePersistenceService(config.Queue.QueueFilePath));
        builder.Services.AddSingleton<ICalendarService, CalendarService>();
        builder.Services.AddSingleton<IQueueService, QueueService>();
        builder.Services.AddSingleton<ISonarrService, SonarrService>();
        builder.Services.AddSingleton<ISyncingService, SyncingService>();
        builder.Services.AddSingleton<IVideoSyncer, VideoSyncer>();
        builder.Services.AddSingleton<IMovieService, MovieService>();
        builder.Services.AddSingleton<IMusicService, MusicService>();
        builder.Services.AddSingleton<IEncodingService, EncodingService>();

        // Add CORS for *arr integration
        builder.Services.AddCors(options =>{
            options.AddPolicy("AllowAll", policy =>{
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment()){
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseCors("AllowAll");
        app.UseAuthorization();
        app.MapControllers();

        // Serve static files for web UI
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Ensure config directory exists
        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir)){
            Directory.CreateDirectory(configDir);
        }

        // Ensure directories exist
        Directory.CreateDirectory(config.Download.OutputDirectory);
        Directory.CreateDirectory(config.Download.TempDirectory);

        // Initialize auth FIRST - queue auto-download must wait for auth to be ready
        using (var scope = app.Services.CreateScope()){
            var authService = scope.ServiceProvider.GetRequiredService<ICrunchyrollAuthService>();
            var logger = scope.ServiceProvider.GetService<ILogger<Program>>();
            try{
                logger?.LogInformation("Initializing authentication...");
                var authResult = await authService.AuthenticateAsync(config.Crunchyroll?.UseBetaApi ?? true);
                if (authResult){
                    logger?.LogInformation("Authentication successful - logged in as {User}", authService.Profile.Username);
                } else{
                    logger?.LogWarning("Authentication failed - anonymous mode");
                }
            } catch (Exception ex){
                logger?.LogError(ex, "Authentication initialization failed");
            }
        }

        // Start queue processing AFTER auth is initialized
        using (var scope = app.Services.CreateScope()){
            var queueService = scope.ServiceProvider.GetRequiredService<IQueueService>();
            var configService = scope.ServiceProvider.GetRequiredService<CruncharrConfig>();
            _ = queueService.ProcessQueueAsync(configService, null, CancellationToken.None);
        }

        app.Run();
    }
}
