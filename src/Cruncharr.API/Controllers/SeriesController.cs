using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SeriesController : ControllerBase{
    private readonly ICrunchyrollApiService _api;
    private readonly ILogger<SeriesController> _logger;

    public SeriesController(ICrunchyrollApiService api, ILogger<SeriesController> logger){
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// Search for series on Crunchyroll
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult> Search([FromQuery] string query, [FromQuery] bool premium = false){
        if (string.IsNullOrWhiteSpace(query)){
            return BadRequest(new { Error = "Query parameter is required" });
        }

        try{
            var results = await _api.SearchAsync(query, premium);
            return Ok(results);
        } catch (Exception ex){
            _logger.LogError(ex, "Search failed for query: {Query}", query);
            return StatusCode(500, new { Error = "Search failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get episodes for a series
    /// </summary>
    [HttpGet("{seriesId}/episodes")]
    public async Task<ActionResult> GetEpisodes(string seriesId, [FromQuery] bool premium = false){
        try{
            var episodes = await _api.GetEpisodesAsync(seriesId, premium);
            return Ok(episodes);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get episodes for series: {SeriesId}", seriesId);
            return StatusCode(500, new { Error = "Failed to get episodes", Message = ex.Message });
        }
    }
}

[ApiController]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase{
    private readonly CruncharrConfig _config;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(CruncharrConfig config, ILogger<ConfigController> logger){
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Get current configuration (sanitized - no passwords)
    /// </summary>
    [HttpGet]
    public ActionResult GetConfig(){
        return Ok(new{
            Download = new{
                OutputDirectory = _config.Download.OutputDirectory,
                TempDirectory = _config.Download.TempDirectory,
                FilenameTemplate = _config.Download.FilenameTemplate,
                Quality = _config.Download.Quality,
                DubLanguages = _config.Download.DubLanguages,
                SubtitleLanguages = _config.Download.SubtitleLanguages,
                SimultaneousDownloads = _config.Download.SimultaneousDownloads,
                RetryAttempts = _config.Download.RetryAttempts,
                SkipMuxing = _config.Download.SkipMuxing,
                MuxFonts = _config.Download.MuxFonts,
                IncludeChapters = _config.Download.IncludeChapters
            },
            Queue = new{
                PersistQueue = _config.Queue.PersistQueue,
                AutoDownload = _config.Queue.AutoDownload,
                SimultaneousProcessingJobs = _config.Queue.SimultaneousProcessingJobs,
                QueueFilePath = _config.Queue.QueueFilePath
            },
            History = new{
                Enabled = _config.History.Enabled,
                RemoveMissing = _config.History.RemoveMissing
            },
            Notifications = new{
                WebhookUrl = !string.IsNullOrEmpty(_config.Notifications.WebhookUrl) ? "[configured]" : null,
                OnComplete = _config.Notifications.OnComplete,
                OnError = _config.Notifications.OnError
            }
        });
    }

    /// <summary>
    /// Update configuration
    /// </summary>
    [HttpPost]
    public IActionResult UpdateConfig([FromBody] ConfigUpdateRequest request){
        if (request.Crunchyroll != null){
            if (!string.IsNullOrEmpty(request.Crunchyroll.Email))
                _config.Crunchyroll.Email = request.Crunchyroll.Email;
        }

        if (request.Download != null){
            if (!string.IsNullOrEmpty(request.Download.OutputDirectory))
                _config.Download.OutputDirectory = request.Download.OutputDirectory;
            if (!string.IsNullOrEmpty(request.Download.Quality))
                _config.Download.Quality = request.Download.Quality;
            if (request.Download.SimultaneousDownloads.HasValue)
                _config.Download.SimultaneousDownloads = request.Download.SimultaneousDownloads.Value;
        }

        var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
        try{
            _config.Save(configPath);
            _logger.LogInformation("Configuration saved to {Path}", configPath);
            return Ok(new { Success = true, Message = "Configuration saved" });
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to save configuration");
            return StatusCode(500, new { Success = false, Message = ex.Message });
        }
    }
}

public class ConfigUpdateRequest{
    public CrunchyrollUpdateConfig? Crunchyroll { get; set; }
    public DownloadUpdateConfig? Download { get; set; }
}

public class CrunchyrollUpdateConfig{
    public string? Email { get; set; }
}

public class DownloadUpdateConfig{
    public string? OutputDirectory { get; set; }
    public string? Quality { get; set; }
    public int? SimultaneousDownloads { get; set; }
}
