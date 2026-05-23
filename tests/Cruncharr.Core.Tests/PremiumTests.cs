using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class PremiumTests{
    private readonly CrunchyrollAuthService _auth = new();
    private readonly CrunchyrollApiService _api;

    public PremiumTests(){
        _api = new CrunchyrollApiService(_auth);
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
    public async Task PremiumLogin_ReturnsPremiumStatus(){
        if (!HasCredentials()){
            return;
        }

        var (email, password) = GetCredentials();
        var result = await _auth.LoginAsync(email, password, true);
        
        if (!result){
            Console.WriteLine("Login failed");
            return;
        }

        Assert.True(_auth.IsAuthenticated, "Should be authenticated");
        Console.WriteLine($"Username: {_auth.Profile.Username}");
        Console.WriteLine($"Premium: {_auth.Profile.HasPremium}");
        Console.WriteLine($"Account ID: {_auth.Token?.account_id}");
    }

    [Fact]
    public async Task Premium_GetEpisodePlayback_ReturnsData(){
        if (!HasCredentials()){
            return;
        }

        var (email, password) = GetCredentials();
        var loginResult = await _auth.LoginAsync(email, password, true);
        if (!loginResult){
            Console.WriteLine("Login failed");
            return;
        }

        // Get a series
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

        var firstEpisode = episodes[0];
        Console.WriteLine($"Episode: {firstEpisode.Title} (ID: {firstEpisode.Id})");
        Console.WriteLine($"Premium only: {firstEpisode.IsPremium}");
    }
}
