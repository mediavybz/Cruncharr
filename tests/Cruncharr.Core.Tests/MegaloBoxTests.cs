using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class MegaloBoxTests{
    private readonly CrunchyrollAuthService _auth = new();
    private readonly CrunchyrollApiService _api;
    private readonly DownloadService _downloadService;

    public MegaloBoxTests(){
        _api = new CrunchyrollApiService(_auth);
        _downloadService = new DownloadService(_auth, _api);
    }

    private (string Email, string Password) GetCredentials(){
        var email = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_EMAIL") ?? "";
        var password = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_PASSWORD") ?? "";
        return (email, password);
    }

    [Fact]
    public async Task Download_MegaloBox_FirstEpisode(){
        var (email, password) = GetCredentials();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)){
            Console.WriteLine("No credentials set");
            return;
        }

        // Login
        var loginResult = await _auth.LoginAsync(email, password, true);
        if (!loginResult){
            Console.WriteLine("Login failed");
            return;
        }
        Console.WriteLine($"Logged in as: {_auth.Profile.Username}, Premium: {_auth.Profile.HasPremium}");

        // Get episodes for MegaloBox
        var episodes = await _api.GetEpisodesAsync("GR4PVJ1WY", true);
        Console.WriteLine($"Found {episodes.Count} episodes");
        
        if (episodes.Count == 0){
            Console.WriteLine("No episodes found");
            return;
        }

        // Find first non-PV episode
        var targetEpisode = episodes.FirstOrDefault(e => !e.Title.Contains("PV", StringComparison.OrdinalIgnoreCase) 
            && !e.Title.Contains("Preview", StringComparison.OrdinalIgnoreCase))
            ?? episodes[0];
        
        Console.WriteLine($"Target episode: {targetEpisode.Title} (ID: {targetEpisode.Id})");
        Console.WriteLine($"Episode number: {targetEpisode.EpisodeNumber}, Season: {targetEpisode.SeasonNumber}");
        Console.WriteLine($"Premium only: {targetEpisode.IsPremium}");

        // Setup config
        var config = new CruncharrConfig{
            Download = new DownloadConfig{
                OutputDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_megalobox"),
                TempDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_megalobox_temp"),
                Quality = "best",
                SkipMuxing = false // Enable muxing
            }
        };

        // Clean up previous test
        if (Directory.Exists(config.Download.OutputDirectory)){
            Directory.Delete(config.Download.OutputDirectory, true);
        }

        try{
            var result = await _downloadService.DownloadEpisodeAsync(targetEpisode.Id, config);
            
            Console.WriteLine($"Download result: Success={result.Success}");
            if (result.Success){
                Console.WriteLine($"Output file: {result.OutputPath}");
                if (File.Exists(result.OutputPath)){
                    var fileInfo = new FileInfo(result.OutputPath);
                    Console.WriteLine($"File size: {fileInfo.Length} bytes ({fileInfo.Length / 1024 / 1024} MB)");
                } else{
                    Console.WriteLine("Output file not found!");
                }
            } else{
                Console.WriteLine($"Error: {result.ErrorMessage}");
            }
        } catch (Exception ex){
            Console.WriteLine($"Exception: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}
