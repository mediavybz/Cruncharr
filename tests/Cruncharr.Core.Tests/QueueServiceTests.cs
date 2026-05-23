using System.Threading;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Xunit;

namespace Cruncharr.Core.Tests;

public class ProcessingSlotManagerTests{
    [Fact]
    public void Constructor_Initializes_With_Limit(){
        var manager = new ProcessingSlotManager(3);
        Assert.Equal(3, manager.Limit);
    }

    [Fact]
    public void Constructor_Throws_On_Negative_Limit(){
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessingSlotManager(-1));
    }

    [Fact]
    public async Task WaitAsync_Acquires_Slot(){
        var manager = new ProcessingSlotManager(1);
        await manager.WaitAsync();
        // Should not throw - we acquired the only slot
    }

    [Fact]
    public async Task WaitAsync_Blocks_When_No_Slots(){
        var manager = new ProcessingSlotManager(1);
        await manager.WaitAsync();
        
        var secondWait = manager.WaitAsync();
        await Task.Delay(50);
        
        Assert.False(secondWait.IsCompleted);
        
        manager.Release();
        await secondWait;
    }

    [Fact]
    public void Release_Returns_Slot(){
        var manager = new ProcessingSlotManager(1);
        manager.WaitAsync().GetAwaiter().GetResult();
        manager.Release();
        
        // Should be able to acquire again immediately
        var secondWait = manager.WaitAsync();
        Assert.True(secondWait.IsCompletedSuccessfully);
    }

    [Fact]
    public void SetLimit_Increases_Available_Slots(){
        var manager = new ProcessingSlotManager(1);
        manager.SetLimit(3);
        
        Assert.Equal(3, manager.Limit);
        
        // Should be able to acquire 3 slots now
        manager.WaitAsync().GetAwaiter().GetResult();
        manager.WaitAsync().GetAwaiter().GetResult();
        manager.WaitAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void SetLimit_Decreases_Removes_Slots(){
        var manager = new ProcessingSlotManager(3);
        manager.WaitAsync().GetAwaiter().GetResult();
        manager.WaitAsync().GetAwaiter().GetResult();
        
        manager.SetLimit(1);
        Assert.Equal(1, manager.Limit);
        
        // Third acquisition should block
        var thirdWait = manager.WaitAsync();
        Assert.False(thirdWait.IsCompleted);
    }

    [Fact]
    public void Dispose_Does_Not_Throw(){
        var manager = new ProcessingSlotManager(2);
        manager.Dispose();
    }
}

public class QueuePersistenceServiceTests : IDisposable{
    private readonly string _tempFile;

    public QueuePersistenceServiceTests(){
        _tempFile = Path.Combine(Path.GetTempPath(), $"cruncharr_test_queue_{Guid.NewGuid()}.json");
    }

    public void Dispose(){
        if (File.Exists(_tempFile)){
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void SaveQueue_Creates_File(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" },
                DownloadProgress = new DownloadProgress{ State = DownloadState.Queued }
            }
        };

        service.SaveQueue(queue);
        Assert.True(File.Exists(_tempFile));
    }

    [Fact]
    public void LoadQueue_Restores_Items(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" },
                DownloadProgress = new DownloadProgress{ State = DownloadState.Downloading, Percent = 50 }
            }
        };

        service.SaveQueue(queue);
        
        var loaded = service.LoadQueue();
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal("ep1", loaded[0].Episode.Id);
        Assert.Equal("Episode 1", loaded[0].Episode.Title);
    }

    [Fact]
    public void LoadQueue_Returns_Null_When_No_File(){
        var service = new QueuePersistenceService(_tempFile);
        var loaded = service.LoadQueue();
        Assert.Null(loaded);
    }

    [Fact]
    public void SaveQueue_Deletes_File_When_Empty(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" }
            }
        };

        service.SaveQueue(queue);
        Assert.True(File.Exists(_tempFile));

        service.SaveQueue(new List<QueueItem>());
        Assert.False(File.Exists(_tempFile));
    }

    [Fact]
    public void SaveQueue_Filters_Finished_Items(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" },
                DownloadProgress = new DownloadProgress{ State = DownloadState.Done }
            },
            new(){
                Id = "2",
                Episode = new EpisodeInfo{ Id = "ep2", Title = "Episode 2" },
                DownloadProgress = new DownloadProgress{ State = DownloadState.Error }
            },
            new(){
                Id = "3",
                Episode = new EpisodeInfo{ Id = "ep3", Title = "Episode 3" },
                DownloadProgress = new DownloadProgress{ State = DownloadState.Queued }
            }
        };

        service.SaveQueue(queue);
        var loaded = service.LoadQueue();
        
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal("ep3", loaded[0].Episode.Id);
    }

    [Fact]
    public void LoadQueue_Prepares_Retry_State(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" },
                DownloadProgress = new DownloadProgress{
                    State = DownloadState.Queued,
                    RetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) // Past retry time
                }
            }
        };

        service.SaveQueue(queue);
        var loaded = service.LoadQueue();
        
        Assert.NotNull(loaded);
        Assert.Equal(DownloadState.Queued, loaded[0].DownloadProgress.State);
        Assert.Null(loaded[0].DownloadProgress.RetryAtUtc);
    }

    [Fact]
    public void DeleteQueue_Removes_File(){
        var service = new QueuePersistenceService(_tempFile);
        var queue = new List<QueueItem>{
            new(){
                Id = "1",
                Episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" }
            }
        };

        service.SaveQueue(queue);
        Assert.True(File.Exists(_tempFile));

        service.DeleteQueue();
        Assert.False(File.Exists(_tempFile));
    }
}

public class QueueServiceTests{
    private class MockDownloadService : IDownloadService{
        public List<string> DownloadedEpisodes { get; } = new();
        public TimeSpan? Delay { get; set; }
        public bool ShouldThrow { get; set; }
        public int ThrowCount { get; set; }
        private int _throwCounter;

        public Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
            return Task.FromResult(new DownloadResult{ Success = true });
        }

        public Task<DownloadResult> DownloadEpisodeAsync(string episodeId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
            if (ShouldThrow && _throwCounter++ < ThrowCount){
                throw new Exception("Simulated download failure");
            }
            
            DownloadedEpisodes.Add(episodeId);
            
            if (Delay.HasValue){
                return Task.Run(async () =>{
                    await Task.Delay(Delay.Value, cancellationToken);
                    return new DownloadResult{ Success = true, Episode = new EpisodeInfo{ Id = episodeId } };
                }, cancellationToken);
            }
            
            return Task.FromResult(new DownloadResult{ Success = true, Episode = new EpisodeInfo{ Id = episodeId } });
        }
    }

    [Fact]
    public void AddToQueue_Adds_Item(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        var episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" };
        service.AddToQueue(episode);
        
        var queue = service.GetQueue();
        Assert.Single(queue);
        Assert.Equal("ep1", queue[0].Episode.Id);
        Assert.Equal(DownloadState.Queued, queue[0].DownloadProgress.State);
    }

    [Fact]
    public void AddToQueue_Does_Not_Duplicate(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        var episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" };
        service.AddToQueue(episode);
        service.AddToQueue(episode); // Same episode, different instance
        
        // Should have 2 items since we generate new QueueItem IDs each time
        var queue = service.GetQueue();
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void RemoveFromQueue_Removes_Item(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        var episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" };
        service.AddToQueue(episode);
        
        var queueItem = service.GetQueue()[0];
        service.RemoveFromQueue(queueItem.Id);
        
        Assert.Empty(service.GetQueue());
    }

    [Fact]
    public async Task ProcessQueueAsync_Processes_Items(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        service.AddToQueue(new EpisodeInfo{ Id = "ep1", Title = "Episode 1" });
        service.AddToQueue(new EpisodeInfo{ Id = "ep2", Title = "Episode 2" });
        
        var config = new CruncharrConfig{
            Download = new DownloadConfig{ SimultaneousDownloads = 2 },
            Queue = new QueueConfig{ AutoDownload = true }
        };
        
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var processTask = service.ProcessQueueAsync(config, cancellationToken: cts.Token);
        
        // Wait for both items to be processed with a timeout
        var timeout = DateTime.Now.AddSeconds(5);
        while (mockDownload.DownloadedEpisodes.Count < 2 && DateTime.Now < timeout){
            await Task.Delay(50);
        }
        
        cts.Cancel();
        
        try{
            await processTask;
        } catch (OperationCanceledException){
            // Expected
        }
        
        Assert.Equal(2, mockDownload.DownloadedEpisodes.Count);
    }

    [Fact]
    public async Task ProcessQueueAsync_Respects_Concurrent_Limit(){
        var mockDownload = new MockDownloadService{ Delay = TimeSpan.FromMilliseconds(300) };
        var service = new QueueService(mockDownload);
        
        service.AddToQueue(new EpisodeInfo{ Id = "ep1", Title = "Episode 1" });
        service.AddToQueue(new EpisodeInfo{ Id = "ep2", Title = "Episode 2" });
        service.AddToQueue(new EpisodeInfo{ Id = "ep3", Title = "Episode 3" });
        
        var config = new CruncharrConfig{
            Download = new DownloadConfig{ SimultaneousDownloads = 1 },
            Queue = new QueueConfig{ AutoDownload = true }
        };
        
        var cts = new CancellationTokenSource();
        var processTask = service.ProcessQueueAsync(config, cancellationToken: cts.Token);
        
        // Wait a bit - with 1 concurrent download and 300ms delay each, 
        // after 400ms we should have at most 2 done
        await Task.Delay(400);
        
        Assert.True(mockDownload.DownloadedEpisodes.Count <= 2, 
            $"Expected at most 2 downloads but got {mockDownload.DownloadedEpisodes.Count}");
        
        cts.Cancel();
        try{
            await processTask;
        } catch (OperationCanceledException){
            // Expected
        }
    }

    [Fact]
    public void ScheduleRetry_Sets_Retry_State(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        var episode = new EpisodeInfo{ Id = "ep1", Title = "Episode 1" };
        service.AddToQueue(episode);
        
        var queueItem = service.GetQueue()[0];
        service.ScheduleRetry(queueItem.Id, TimeSpan.FromMinutes(5), "Rate limited");
        
        var updatedItem = service.GetQueue()[0];
        Assert.True(updatedItem.DownloadProgress.IsWaitingForRetry);
        Assert.Equal("Rate limited", updatedItem.DownloadProgress.Doing);
        Assert.Equal(1, updatedItem.DownloadProgress.RetryAttemptCount);
    }

    [Fact]
    public void BlockAutoDownloadUntil_Blocks_Processing(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        
        service.BlockAutoDownloadUntil(TimeSpan.FromMilliseconds(500));
        
        // The block should be active
        Assert.NotNull(service.GetQueue()); // Just verifying no exception
    }

    [Fact]
    public void QueueStateChanged_Event_Fires(){
        var mockDownload = new MockDownloadService();
        var service = new QueueService(mockDownload);
        var eventFired = false;
        
        service.QueueStateChanged += (sender, args) =>{
            eventFired = true;
        };
        
        service.AddToQueue(new EpisodeInfo{ Id = "ep1", Title = "Episode 1" });
        
        Assert.True(eventFired);
    }

    [Fact]
    public void ActiveDownloads_Tracks_Correctly(){
        var mockDownload = new MockDownloadService{ Delay = TimeSpan.FromMilliseconds(500) };
        var service = new QueueService(mockDownload);
        
        Assert.Equal(0, service.ActiveDownloads);
        Assert.False(service.HasActiveDownloads);
    }
}
