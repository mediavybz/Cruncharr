using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cruncharr.Core.Tests;

public class QueueDeduplicationTests
{
    [Fact]
    public void AddToQueue_SuppressesSequentialDuplicateEpisodeIds()
    {
        using var queue = CreateQueue();

        var first = queue.AddToQueue(Episode("GTEST0001", "First payload"));
        var duplicate = queue.AddToQueue(Episode("GTEST0001", "Changed payload"));

        Assert.True(first.Added);
        Assert.False(duplicate.Added);
        Assert.Same(first.Item, duplicate.Item);
        Assert.Single(queue.GetQueue());
    }

    [Fact]
    public async Task AddToQueue_AdmitsOnlyOneConcurrentRequestPerEpisodeId()
    {
        using var queue = CreateQueue();
        var ready = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
            {
                Interlocked.Increment(ref ready);
                await release.Task;
                return queue.AddToQueue(Episode("GTEST0002", $"Request {index}"));
            }))
            .ToArray();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ready) == 64, TimeSpan.FromSeconds(10)));
        release.SetResult(true);
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Added);
        Assert.Equal(63, results.Count(result => !result.Added));
        Assert.Single(queue.GetQueue());
        Assert.Single(results.Select(result => result.Item.Id).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void AddToQueue_AllowsReaddAfterRemoval()
    {
        using var queue = CreateQueue();
        var first = queue.AddToQueue(Episode("GTEST0003", "Episode"));

        Assert.True(queue.RemoveFromQueue(first.Item.Id));
        var second = queue.AddToQueue(Episode("GTEST0003", "Episode"));

        Assert.True(second.Added);
        Assert.NotEqual(first.Item.Id, second.Item.Id);
    }

    [Fact]
    public void AddToQueue_KeepsDistinctIdsEvenWhenLabelsMatch()
    {
        using var queue = CreateQueue();

        Assert.True(queue.AddToQueue(Episode("GTEST0004", "Same title")).Added);
        Assert.True(queue.AddToQueue(Episode("GTEST0005", "Same title")).Added);

        Assert.Equal(2, queue.GetQueue().Count);
    }

    [Fact]
    public void Restore_KeepsEarliestEntryForEachEpisodeIdAndPersistsSanitizedQueue()
    {
        var early = Item("queue-early", "GTEST0006", DateTimeOffset.Parse("2026-08-01T01:00:00Z"));
        var late = Item("queue-late", "GTEST0006", DateTimeOffset.Parse("2026-08-01T02:00:00Z"));
        var other = Item("queue-other", "GTEST0007", DateTimeOffset.Parse("2026-08-01T03:00:00Z"));
        var persistence = new Mock<IQueuePersistenceService>();
        persistence.Setup(service => service.LoadQueue()).Returns([late, other, early]);

        using var provider = new ServiceCollection().BuildServiceProvider();
        using var queue = new QueueService(provider, NullLogger<QueueService>.Instance, persistence.Object);

        Assert.Equal(["queue-early", "queue-other"], queue.GetQueue().OrderBy(item => item.AddedAt).Select(item => item.Id));
        persistence.Verify(service => service.SaveQueue(It.Is<List<QueueItem>>(items =>
            items.Count == 2 && items.Any(item => item.Id == "queue-early") && items.All(item => item.Id != "queue-late"))), Times.Once);
    }

    [Fact]
    public void ReplaceQueue_DeterministicallySuppressesDuplicateEpisodeIds()
    {
        using var queue = CreateQueue();
        var early = Item("replace-early", "GTEST0008", DateTimeOffset.Parse("2026-08-01T01:00:00Z"));
        var late = Item("replace-late", "GTEST0008", DateTimeOffset.Parse("2026-08-01T02:00:00Z"));

        queue.ReplaceQueue([late, early, Item("replace-other", "GTEST0009", DateTimeOffset.Parse("2026-08-01T03:00:00Z"))]);

        Assert.Equal(["replace-early", "replace-other"], queue.GetQueue().OrderBy(item => item.AddedAt).Select(item => item.Id));
    }

    private static QueueService CreateQueue()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new QueueService(provider, NullLogger<QueueService>.Instance);
    }

    private static EpisodeInfo Episode(string id, string title) => new()
    {
        Id = id,
        Title = title,
        SeriesTitle = "Test Series"
    };

    private static QueueItem Item(string queueId, string episodeId, DateTimeOffset addedAt) => new()
    {
        Id = queueId,
        AddedAt = addedAt,
        Episode = Episode(episodeId, "Episode"),
        DownloadProgress = new DownloadProgress { State = DownloadState.Queued }
    };
}
