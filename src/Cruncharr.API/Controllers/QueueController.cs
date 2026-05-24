using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class QueueController : ControllerBase{
    private readonly IQueueService _queueService;
    private readonly ILogger<QueueController> _logger;

    public QueueController(IQueueService queueService, ILogger<QueueController> logger){
        _queueService = queueService;
        _logger = logger;
    }

    /// <summary>
    /// Get current download queue
    /// </summary>
    [HttpGet]
    public ActionResult<QueueResponse> GetQueue(){
        var items = _queueService.GetQueue();
        return Ok(new QueueResponse{
            Items = items,
            ActiveDownloads = _queueService.ActiveDownloads,
            HasActiveDownloads = _queueService.HasActiveDownloads
        });
    }

    /// <summary>
    /// Add episode to download queue
    /// </summary>
    [HttpPost]
    public ActionResult<QueueItem> AddToQueue([FromBody] QueueRequest request){
        if (string.IsNullOrEmpty(request.EpisodeId)){
            return BadRequest(new { Error = "EpisodeId is required" });
        }

        var episode = new EpisodeInfo{
            Id = request.EpisodeId,
            Title = request.Title ?? $"Episode {request.EpisodeId}",
            SeriesTitle = request.SeriesTitle ?? "Unknown",
            SeasonNumber = request.SeasonNumber ?? 1,
            EpisodeNumber = request.EpisodeNumber ?? 1,
            Locale = request.Locale ?? "ja-JP",
            AudioLocale = request.AudioLocale ?? request.Locale ?? "ja-JP",
            ThumbnailUrl = request.ThumbnailUrl,
            CoverArtUrl = request.CoverArtUrl,
            Description = request.Description
        };

        _queueService.AddToQueue(episode);
        _logger.LogInformation("Added episode {EpisodeId} to queue", request.EpisodeId);

        return Ok(new { Message = "Added to queue", EpisodeId = request.EpisodeId });
    }

    /// <summary>
    /// Remove item from queue
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult RemoveFromQueue(string id){
        _queueService.RemoveFromQueue(id);
        _logger.LogInformation("Removed item {QueueItemId} from queue", id);
        return NoContent();
    }

    /// <summary>
    /// Retry all failed downloads
    /// </summary>
    [HttpPost("retry-failed")]
    public IActionResult RetryFailed(){
        _queueService.RetryAllFailed();
        _logger.LogInformation("Retrying all failed downloads");
        return Ok(new { Message = "Retrying failed downloads" });
    }

    /// <summary>
    /// Clear entire queue
    /// </summary>
    [HttpDelete]
    public IActionResult ClearQueue(){
        _queueService.ClearQueue();
        _logger.LogInformation("Queue cleared");
        return NoContent();
    }

    /// <summary>
    /// Retry specific item
    /// </summary>
    [HttpPost("{id}/retry")]
    public IActionResult RetryItem(string id){
        _queueService.RetryItem(id);
        _logger.LogInformation("Retrying item {QueueItemId}", id);
        return Ok(new { Message = "Retrying item", Id = id });
    }

    /// <summary>
    /// Pause specific item
    /// </summary>
    [HttpPost("{id}/pause")]
    public IActionResult PauseItem(string id){
        _queueService.PauseItem(id);
        _logger.LogInformation("Paused item {QueueItemId}", id);
        return Ok(new { Message = "Paused item", Id = id });
    }

    /// <summary>
    /// Resume specific item
    /// </summary>
    [HttpPost("{id}/resume")]
    public IActionResult ResumeItem(string id){
        _queueService.ResumeItem(id);
        _logger.LogInformation("Resumed item {QueueItemId}", id);
        return Ok(new { Message = "Resumed item", Id = id });
    }

    /// <summary>
    /// Get queue statistics
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<QueueStats> GetStats(){
        var items = _queueService.GetQueue();
        return Ok(new QueueStats{
            Total = items.Count,
            Active = _queueService.ActiveDownloads,
            Queued = items.Count(i => i.DownloadProgress.IsQueued),
            Completed = items.Count(i => i.DownloadProgress.IsDone),
            Failed = items.Count(i => i.DownloadProgress.IsError),
            WaitingForRetry = items.Count(i => i.DownloadProgress.IsWaitingForRetry)
        });
    }
}

public class QueueRequest{
    public string EpisodeId { get; set; } = "";
    public string? Title { get; set; }
    public string? SeriesTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? Locale { get; set; }
    public string? AudioLocale { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? Description { get; set; }
}

public class QueueResponse{
    public List<QueueItem> Items { get; set; } = new();
    public int ActiveDownloads { get; set; }
    public bool HasActiveDownloads { get; set; }
}

public class QueueStats{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Queued { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int WaitingForRetry { get; set; }
}
