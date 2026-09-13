using System.Text.Json;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cruncharr.Core.Tests;

public class HistorySettingsOverrideTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cruncharr-overrides-{Guid.NewGuid():N}");
    private string HistoryPath => Path.Combine(_directory, "history.json");

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(_directory);
        var history = new List<HistorySeries>
        {
            new() { SeriesId = "SERIES", Seasons = [new HistorySeason
            { SeasonId = "SEASON", EpisodesList = [new HistoryEpisode { EpisodeId = "EPISODE" }] }] }
        };
        await File.WriteAllTextAsync(HistoryPath, JsonSerializer.Serialize(history), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Overrides_PersistAcrossRestartAreReturnedByApiAndSeasonTakesPrecedence()
    {
        await SeedAsync();
        using (var service = new HistoryService(HistoryPath))
        {
            await service.SetSeriesSettingsOverrideAsync("SERIES", "720", ["en-US"], ["fr-FR"]);
            await service.SetSeasonSettingsOverrideAsync("SEASON", "1080", ["de-DE"], ["en-US"]);
        }
        using var reloaded = new HistoryService(HistoryPath);
        var controller = new HistoryController(reloaded, NullLogger<HistoryController>.Instance);
        var action = await controller.GetSeriesHistory("SERIES");
        var response = Assert.IsType<HistorySeriesResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal("720", response.SettingsOverride.VideoQuality);
        Assert.Equal(new[] { "en-US" }, response.SettingsOverride.DubLanguages);
        Assert.Equal(new[] { "fr-FR" }, response.SettingsOverride.SoftSubs);
        Assert.Equal(new[] { "de-DE" }, response.Seasons[0].SettingsOverride.DubLanguages);
        var resolved = await reloaded.GetHistoryEpisodeWithDubListAndDownloadDirAsync("SERIES", "SEASON", "EPISODE");
        Assert.Equal(new[] { "de-DE" }, resolved.DubList);
        Assert.Equal(new[] { "en-US" }, resolved.SubList);
        Assert.Equal("1080", resolved.VideoQuality);

        await reloaded.SetSeasonSettingsOverrideAsync("SEASON", "", [], []);
        resolved = await reloaded.GetHistoryEpisodeWithDubListAndDownloadDirAsync("SERIES", "SEASON", "EPISODE");
        Assert.Equal(new[] { "en-US" }, resolved.DubList);
        Assert.Equal(new[] { "fr-FR" }, resolved.SubList);
        Assert.Equal("720", resolved.VideoQuality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSave_ReportsFailureAndRestoresPreviousSettings(bool season)
    {
        await SeedAsync();
        using var service = new HistoryService(HistoryPath);
        await service.SetSeriesSettingsOverrideAsync("SERIES", "720", ["en-US"], ["fr-FR"]);
        Directory.CreateDirectory(HistoryPath + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => season
            ? service.SetSeasonSettingsOverrideAsync("SEASON", "1080", ["de-DE"], [])
            : service.SetSeriesSettingsOverrideAsync("SERIES", "1080", ["de-DE"], []));
        var resolved = await service.GetHistoryEpisodeWithDubListAndDownloadDirAsync("SERIES", "SEASON", "EPISODE");
        Assert.Equal("720", resolved.VideoQuality);
        Assert.Equal(new[] { "en-US" }, resolved.DubList);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
