using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

// Audit F03: a failed queue write was only logged, so admission reported success and the scheduler
// marked the episode handled even though a restart would lose it.
public class QueueDurabilityTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-queue-durable-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void SaveQueue_ReportsWhetherTheQueueReachedDisk()
    {
        var good = new QueuePersistenceService(Path.Combine(_root.FullName, "queue.json"));
        Assert.True(good.SaveQueue([Item("GOOD")]));
        Assert.Equal("GOOD", Assert.Single(good.LoadQueue()!).Episode.Id);

        // A directory where the queue file should be: the write/rename fails.
        var blocked = Path.Combine(_root.FullName, "blocked.json");
        Directory.CreateDirectory(blocked);
        var bad = new QueuePersistenceService(blocked);
        Assert.False(bad.SaveQueue([Item("BAD")]));
        Assert.Null(bad.LoadQueue());
    }

    [Fact]
    public void AddToQueue_ReportsAnUnsavedAdmission()
    {
        var saves = true;
        var persistence = new Mock<IQueuePersistenceService>();
        persistence.Setup(p => p.ScheduleSave(It.IsAny<List<QueueItem>>())).Returns(() => saves);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var queue = new QueueService(provider, NullLogger<QueueService>.Instance, persistence.Object);

        saves = false;
        var unsaved = queue.AddToQueue(Episode("E1"));
        Assert.True(unsaved.Added);
        Assert.False(unsaved.Persisted);
        // A duplicate of an unsaved admission is not durable either.
        Assert.False(queue.AddToQueue(Episode("E1")).Persisted);

        saves = true;
        Assert.True(queue.AddToQueue(Episode("E2")).Persisted);
        Assert.True(queue.AddToQueue(Episode("E1")).Persisted);
    }

    private static EpisodeInfo Episode(string id) => new() { Id = id, Title = id };

    private static QueueItem Item(string episodeId) => new()
    {
        Episode = Episode(episodeId),
        DownloadProgress = new DownloadProgress { State = DownloadState.Queued }
    };
}
