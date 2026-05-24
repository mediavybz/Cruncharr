using System.CommandLine;
using System.Text.Json;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cruncharr.CLI;

class Program{
    static async Task<int> Main(string[] args){
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ICrunchyrollAuthService, CrunchyrollAuthService>();
        services.AddSingleton<ICrunchyrollApiService, CrunchyrollApiService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<INotificationService, NotificationService>();
        
        var provider = services.BuildServiceProvider();
        
        var rootCommand = new RootCommand("Cruncharr - Headless Crunchyroll downloader for *arr stacks");
        
        rootCommand.AddCommand(CreateLoginCommand(provider));
        rootCommand.AddCommand(CreateLogoutCommand(provider));
        rootCommand.AddCommand(CreateDownloadCommand(provider));
        rootCommand.AddCommand(CreateSeriesCommand(provider));
        rootCommand.AddCommand(CreateSearchCommand(provider));
        rootCommand.AddCommand(CreateConfigCommand());
        rootCommand.AddCommand(DaemonCommand.Create(provider));
        
        return await rootCommand.InvokeAsync(args);
    }
    
    static Command CreateLoginCommand(IServiceProvider provider){
        var emailOption = new Option<string>("--email", "Crunchyroll email") { IsRequired = true };
        var passwordOption = new Option<string>("--password", "Crunchyroll password") { IsRequired = true };
        
        var command = new Command("login", "Authenticate with Crunchyroll"){
            emailOption,
            passwordOption
        };
        
        command.SetHandler(async (string email, string password) => {
            var auth = provider.GetRequiredService<IAuthenticationService>();
            var result = await auth.LoginAsync(email, password);
            if (result){
                Console.WriteLine("Login successful");
                // Save credentials to config
                var config = LoadConfig();
                config.Crunchyroll.Email = email;
                config.Crunchyroll.Password = password;
                SaveConfig(config);
            } else{
                Console.Error.WriteLine("Login failed");
                Environment.ExitCode = 1;
            }
        }, emailOption, passwordOption);
        
        return command;
    }
    
    static Command CreateLogoutCommand(IServiceProvider provider){
        var command = new Command("logout", "Clear saved credentials");
        command.SetHandler(async () => {
            var auth = provider.GetRequiredService<IAuthenticationService>();
            await auth.LogoutAsync();
            var config = LoadConfig();
                config.Crunchyroll.Email = "";
                config.Crunchyroll.Password = "";
            SaveConfig(config);
            Console.WriteLine("Logged out");
        });
        return command;
    }
    
    static Command CreateDownloadCommand(IServiceProvider provider){
        var urlArgument = new Argument<string>("url", "Episode or series URL");
        var formatOption = new Option<string>("--format", () => "human", "Output format: human, json") { Arity = ArgumentArity.ZeroOrOne };
        var quietOption = new Option<bool>("--quiet", () => false, "Only output exit codes");
        var outputOption = new Option<string?>("--output", "Output directory override");
        
        var command = new Command("download", "Download episode or series"){
            urlArgument,
            formatOption,
            quietOption,
            outputOption
        };
        
        command.SetHandler(async (string url, string format, bool quiet, string? output) => {
            var config = LoadConfig();
            if (!string.IsNullOrEmpty(output)) config.Download.OutputDirectory = output;
            
            var downloadService = provider.GetRequiredService<IDownloadService>();
            var progress = quiet ? null : new Progress<DownloadProgress>(p => {
                if (format == "json") return;
                Console.WriteLine($"[{p.State}] {p.Percent:F1}% - {p.Doing}");
            });
            
            try{
                // Fetch episode info first
                var api = provider.GetRequiredService<ICrunchyrollApiService>();
                var episode = await api.GetEpisodeAsync(url, true);
                if (episode == null){
                    Console.Error.WriteLine("Failed to fetch episode info");
                    Environment.ExitCode = 1;
                    return;
                }
                
                var result = await downloadService.DownloadEpisodeAsync(episode, config, progress);
                if (format == "json"){
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                } else if (!quiet){
                    Console.WriteLine(result.Success ? $"Download complete: {result.OutputPath}" : $"Download failed: {result.ErrorMessage}");
                }
                
                Environment.ExitCode = result.Success ? 0 : 1;
            } catch (Exception ex){
                if (!quiet){
                    if (format == "json"){
                        Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
                    } else{
                        Console.Error.WriteLine($"Error: {ex.Message}");
                    }
                }
                Environment.ExitCode = 1;
            }
        }, urlArgument, formatOption, quietOption, outputOption);
        
        return command;
    }
    
    static Command CreateSeriesCommand(IServiceProvider provider){
        var idArgument = new Argument<string>("id", "Series ID or URL");
        var formatOption = new Option<string>("--format", () => "human", "Output format: human, json");
        var downloadOption = new Option<bool>("--download", () => false, "Download all episodes");
        
        var command = new Command("series", "Get series information"){
            idArgument,
            formatOption,
            downloadOption
        };
        
        command.SetHandler(async (string id, string format, bool download) => {
            var search = provider.GetRequiredService<ISearchService>();
            var series = await search.GetSeriesAsync(id);
            
            if (series == null){
                Console.Error.WriteLine("Series not found");
                Environment.ExitCode = 1;
                return;
            }
            
            if (format == "json"){
                Console.WriteLine(JsonSerializer.Serialize(series, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            } else{
                Console.WriteLine($"Series: {series.Title}");
                Console.WriteLine($"ID: {series.Id}");
                Console.WriteLine($"Seasons: {series.Seasons.Count}");
                foreach (var season in series.Seasons){
                    Console.WriteLine($"  Season {season.SeasonNumber}: {season.Title} ({season.Episodes.Count} episodes)");
                }
            }
            
            if (download){
                var config = LoadConfig();
                var downloadService = provider.GetRequiredService<IDownloadService>();
                await downloadService.DownloadSeriesAsync(id, config);
            }
        }, idArgument, formatOption, downloadOption);
        
        return command;
    }
    
    static Command CreateSearchCommand(IServiceProvider provider){
        var queryArgument = new Argument<string>("query", "Search query");
        var formatOption = new Option<string>("--format", () => "human", "Output format: human, json");
        
        var command = new Command("search", "Search for series"){
            queryArgument,
            formatOption
        };
        
        command.SetHandler(async (string query, string format) => {
            var search = provider.GetRequiredService<ISearchService>();
            var results = await search.SearchAsync(query);
            
            if (format == "json"){
                Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            } else{
                foreach (var series in results){
                    Console.WriteLine($"{series.Id}: {series.Title}");
                }
            }
        }, queryArgument, formatOption);
        
        return command;
    }
    
    static Command CreateConfigCommand(){
        var command = new Command("config", "View or edit configuration");
        
        var getCommand = new Command("get", "Get configuration value");
        var getKeyArg = new Argument<string>("key", "Configuration key");
        getCommand.Add(getKeyArg);
        getCommand.SetHandler((string key) => {
            var config = LoadConfig();
            var prop = typeof(CruncharrConfig).GetProperty(key);
            if (prop != null){
                Console.WriteLine(prop.GetValue(config));
            }
        }, getKeyArg);
        
        var setCommand = new Command("set", "Set configuration value");
        var setKeyArg = new Argument<string>("key", "Configuration key");
        var setValueArg = new Argument<string>("value", "Configuration value");
        setCommand.Add(setKeyArg);
        setCommand.Add(setValueArg);
        setCommand.SetHandler((string key, string value) => {
            var config = LoadConfig();
            var prop = typeof(CruncharrConfig).GetProperty(key);
            if (prop != null){
                prop.SetValue(config, value);
                SaveConfig(config);
                Console.WriteLine($"Set {key} = {value}");
            }
        }, setKeyArg, setValueArg);
        
        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        
        return command;
    }
    
    static string GetConfigPath(){
        var configDir = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_DIR") ?? "/config";
        return Path.Combine(configDir, "cruncharr.json");
    }
    
    static CruncharrConfig LoadConfig(){
        var config = CruncharrConfig.Load(GetConfigPath());
        config.ApplyEnvironmentVariables();
        return config;
    }
    
    static void SaveConfig(CruncharrConfig config){
        var configPath = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        config.Save(configPath);
    }
}
