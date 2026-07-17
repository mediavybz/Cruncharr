using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class QueueController : ControllerBase
{
    private readonly IQueueService _queueService;
    private readonly IHistoryService _historyService;
    private readonly ILanguagePrefsService _languagePrefs;
    private readonly CruncharrConfig _config;
    private readonly ILogger<QueueController> _logger;
    private static readonly JsonSerializerSettings _sseJsonSettings = new JsonSerializerSettings
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public QueueController(IQueueService queueService, IHistoryService historyService, ILanguagePrefsService languagePrefs, CruncharrConfig config, ILogger<QueueController> logger)
    {
        _queueService = queueService;
        _historyService = historyService;
        _languagePrefs = languagePrefs;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Get current download queue
    /// </summary>
    [HttpGet]
    public ActionResult<QueueResponse> GetQueue()
    {
        try
        {
            var items = _queueService.GetQueue();
            return Ok(new QueueResponse
            {
                Items = items,
                ActiveDownloads = _queueService.ActiveDownloads,
                HasActiveDownloads = _queueService.HasActiveDownloads,
                IsGloballyPaused = _queueService.IsGloballyPaused
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue");
            return StatusCode(500, new { Error = "Failed to get queue", Message = ex.Message });
        }
    }

    /// <summary>
    /// Add episode to download queue
    /// </summary>
    [HttpPost]
    public IActionResult AddToQueue([FromBody] QueueRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.EpisodeId))
            {
                return BadRequest(new { Error = "EpisodeId is required" });
            }
            // Crunchyroll IDs are short alphanumerics; this value flows into downstream CR URLs,
            // so reject anything else before it leaves the app.
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.EpisodeId, "^[A-Za-z0-9._-]{1,64}$"))
            {
                return BadRequest(new { Error = "Invalid EpisodeId format" });
            }

            var episode = new EpisodeInfo
            {
                Id = request.EpisodeId,
                Title = request.Title ?? $"Episode {request.EpisodeId}",
                SeriesTitle = request.SeriesTitle ?? "Unknown",
                // 0 = "unknown, resolve on the backend". A queue-add (per the add-path contract)
                // sends only episodeId + dubs, so season/episode arrive null; defaulting to a
                // concrete 1 made DownloadService's "only fill when <= 0" guard skip the real
                // CR season_number (e.g. a Season 5 episode saved under Season 1).
                SeasonNumber = request.SeasonNumber ?? 0,
                EpisodeNumber = request.EpisodeNumber ?? 0,
                Locale = request.Locale ?? "ja-JP",
                AudioLocale = request.AudioLocale ?? request.Locale ?? "ja-JP",
                ThumbnailUrl = request.ThumbnailUrl,
                CoverArtUrl = request.CoverArtUrl,
                Description = request.Description,
                SelectedDubs = request.SelectedDubs,
                SelectedSubs = request.SelectedSubs,
                Versions = request.Versions
            };

            _queueService.AddToQueue(episode);
            _logger.LogInformation("Added episode {EpisodeId} to queue", request.EpisodeId);

            // Adaptive defaults: learn from the PRIMARY (first) language the user picked for this
            // download. No-op unless the feature is enabled; bare adds (no explicit pick) don't count.
            _languagePrefs.RecordPick("audio", request.SelectedDubs?.FirstOrDefault());
            _languagePrefs.RecordPick("sub", request.SelectedSubs?.FirstOrDefault());

            return Ok(new { Message = "Added to queue", EpisodeId = request.EpisodeId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add episode to queue");
            return StatusCode(500, new { Error = "Failed to add to queue", Message = ex.Message });
        }
    }

    /// <summary>
    /// Remove item from queue
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult RemoveFromQueue(string id)
    {
        try
        {
            var found = _queueService.RemoveFromQueue(id);
            if (!found) return NotFound(new { Message = "Item not found in queue" });
            _logger.LogInformation("Removed item {QueueItemId} from queue", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove item from queue");
            return StatusCode(500, new { Error = "Failed to remove item", Message = ex.Message });
        }
    }

    /// <summary>
    /// Stream a completed download's file to the browser so the user can save a copy to the
    /// device they're viewing the UI on (e.g. a VPS deployment - no FTP needed). The path comes
    /// from server-side queue state (the client only passes the queue id), so there's no path traversal.
    /// </summary>
    [HttpGet("{id}/file")]
    public async Task<IActionResult> DownloadFile(string id)
    {
        var path = await ResolveOutputPathAsync(id);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound(new { Error = "File not found - it may have been deleted, moved, or not finished." });
        }
        var fileName = System.IO.Path.GetFileName(path);
        var stream = new System.IO.FileStream(
            path,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        return File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Delete a completed download's file from the host running Cruncharr, then drop the queue item.
    /// </summary>
    [HttpDelete("{id}/file")]
    public async Task<IActionResult> DeleteFile(string id)
    {
        var item = _queueService.GetQueue()?.FirstOrDefault(q => q.Id == id);
        if (item == null) return NotFound(new { Error = "Queue item not found" });
        try
        {
            var path = await ResolveOutputPathAsync(id);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
                _logger.LogInformation("Deleted downloaded file {Path} for queue item {Id}", path, id);
            }
            _queueService.RemoveFromQueue(id);
            return Ok(new { deleted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete downloaded file for queue item {Id}", id);
            return StatusCode(500, new { Error = "Delete failed", Message = ex.Message });
        }
    }

    // The output file path is recorded in flat history (DownloadHistory.OutputPath), not on the
    // queue item. Resolve it from the queue item's episode id.
    private async Task<string?> ResolveOutputPathAsync(string queueId)
    {
        var item = _queueService.GetQueue()?.FirstOrDefault(q => q.Id == queueId);
        var epId = item?.Episode?.Id;
        if (string.IsNullOrEmpty(epId)) return null;
        var history = await _historyService.GetAllAsync(0, 100000);
        // Some flat-history ids append the audio locale (e.g. <id>JAJP), so match exact OR prefix.
        var match = history.LastOrDefault(h => h.EpisodeId == epId)
                    ?? history.LastOrDefault(h => h.EpisodeId != null && h.EpisodeId.StartsWith(epId, StringComparison.Ordinal));
        return GetSafeOutputPath(match?.OutputPath);
    }

    private string? GetSafeOutputPath(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return null;

        try
        {
            var root = System.IO.Path.GetFullPath(_config.Download.OutputDirectory);
            var candidate = System.IO.Path.GetFullPath(outputPath);
            var relative = System.IO.Path.GetRelativePath(root, candidate);
            var escapesRoot = System.IO.Path.IsPathRooted(relative)
                              || relative == ".."
                              || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                              || relative.StartsWith(".." + System.IO.Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
            if (escapesRoot)
            {
                _logger.LogWarning("Rejected queue file path outside configured output directory for {Path}", candidate);
                return null;
            }

            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogWarning("Rejected invalid queue file path");
            return null;
        }
    }

    /// <summary>
    /// Retry all failed downloads
    /// </summary>
    [HttpPost("retry-failed")]
    public IActionResult RetryFailed()
    {
        try
        {
            _queueService.RetryAllFailed();
            _logger.LogInformation("Retrying all failed downloads");
            return Ok(new { Message = "Retrying failed downloads" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry failed downloads");
            return StatusCode(500, new { Error = "Failed to retry", Message = ex.Message });
        }
    }

    /// <summary>
    /// Clear entire queue
    /// </summary>
    [HttpDelete]
    public IActionResult ClearQueue()
    {
        try
        {
            _queueService.ClearQueue();
            _logger.LogInformation("Queue cleared");
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear queue");
            return StatusCode(500, new { Error = "Failed to clear queue", Message = ex.Message });
        }
    }

    /// <summary>
    /// Replace entire queue with new items
    /// </summary>
    [HttpPost("replace")]
    public IActionResult ReplaceQueue([FromBody] List<QueueItem> newQueue)
    {
        try
        {
            _queueService.ReplaceQueue(newQueue ?? new List<QueueItem>());
            _logger.LogInformation("Queue replaced with {Count} items", newQueue?.Count ?? 0);
            return Ok(new { Message = "Queue replaced", Count = newQueue?.Count ?? 0 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to replace queue");
            return StatusCode(500, new { Error = "Failed to replace queue", Message = ex.Message });
        }
    }

    /// <summary>
    /// Retry specific item
    /// </summary>
    [HttpPost("{id}/retry")]
    public IActionResult RetryItem(string id)
    {
        try
        {
            var found = _queueService.RetryItem(id);
            if (!found) return NotFound(new { Message = "Item not found in queue" });
            _logger.LogInformation("Retrying item {QueueItemId}", id);
            return Ok(new { Message = "Retrying item", Id = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry item");
            return StatusCode(500, new { Error = "Failed to retry item", Message = ex.Message });
        }
    }

    /// <summary>
    /// Pause specific item
    /// </summary>
    [HttpPost("{id}/pause")]
    public IActionResult PauseItem(string id)
    {
        try
        {
            var found = _queueService.PauseItem(id);
            if (!found) return NotFound(new { Message = "Item not found in queue" });
            _logger.LogInformation("Paused item {QueueItemId}", id);
            return Ok(new { Message = "Paused item", Id = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause item");
            return StatusCode(500, new { Error = "Failed to pause item", Message = ex.Message });
        }
    }

    /// <summary>
    /// Resume specific item
    /// </summary>
    [HttpPost("{id}/resume")]
    public IActionResult ResumeItem(string id)
    {
        try
        {
            var found = _queueService.ResumeItem(id);
            if (!found) return NotFound(new { Message = "Item not found in queue" });
            _logger.LogInformation("Resumed item {QueueItemId}", id);
            return Ok(new { Message = "Resumed item", Id = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume item");
            return StatusCode(500, new { Error = "Failed to resume item", Message = ex.Message });
        }
    }

    /// <summary>
    /// Start specific item immediately (bypass AutoDownload)
    /// </summary>
    [HttpPost("{id}/start")]
    public IActionResult StartItem(string id)
    {
        try
        {
            var found = _queueService.StartItem(id);
            if (!found) return NotFound(new { Message = "Item not found in queue" });
            _logger.LogInformation("Started item {QueueItemId}", id);
            return Ok(new { Message = "Started item", Id = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start item");
            return StatusCode(500, new { Error = "Failed to start item", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get queue statistics
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<QueueStats> GetQueueStats()
    {
        try
        {
            var items = _queueService.GetQueue() ?? new List<QueueItem>();
            var total = items.Count;
            var active = items.Count(i => i.DownloadProgress?.State == DownloadState.Downloading || i.DownloadProgress?.State == DownloadState.Processing);
            var queued = items.Count(i => i.DownloadProgress?.State == DownloadState.Queued);
            var completed = items.Count(i => i.DownloadProgress?.State == DownloadState.Done);
            var failed = items.Count(i => i.DownloadProgress?.State == DownloadState.Error);
            var waitingForRetry = items.Count(i => i.DownloadProgress?.IsWaitingForRetry == true);

            return Ok(new QueueStats
            {
                Total = total,
                Active = active,
                Queued = queued,
                Completed = completed,
                Failed = failed,
                WaitingForRetry = waitingForRetry,
                IsGloballyPaused = _queueService.IsGloballyPaused
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue stats");
            return StatusCode(500, new { Error = "Failed to get queue stats", Message = ex.Message });
        }
    }

    /// <summary>
    /// Server-Sent Events endpoint for real-time queue updates
    /// </summary>
    [HttpGet("sse")]
    public async Task GetQueueUpdates([FromServices] QueueBroadcastService broadcastService, CancellationToken cancellationToken)
    {
        try
        {
            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var clientId = Guid.NewGuid();
            var reader = broadcastService.Subscribe(clientId);
            try
            {
                // Send initial state
                var initialQueue = _queueService.GetQueue();
                var initialResponse = new QueueResponse
                {
                    Items = initialQueue,
                    ActiveDownloads = _queueService.ActiveDownloads,
                    HasActiveDownloads = _queueService.HasActiveDownloads,
                    IsGloballyPaused = _queueService.IsGloballyPaused
                };
                await WriteSseEventAsync(JsonConvert.SerializeObject(initialResponse, _sseJsonSettings), cancellationToken);

                // Listen for updates
                await foreach (var update in reader.ReadAllAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await WriteSseEventAsync(update, cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            finally
            {
                broadcastService.Unsubscribe(clientId);
            }
        }
        catch (OperationCanceledException)
        {
            // Client closed the SSE connection (navigation/refresh) - expected, not an error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSE queue updates failed");
        }
    }

    /// <summary>
    /// Pause all queue processing globally
    /// </summary>
    [HttpPost("pause")]
    public IActionResult PauseGlobally()
    {
        try
        {
            _queueService.PauseGlobally();
            return Ok(new { Message = "Queue paused globally" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause queue globally");
            return StatusCode(500, new { Error = "Failed to pause queue", Message = ex.Message });
        }
    }

    /// <summary>
    /// Resume queue processing globally
    /// </summary>
    [HttpPost("resume")]
    public IActionResult ResumeGlobally()
    {
        try
        {
            _queueService.ResumeGlobally();
            return Ok(new { Message = "Queue resumed globally" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume queue globally");
            return StatusCode(500, new { Error = "Failed to resume queue", Message = ex.Message });
        }
    }

    private async Task WriteSseEventAsync(string data, CancellationToken cancellationToken)
    {
        try
        {
            await Response.WriteAsync($"data: {data}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // Client disconnected, ignore
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested, ignore
        }
    }
}

public class QueueRequest
{
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
    public List<string>? SelectedDubs { get; set; }
    public List<string>? SelectedSubs { get; set; }
    public List<EpisodeVersion>? Versions { get; set; }
}

public class QueueResponse
{
    public List<QueueItem> Items { get; set; } = new();
    public int ActiveDownloads { get; set; }
    public bool HasActiveDownloads { get; set; }
    public bool IsGloballyPaused { get; set; }
}

public class QueueStats
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Queued { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int WaitingForRetry { get; set; }
    public bool IsGloballyPaused { get; set; }
}
