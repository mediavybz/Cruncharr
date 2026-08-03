using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Tests;

public class QueueControllerAdmissionTests
{
    [Fact]
    public void AddToQueue_PreservesRawEpisodeIdentitySeparatelyFromIntegerNumber()
    {
        EpisodeInfo? captured = null;
        var queue = new Mock<IQueueService>();
        queue.Setup(service => service.AddToQueue(It.IsAny<EpisodeInfo>()))
            .Callback<EpisodeInfo>(episode => captured = episode)
            .Returns((EpisodeInfo episode) => new QueueAddResult(
                true,
                new QueueItem { Id = "decimal-queue-id", Episode = episode }));
        var controller = new QueueController(
            queue.Object,
            Mock.Of<IHistoryService>(),
            Mock.Of<ILanguagePrefsService>(),
            new CruncharrConfig(),
            NullLogger<QueueController>.Instance);

        var response = Assert.IsType<OkObjectResult>(controller.AddToQueue(new QueueRequest
        {
            EpisodeId = "GDECIMAL01",
            Episode = "24.9",
            EpisodeNumber = 0,
            SeasonNumber = 2,
            Title = "Digression: Hinata Sakaguchi"
        }));

        Assert.NotNull(response.Value);
        Assert.NotNull(captured);
        Assert.Equal("24.9", captured.Episode);
        Assert.Equal(0, captured.EpisodeNumber);
        Assert.Equal(2, captured.SeasonNumber);
    }

    [Fact]
    public void AddToQueue_ReturnsExistingAdmissionWithoutLearningDuplicatePreferences()
    {
        var existing = new QueueItem
        {
            Id = "existing-queue-id",
            Episode = new EpisodeInfo { Id = "GCTRL0001", Title = "Episode" },
            DownloadProgress = new DownloadProgress { State = DownloadState.Downloading }
        };
        var queue = new Mock<IQueueService>();
        queue.Setup(service => service.AddToQueue(It.IsAny<EpisodeInfo>()))
            .Returns(new QueueAddResult(false, existing));
        var preferences = new Mock<ILanguagePrefsService>();
        var controller = new QueueController(
            queue.Object,
            Mock.Of<IHistoryService>(),
            preferences.Object,
            new CruncharrConfig(),
            NullLogger<QueueController>.Instance);

        var response = Assert.IsType<OkObjectResult>(controller.AddToQueue(new QueueRequest
        {
            EpisodeId = "GCTRL0001",
            SelectedDubs = ["en-US"],
            SelectedSubs = ["en-US"]
        }));
        var body = JObject.FromObject(response.Value!);

        Assert.False(body.Value<bool>("Added"));
        Assert.Equal("existing-queue-id", body.Value<string>("QueueItemId"));
        Assert.Equal("Downloading", body.Value<string>("State"));
        preferences.Verify(service => service.RecordPick(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
