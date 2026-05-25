using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HistoryController : ControllerBase{
    private readonly IHistoryService _historyService;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(IHistoryService historyService, ILogger<HistoryController> logger){
        _historyService = historyService;
        _logger = logger;
    }

    /// <summary>
    /// Get download history
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<DownloadHistory>>> GetHistory(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0){
        var history = await _historyService.GetAllAsync();
        var paginated = history
            .Skip(offset)
            .Take(limit)
            .ToList();
        
        return Ok(paginated);
    }

    /// <summary>
    /// Get rich history with series/season/episode tree
    /// </summary>
    [HttpGet("rich")]
    public async Task<ActionResult<List<HistorySeriesResponse>>> GetRichHistory(){
        var history = await _historyService.GetHistorySeriesAsync();
        var response = history.Select(MapToResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Check if episode exists in history
    /// </summary>
    [HttpGet("check/{episodeId}/{audioLanguage}")]
    public async Task<ActionResult<HistoryCheckResponse>> CheckHistory(string episodeId, string audioLanguage){
        var exists = await _historyService.IsDownloadedAsync(episodeId, audioLanguage);
        return Ok(new HistoryCheckResponse{
            EpisodeId = episodeId,
            AudioLanguage = audioLanguage,
            Exists = exists
        });
    }

    /// <summary>
    /// Add entry to history
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddToHistory([FromBody] DownloadHistory entry){
        await _historyService.AddAsync(entry);
        return Ok(new { Message = "Added to history" });
    }

    /// <summary>
    /// Get history for a series
    /// </summary>
    [HttpGet("series/{seriesId}")]
    public async Task<ActionResult<HistorySeriesResponse?>> GetSeriesHistory(string seriesId){
        var history = await _historyService.GetHistorySeriesAsync();
        var series = history.FirstOrDefault(s => s.SeriesId == seriesId);
        if (series == null) return NotFound();
        return Ok(MapToResponse(series));
    }

    /// <summary>
    /// Mark episode as downloaded
    /// </summary>
    [HttpPost("downloaded/{seriesId}/{seasonId}/{episodeId}")]
    public async Task<IActionResult> SetDownloaded(string seriesId, string seasonId, string episodeId){
        await _historyService.SetAsDownloadedAsync(seriesId, seasonId, episodeId);
        return Ok(new { Message = "Marked as downloaded" });
    }

    /// <summary>
    /// Remove unavailable episodes from history
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(){
        await _historyService.RemoveUnavailableEpisodesAsync();
        return Ok(new { Message = "Cleaned up unavailable episodes" });
    }

    private static HistorySeriesResponse MapToResponse(HistorySeries series){
        return new HistorySeriesResponse{
            SeriesId = series.SeriesId,
            SeriesTitle = series.SeriesTitle,
            SeriesDescription = series.SeriesDescription,
            ThumbnailImageUrl = series.ThumbnailImageUrl,
            HasNewEpisodes = series.HasNewEpisodes,
            DownloadedEpisodes = series.DownloadedEpisodes,
            TotalEpisodes = series.TotalEpisodes,
            Seasons = series.Seasons.Select(s => new HistorySeasonResponse{
                SeasonId = s.SeasonId,
                SeasonTitle = s.SeasonTitle,
                SeasonNum = s.SeasonNum,
                SpecialSeason = s.SpecialSeason,
                DownloadedEpisodes = s.DownloadedEpisodes,
                Episodes = s.EpisodesList.Select(e => new HistoryEpisodeResponse{
                    EpisodeId = e.EpisodeId,
                    EpisodeTitle = e.EpisodeTitle,
                    EpisodeDescription = e.EpisodeDescription,
                    Episode = e.Episode,
                    EpisodeSeasonNum = e.EpisodeSeasonNum,
                    SpecialEpisode = e.SpecialEpisode,
                    WasDownloaded = e.WasDownloaded,
                    IsEpisodeAvailableOnStreamingService = e.IsEpisodeAvailableOnStreamingService,
                    ThumbnailImageUrl = e.ThumbnailImageUrl,
                    EpisodeCrPremiumAirDate = e.EpisodeCrPremiumAirDate
                }).ToList()
            }).ToList()
        };
    }
}

public class HistoryCheckResponse{
    public string EpisodeId { get; set; } = "";
    public string AudioLanguage { get; set; } = "";
    public bool Exists { get; set; }
}

public class HistorySeriesResponse{
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? SeriesDescription { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public bool HasNewEpisodes { get; set; }
    public int DownloadedEpisodes { get; set; }
    public int TotalEpisodes { get; set; }
    public List<HistorySeasonResponse> Seasons { get; set; } = [];
}

public class HistorySeasonResponse{
    public string? SeasonId { get; set; }
    public string? SeasonTitle { get; set; }
    public string? SeasonNum { get; set; }
    public bool SpecialSeason { get; set; }
    public int DownloadedEpisodes { get; set; }
    public List<HistoryEpisodeResponse> Episodes { get; set; } = [];
}

public class HistoryEpisodeResponse{
    public string? EpisodeId { get; set; }
    public string? EpisodeTitle { get; set; }
    public string? EpisodeDescription { get; set; }
    public string? Episode { get; set; }
    public string? EpisodeSeasonNum { get; set; }
    public bool SpecialEpisode { get; set; }
    public bool WasDownloaded { get; set; }
    public bool IsEpisodeAvailableOnStreamingService { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public DateTime? EpisodeCrPremiumAirDate { get; set; }
}
