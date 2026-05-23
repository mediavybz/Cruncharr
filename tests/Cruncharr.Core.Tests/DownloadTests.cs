using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class DownloadTests{
    private readonly CrunchyrollAuthService _auth = new();
    private readonly CrunchyrollApiService _api;
    private readonly DownloadService _downloadService;

    public DownloadTests(){
        _api = new CrunchyrollApiService(_auth);
        _downloadService = new DownloadService(_auth, _api);
    }

    private (string Email, string Password) GetCredentials(){
        var email = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_EMAIL") ?? "";
        var password = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEST_PASSWORD") ?? "";
        return (email, password);
    }

    private bool HasCredentials(){
        var (email, password) = GetCredentials();
        return !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password);
    }

    [Fact]
    public async Task Download_Episode_WithPremiumAccount(){
        if (!HasCredentials()){
            return;
        }

        var (email, password) = GetCredentials();
        var loginResult = await _auth.LoginAsync(email, password, true);
        if (!loginResult){
            Console.WriteLine("Login failed");
            return;
        }

        // Search for a series
        var searchResults = await _api.SearchAsync("attack on titan", true);
        if (searchResults.Count == 0){
            Console.WriteLine("No search results");
            return;
        }

        // Get episodes
        var episodes = await _api.GetEpisodesAsync(searchResults[0].Id, true);
        if (episodes.Count == 0){
            Console.WriteLine("No episodes");
            return;
        }

        // Get first episode info
        var firstEpisode = episodes[0];
        Console.WriteLine($"Downloading: {firstEpisode.Title} (ID: {firstEpisode.Id})");
        Console.WriteLine($"Premium only: {firstEpisode.IsPremium}");

        // Create config
        var config = new CruncharrConfig{
            Download = new DownloadConfig{
                OutputDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_test"),
                TempDirectory = Path.Combine(Path.GetTempPath(), "cruncharr_test_temp"),
                Quality = "best",
                SkipMuxing = true // Don't mux, just test download
            }
        };

        // Clean up any previous test files
        if (Directory.Exists(config.Download.OutputDirectory)){
            Directory.Delete(config.Download.OutputDirectory, true);
        }
        if (Directory.Exists(config.Download.TempDirectory)){
            Directory.Delete(config.Download.TempDirectory, true);
        }

        try{
            var result = await _downloadService.DownloadEpisodeAsync(firstEpisode.Id, config);
            
            Console.WriteLine($"Download result: Success={result.Success}");
            if (!result.Success){
                Console.WriteLine($"Error: {result.ErrorMessage}");
            } else{
                Console.WriteLine($"Output: {result.OutputPath}");
            }
        } catch (Exception ex){
            Console.WriteLine($"Download exception: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}
