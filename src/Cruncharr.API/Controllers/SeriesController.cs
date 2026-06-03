using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
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
    
    /// <summary>
    /// Mark an episode as watched on Crunchyroll
    /// </summary>
    [HttpPost("episodes/{episodeId}/mark-watched")]
    public async Task<ActionResult> MarkAsWatched(string episodeId){
        try{
            await _api.MarkAsWatchedAsync(episodeId);
            return Ok(new { Message = "Episode marked as watched" });
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to mark episode as watched: {EpisodeId}", episodeId);
            return StatusCode(500, new { Error = "Failed to mark as watched", Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Get all browseable series (paginated, alphabetical)
    /// </summary>
    [HttpGet("all")]
    public async Task<ActionResult> GetAllSeries([FromQuery] string? locale = null){
        try{
            var results = await _api.GetAllSeriesAsync(locale);
            return Ok(results);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get all series");
            return StatusCode(500, new { Error = "Failed to get all series", Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Get seasonal series by tag (e.g., winter-2024)
    /// </summary>
    [HttpGet("seasonal")]
    public async Task<ActionResult> GetSeasonalSeries([FromQuery] string season, [FromQuery] string year, [FromQuery] string? locale = null){
        if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(year)){
            return BadRequest(new { Error = "Season and year parameters are required" });
        }
        
        try{
            var results = await _api.GetSeasonalSeriesAsync(season, year, locale);
            return Ok(results);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get seasonal series: {Season} {Year}", season, year);
            return StatusCode(500, new { Error = "Failed to get seasonal series", Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Get grouped episode list with versions for a series (ported from upstream ListSeriesId)
    /// </summary>
    [HttpGet("{seriesId}/list")]
    public async Task<ActionResult> ListSeriesId(string seriesId, [FromQuery] string? locale = null, [FromQuery] bool forcedLocale = false, [FromQuery] string? seasonId = null, [FromQuery] List<string>? dubLang = null, [FromQuery] bool? all = null, [FromQuery] List<string>? e = null){
        try{
            var data = new CrunchyMultiDownload(dubLang ?? new List<string>(), all, e: e, s: seasonId);
            var result = await _api.ListSeriesIdAsync(seriesId, locale ?? "", data, forcedLocale);
            if (result == null){
                return NotFound(new { Error = "Series not found or no episodes available" });
            }
            return Ok(result);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to list series episodes: {SeriesId}", seriesId);
            return StatusCode(500, new { Error = "Failed to list series episodes", Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Select multi-dub episodes for queue (ported from upstream ItemSelectMultiDub)
    /// </summary>
    [HttpPost("item-select-multi-dub")]
    public ActionResult ItemSelectMultiDub([FromBody] ItemSelectMultiDubRequest request){
        try{
            if (request.Episodes == null || request.Episodes.Count == 0){
                return BadRequest(new { Error = "Episodes dictionary is required" });
            }
            
            var result = _api.ItemSelectMultiDub(request.Episodes, request.DubLang, request.All, request.E);
            return Ok(result);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to select multi-dub episodes");
            return StatusCode(500, new { Error = "Failed to select multi-dub episodes", Message = ex.Message });
        }
    }
}

public class ItemSelectMultiDubRequest{
    public Dictionary<string, EpisodeAndLanguage> Episodes{ get; set; } = new();
    public List<string> DubLang{ get; set; } = new();
    public bool? All{ get; set; }
    public List<string>? E{ get; set; }
}
