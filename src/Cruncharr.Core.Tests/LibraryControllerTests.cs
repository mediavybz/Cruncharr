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
