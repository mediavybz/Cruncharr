using Cruncharr.API.Controllers;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

public class HistoryArtifactAvailabilityTests
{
    [Fact]
    public async Task RichHistory_ExposesMissingCompletedArtifactForRedownload()
    {
        var response = await GetEpisodeResponseAsync(
            recordedOutputPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mkv"),
            persistedSonarrHasFile: false,
            currentSonarrHasFile: false);

        Assert.True(response.WasDownloaded);
        Assert.False(response.HasCompletedArtifact);
    }

    [Fact]
    public async Task RichHistory_RecognizesExistingCruncharrArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"existing-{Guid.NewGuid():N}.mkv");
        try
        {
            await File.WriteAllTextAsync(path, "artifact", TestContext.Current.CancellationToken);

            var response = await GetEpisodeResponseAsync(path, persistedSonarrHasFile: false, currentSonarrHasFile: false);

            Assert.True(response.HasLocalArtifact);
            Assert.True(response.HasCompletedArtifact);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RichHistory_RecognizesSonarrManagedArtifactWhenOriginalPathMoved()
    {
        var response = await GetEpisodeResponseAsync(
            recordedOutputPath: Path.Combine(Path.GetTempPath(), $"imported-{Guid.NewGuid():N}.mkv"),
            persistedSonarrHasFile: false,
            currentSonarrHasFile: true);

        Assert.False(response.HasLocalArtifact);
        Assert.True(response.SonarrHasFile);
        Assert.True(response.HasCompletedArtifact);
    }

    [Fact]
    public async Task RichHistory_IgnoresStalePersistedSonarrTrueWhenDirectSonarrReportsMissing()
    {
        var response = await GetEpisodeResponseAsync(
            recordedOutputPath: Path.Combine(Path.GetTempPath(), $"deleted-{Guid.NewGuid():N}.mkv"),
            persistedSonarrHasFile: true,
            currentSonarrHasFile: false);

        Assert.False(response.SonarrHasFile);
        Assert.False(response.HasCompletedArtifact);
    }

    [Fact]
    public async Task IsDownloadedAsync_RequiresReferencedArtifactToStillExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cruncharr-history-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, "episode.mkv");
        try
        {
            await File.WriteAllTextAsync(artifact, "artifact", TestContext.Current.CancellationToken);
            using var history = new HistoryService(Path.Combine(directory, "rich.json"));
            await history.AddAsync(new DownloadHistory
            {
                EpisodeId = "CHECK-EP1",
                AudioLanguage = "en-US",
                OutputPath = artifact
            });

            Assert.True(await history.IsDownloadedAsync("CHECK-EP1", "en-US"));

            File.Delete(artifact);

            Assert.False(await history.IsDownloadedAsync("CHECK-EP1", "en-US"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<HistoryEpisodeResponse> GetEpisodeResponseAsync(
        string recordedOutputPath,
        bool persistedSonarrHasFile,
        bool currentSonarrHasFile)
    {
        var richEpisode = new HistoryEpisode
        {
            EpisodeId = "HISTORY-EP1",
            Episode = "1",
            WasDownloaded = true,
            SonarrEpisodeId = "100",
            SonarrHasFile = persistedSonarrHasFile
        };
        var richSeries = new HistorySeries
        {
            SeriesId = "HISTORY-SERIES",
            SonarrSeriesId = "10",
            Seasons = [new HistorySeason { SeasonId = "HISTORY-SEASON", EpisodesList = [richEpisode] }]
        };
        var history = new Mock<IHistoryService>();
        history.Setup(service => service.GetHistorySeriesAsync()).ReturnsAsync([richSeries]);
        history.Setup(service => service.GetAllAsync(0, int.MaxValue)).ReturnsAsync(
        [
            new DownloadHistory
            {
                EpisodeId = richEpisode.EpisodeId!,
                AudioLanguage = "en-US",
                OutputPath = recordedOutputPath
            }
        ]);
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(service => service.GetEpisodesAsync(
                10,
                It.IsAny<Cruncharr.Core.Configuration.SonarrConfig>(),
                true))
            .ReturnsAsync(
            [
                new SonarrEpisode { Id = 100, SeriesId = 10, HasFile = currentSonarrHasFile }
            ]);
        var config = new Cruncharr.Core.Configuration.CruncharrConfig();
        config.Sonarr.Enabled = true;
        // Artifact safety intentionally does not depend on CountSonarr display/count semantics.
        config.History.CountSonarr = false;
        var controller = new HistoryController(
            history.Object,
            NullLogger<HistoryController>.Instance,
            sonarr.Object,
            config);

        var action = await controller.GetRichHistory();
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var series = Assert.IsType<List<HistorySeriesResponse>>(ok.Value);
        return Assert.Single(Assert.Single(series).Seasons).Episodes.Single();
    }
}
