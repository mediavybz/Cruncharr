using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class MegaloBoxDebugTests{
    private readonly CrunchyrollAuthService _auth = new();
    private readonly CrunchyrollApiService _api;
    private readonly DownloadService _downloadService;

    public MegaloBoxDebugTests(){
        _api = new CrunchyrollApiService(_auth);
        _downloadService = new DownloadService(_auth, _api);
    }

    private (string Email, string Password) GetCredentials(){
        var email = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_EMAIL") ?? "";
        var password = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_PASSWORD") ?? "";
        return (email, password);
    }

    [Fact]
    public async Task Debug_MegaloBox_PlaybackData(){
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

        // Get episode info to verify
        var episodeInfo = await _api.GetEpisodeAsync(targetEpisode.Id, true);
        Console.WriteLine($"Episode info: {episodeInfo?.Title ?? "NULL"}");

        // Try to get playback data manually
        var config = new CruncharrConfig{
            Download = new DownloadConfig{
                OutputDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_debug"),
                TempDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_debug_temp"),
                Quality = "best",
                SkipMuxing = true
            }
        };

        // Clean up
        if (Directory.Exists(config.Download.OutputDirectory)){
            Directory.Delete(config.Download.OutputDirectory, true);
        }

        var result = await _downloadService.DownloadEpisodeAsync(targetEpisode.Id, config);
        Console.WriteLine($"Download Success: {result.Success}");
        Console.WriteLine($"Error: {result.ErrorMessage ?? "None"}");
        Console.WriteLine($"Output: {result.OutputPath ?? "None"}");

        // Check temp directory
        if (Directory.Exists(config.Download.TempDirectory)){
            var tempFiles = Directory.GetFiles(config.Download.TempDirectory, "*", SearchOption.AllDirectories);
            Console.WriteLine($"Temp files found: {tempFiles.Length}");
            foreach (var file in tempFiles){
                Console.WriteLine($"  - {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
            }
        }
    }
}
