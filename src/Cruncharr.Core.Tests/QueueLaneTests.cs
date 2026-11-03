using System.Collections.Concurrent;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cruncharr.Core.Tests;

// Downloading and processing (mux/transcode) are separate lanes: an item that finished downloading
// must not keep a download slot while it transcodes. The queue lists and starts items in the order
// they were added.
public class QueueLaneTests
{
    [Fact]
    public async Task FinishedDownload_FreesItsSlotWhileItProcesses()
    {
        var downloads = new StagedDownloadService();
        using var harness = StartQueue(downloads, simultaneousDownloads: 1, processingJobs: 2);
        var first = harness.Queue.AddToQueue(Episode("E1")).Item;
        harness.Queue.AddToQueue(Episode("E2"));

        await downloads.Started("E1");
        Assert.False(downloads.HasStarted("E2"));

        downloads.FinishDownload("E1"); // E1 now muxes/transcodes
        await downloads.Started("E2");

        // Both are still work in progress, and E1 cannot be started a second time meanwhile.
        Assert.Equal(2, harness.Queue.ActiveDownloads);
        Assert.False(harness.Queue.StartItem(first.Id));

        downloads.FinishProcessing("E1");
        downloads.FinishDownload("E2");
        downloads.FinishProcessing("E2");
        await WaitForAsync(() => harness.Queue.ActiveDownloads == 0);
    }

    [Fact]
    public async Task ProcessingBacklog_IsBounded()
    {
        var downloads = new StagedDownloadService();
        // One download slot and one processing job: at most two finished downloads wait for processing.
        using var harness = StartQueue(downloads, simultaneousDownloads: 1, processingJobs: 1);
        foreach (var id in new[] { "E1", "E2", "E3" }) harness.Queue.AddToQueue(Episode(id));

        await downloads.Started("E1");
        downloads.FinishDownload("E1");
        await downloads.Started("E2");
        downloads.FinishDownload("E2");

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(downloads.HasStarted("E3"));

        downloads.FinishProcessing("E1");
        await downloads.Started("E3");

        downloads.FinishProcessing("E2");
        downloads.FinishDownload("E3");
        downloads.FinishProcessing("E3");
        await WaitForAsync(() => harness.Queue.ActiveDownloads == 0);
    }

    [Fact]
    public async Task Queue_ListsAndStartsItemsInTheOrderTheyWereAdded()
    {
        var downloads = new StagedDownloadService();
        using var harness = StartQueue(downloads, simultaneousDownloads: 1, processingJobs: 1, start: false);
        var ids = Enumerable.Range(1, 40).Select(n => $"E{n:00}").ToList();
        foreach (var id in ids) harness.Queue.AddToQueue(Episode(id));

        Assert.Equal(ids, harness.Queue.GetQueue().Select(item => item.Episode.Id).ToList());

        harness.Start();
        foreach (var id in ids.Take(5))
        {
            await downloads.Started(id);
            downloads.FinishDownload(id);
            downloads.FinishProcessing(id);
        }
        Assert.Equal(ids.Take(5), downloads.StartOrder.Take(5));
    }

    private static EpisodeInfo Episode(string id) => new() { Id = id, Title = id };

    private static Harness StartQueue(StagedDownloadService downloads, int simultaneousDownloads, int processingJobs, bool start = true)
    {
        var harness = new Harness(downloads, simultaneousDownloads, processingJobs);
        if (start) harness.Start();
        return harness;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(predicate(), "Condition was not reached before timeout.");
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _stop = new();
        private readonly CruncharrConfig _config = new();
        private Task? _processor;

        public Harness(StagedDownloadService downloads, int simultaneousDownloads, int processingJobs)
        {
            _provider = new ServiceCollection().AddSingleton<IDownloadService>(downloads).BuildServiceProvider();
            Queue = new QueueService(_provider);
            _config.Queue.AutoDownload = true;
            _config.Download.SimultaneousDownloads = simultaneousDownloads;
            _config.Queue.SimultaneousProcessingJobs = processingJobs;
            _config.Download.CooldownDelaySeconds = 0;
            _config.Download.DownloadDelaySeconds = 0;
        }

        public QueueService Queue { get; }

        public void Start()
        {
            _processor = Queue.ProcessQueueAsync(_config, cancellationToken: _stop.Token);
            Queue.SetInitialized(true);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _processor?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            Queue.Dispose();
            _provider.Dispose();
        }
    }

    // Each episode downloads until FinishDownload, then "processes" until FinishProcessing.
    private sealed class StagedDownloadService : IDownloadService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _downloaded = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _processed = new();
        private readonly ConcurrentQueue<string> _startOrder = new();

        public IEnumerable<string> StartOrder => _startOrder;

        private static TaskCompletionSource Gate(ConcurrentDictionary<string, TaskCompletionSource> gates, string id) =>
            gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public bool HasStarted(string id) => Gate(_started, id).Task.IsCompleted;
        public Task Started(string id) => Gate(_started, id).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void FinishDownload(string id) => Gate(_downloaded, id).TrySetResult();
        public void FinishProcessing(string id) => Gate(_processed, id).TrySetResult();

        public async Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, Action? onDownloadComplete = null)
        {
            _startOrder.Enqueue(episode.Id);
            Gate(_started, episode.Id).TrySetResult();
            await Gate(_downloaded, episode.Id).Task.WaitAsync(cancellationToken);
            onDownloadComplete?.Invoke();
            progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 90, Doing = "Encoding..." });
            await Gate(_processed, episode.Id).Task.WaitAsync(cancellationToken);
            return new DownloadResult { Success = true, Episode = episode };
        }

        public Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
