using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Tests;

public class LibraryControllerTests
{
    [Fact]
    public async Task VerifiedCatalogIdsWorkWithoutAddingShowsToHistory()
    {
        var config = new CruncharrConfig();
        config.Sonarr.Enabled = true;
        var owned = new SonarrSeries { Id = 312, TvdbId = 251908, Title = "Haganai: I Don't Have Many Friends" };
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(s => s.GetCurrentSeriesAsync(config.Sonarr, It.IsAny<CancellationToken>())).ReturnsAsync([owned]);
        sonarr.Setup(s => s.ResolveSeriesAsync("GYX0PN4MR", "Haganai", It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>())).ReturnsAsync(owned);
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.GetHistorySeriesAsync()).ReturnsAsync([]);
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new SeriesInfo { Id = "GYX0PN4MR", Title = "Haganai", EpisodeCount = 24 }]);
        var controller = new LibraryController(sonarr.Object, history.Object, config, NullLogger<LibraryController>.Instance, api.Object);
        var response = Assert.IsType<OkObjectResult>(await controller.GetSonarrLibrary(TestContext.Current.CancellationToken));
        Assert.Equal("GYX0PN4MR", JObject.FromObject(response.Value!)["Series"]![0]!["CrunchyrollSeriesIds"]![0]!.Value<string>());
        history.Verify(h => h.CrUpdateSeriesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LibraryIncludesSonarrSeriesOutsideHistoryAndFileCountsWithoutPrivatePaths()
    {
        var sonarr = new Mock<ISonarrService>();
        var config = new CruncharrConfig();
        config.Sonarr.Enabled = true;
        sonarr.Setup(service => service.GetCurrentSeriesAsync(config.Sonarr, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SonarrSeries { Id=1, Title="First", Path="/private/library", Statistics=new SonarrSeriesStatistics { EpisodeFileCount=12, EpisodeCount=24 } },
            new SonarrSeries { Id=2, Title="Second", AlternateTitles=[new SonarrAlternateTitle {Title="Alias"}] }
        ]);
        var history = new Mock<IHistoryService>();
        history.Setup(service => service.GetHistorySeriesAsync()).ReturnsAsync([new HistorySeries {SeriesId="CR1",SonarrSeriesId="1"}]);
        var controller = new LibraryController(sonarr.Object, history.Object, config, NullLogger<LibraryController>.Instance);
        var response = Assert.IsType<OkObjectResult>(await controller.GetSonarrLibrary(TestContext.Current.CancellationToken));
        var json = JObject.FromObject(response.Value!);
        var series = Assert.IsType<JArray>(json["Series"]);
        Assert.Equal(2, series.Count);
        Assert.Equal(12, series[0]["EpisodeFileCount"]!.Value<int>());
        Assert.Equal("CR1", series[0]["CrunchyrollSeriesIds"]![0]!.Value<string>());
        Assert.Contains("Alias", series[1]["Titles"]!.Values<string>());
        Assert.DoesNotContain("/private/library", json.ToString());
    }

    [Fact]
    public async Task FailedTitleVerificationIdentifiesTheAffectedShowAndKeepsOtherMatches()
    {
        var config = new CruncharrConfig();
        config.Sonarr.Enabled = true;
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(s => s.GetCurrentSeriesAsync(config.Sonarr, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrSeries { Id = 10, Title = "A Certain Series: Full Title" }]);
        sonarr.Setup(s => s.ResolveSeriesAsync("unavailable", "A Certain Series", It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Metadata unavailable"));
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.GetHistorySeriesAsync()).ReturnsAsync([]);
        var api = new Mock<ICrunchyrollApiService>();
        api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SeriesInfo { Id = "unavailable", Title = "A Certain Series", EpisodeCount = 12 },
            new SeriesInfo { Id = "available", Title = "A Certain Series: Full Title", EpisodeCount = 12 }
        ]);
        var controller = new LibraryController(sonarr.Object, history.Object, config, NullLogger<LibraryController>.Instance, api.Object);

        var response = Assert.IsType<OkObjectResult>(await controller.GetSonarrLibrary(TestContext.Current.CancellationToken));
        var json = JObject.FromObject(response.Value!);

        Assert.True(json["MatchingUnavailable"]!.Value<bool>());
        Assert.Equal("unavailable", json["MatchingFailures"]![0]!["SeriesId"]!.Value<string>());
        Assert.Equal(new[] { "available" }, json["Series"]![0]!["CrunchyrollSeriesIds"]!.Values<string>());
    }

    [Fact]
    public async Task LibraryFailureIsNotReportedAsAnEmptyLibrary()
    {
        var sonarr = new Mock<ISonarrService>();
        var config = new CruncharrConfig();
        config.Sonarr.Enabled = true;
        sonarr.Setup(service => service.GetCurrentSeriesAsync(config.Sonarr, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Offline"));
        var controller = new LibraryController(sonarr.Object, Mock.Of<IHistoryService>(), config, NullLogger<LibraryController>.Instance);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.GetSonarrLibrary(TestContext.Current.CancellationToken)).StatusCode);
    }
}
