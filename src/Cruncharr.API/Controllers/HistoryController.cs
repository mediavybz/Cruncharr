using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HistoryController : ControllerBase
{
    private readonly IHistoryService _historyService;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(IHistoryService historyService, ILogger<HistoryController> logger)
    {
        _historyService = historyService;
        _logger = logger;
    }

    private static bool IsValidId(string id) => !string.IsNullOrWhiteSpace(id) && !id.Contains("..") && !id.Contains("/") && !id.Contains("\\");

    /// <summary>
    /// Get download history
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<DownloadHistory>>> GetHistory(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        try
        {
            var history = await _historyService.GetAllAsync(offset, limit);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetHistory");
            return StatusCode(500, new { Error = "GetHistory failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get rich history with series/season/episode tree
    /// </summary>
    [HttpGet("rich")]
    public async Task<ActionResult<List<HistorySeriesResponse>>> GetRichHistory()
    {
        try
        {
            var history = await _historyService.GetHistorySeriesAsync();
            var response = history.Select(MapToResponse).ToList();
            return Ok(response);
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in GetRichHistory");
            return StatusCode(500, new { Error = "GetRichHistory failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Check if episode exists in history
    /// </summary>
    [HttpGet("check/{episodeId}/{audioLanguage}")]
    public async Task<ActionResult<HistoryCheckResponse>> CheckHistory(string episodeId, string audioLanguage)
    {
        if (!IsValidId(episodeId)) return BadRequest(new { Error = "Invalid episodeId" });
        try
        {
            var exists = await _historyService.IsDownloadedAsync(episodeId, audioLanguage);
            return Ok(new HistoryCheckResponse
            {
                EpisodeId = episodeId,
                AudioLanguage = audioLanguage,
                Exists = exists
            });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in CheckHistory");
            return StatusCode(500, new { Error = "CheckHistory failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Add entry to history
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddToHistory([FromBody] DownloadHistory entry)
    {
        try
        {
            await _historyService.AddAsync(entry);
            return Ok(new { Message = "Added to history" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in AddToHistory");
            return StatusCode(500, new { Error = "AddToHistory failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get history for a series
    /// </summary>
    [HttpGet("series/{seriesId}")]
    public async Task<ActionResult<HistorySeriesResponse?>> GetSeriesHistory(string seriesId)
    {
        if (!IsValidId(seriesId)) return BadRequest(new { Error = "Invalid seriesId" });
        try
        {
            var history = await _historyService.GetHistorySeriesAsync();
            var series = history?.FirstOrDefault(s => s.SeriesId == seriesId);
            if (series == null) return NotFound();
            return Ok(MapToResponse(series));
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in GetSeriesHistory");
            return StatusCode(500, new { Error = "GetSeriesHistory failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Remove a series from history (rich + flat) so the user can drop series they no longer track.
    /// </summary>
    [HttpDelete("series/{seriesId}")]
    public async Task<IActionResult> RemoveSeries(string seriesId)
    {
        if (!IsValidId(seriesId)) return BadRequest(new { Error = "Invalid seriesId" });
        try
        {
            var removed = await _historyService.RemoveSeriesAsync(seriesId);
            return Ok(new { removed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RemoveSeries");
            return StatusCode(500, new { Error = "RemoveSeries failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Mark episode as downloaded
    /// </summary>
    [HttpPost("downloaded/{seriesId}/{seasonId}/{episodeId}")]
    public async Task<IActionResult> SetDownloaded(string seriesId, string seasonId, string episodeId)
    {
        if (!IsValidId(seriesId) || !IsValidId(seasonId) || !IsValidId(episodeId)) return BadRequest(new { Error = "Invalid path parameters" });
        try
        {
            await _historyService.SetAsDownloadedAsync(seriesId, seasonId, episodeId);
            return Ok(new { Message = "Marked as downloaded" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in SetDownloaded");
            return StatusCode(500, new { Error = "SetDownloaded failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Remove unavailable episodes from history
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup()
    {
        try
        {
            await _historyService.RemoveUnavailableEpisodesAsync();
            return Ok(new { Message = "Cleaned up unavailable episodes" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in Cleanup");
            return StatusCode(500, new { Error = "Cleanup failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Update series data from Crunchyroll
    /// </summary>
    [HttpPost("update-series/{seriesId}")]
    public async Task<IActionResult> UpdateSeries(string seriesId, [FromQuery] string? seasonId = null)
    {
        if (!IsValidId(seriesId)) return BadRequest(new { Error = "Invalid seriesId" });
        if (seasonId != null && !IsValidId(seasonId)) return BadRequest(new { Error = "Invalid seasonId" });
        try
        {
            var result = await _historyService.CrUpdateSeriesAsync(seriesId, seasonId);
            return Ok(new { Success = result });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in UpdateSeries");
            return StatusCode(500, new { Error = "UpdateSeries failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Sort history items
    /// </summary>
    [HttpPost("sort")]
    public async Task<IActionResult> Sort()
    {
        try
        {
            await _historyService.SortItemsAsync();
            return Ok(new { Message = "History sorted" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in Sort");
            return StatusCode(500, new { Error = "Sort failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get episode with download directory
    /// </summary>
    [HttpGet("episode-with-dir/{seriesId}/{seasonId}/{episodeId}")]
    public async Task<ActionResult> GetEpisodeWithDir(string seriesId, string seasonId, string episodeId)
    {
        if (!IsValidId(seriesId) || !IsValidId(seasonId) || !IsValidId(episodeId)) return BadRequest(new { Error = "Invalid path parameters" });
        try
        {
            var (episode, dir) = await _historyService.GetHistoryEpisodeWithDownloadDirAsync(seriesId, seasonId, episodeId);
            if (episode == null) return NotFound();
            return Ok(new { Episode = episode, DownloadDir = dir });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in GetEpisodeWithDir");
            return StatusCode(500, new { Error = "GetEpisodeWithDir failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get episode with dub/sub lists and download directory
    /// </summary>
    [HttpGet("episode-with-dubs/{seriesId}/{seasonId}/{episodeId}")]
    public async Task<ActionResult> GetEpisodeWithDubs(string seriesId, string seasonId, string episodeId)
    {
        if (!IsValidId(seriesId) || !IsValidId(seasonId) || !IsValidId(episodeId)) return BadRequest(new { Error = "Invalid path parameters" });
        try
        {
            var (episode, dubs, subs, dir, quality) = await _historyService.GetHistoryEpisodeWithDubListAndDownloadDirAsync(seriesId, seasonId, episodeId);
            if (episode == null) return NotFound();
            return Ok(new { Episode = episode, DubList = dubs, SubList = subs, DownloadDir = dir, VideoQuality = quality });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in GetEpisodeWithDubs");
            return StatusCode(500, new { Error = "GetEpisodeWithDubs failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get dub list for series/season
    /// </summary>
    [HttpGet("dubs/{seriesId}/{seasonId}")]
    public async Task<ActionResult<List<string>>> GetDubList(string seriesId, string seasonId)
    {
        if (!IsValidId(seriesId) || !IsValidId(seasonId)) return BadRequest(new { Error = "Invalid path parameters" });
        try
        {
            var dubs = await _historyService.GetDubListAsync(seriesId, seasonId);
            return Ok(dubs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDubList");
            return StatusCode(500, new { Error = "GetDubList failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get sub list and video quality for series/season
    /// </summary>
    [HttpGet("subs/{seriesId}/{seasonId}")]
    public async Task<ActionResult> GetSubList(string seriesId, string seasonId)
    {
        if (!IsValidId(seriesId) || !IsValidId(seasonId)) return BadRequest(new { Error = "Invalid path parameters" });
        try
        {
            var (subs, quality) = await _historyService.GetSubListAsync(seriesId, seasonId);
            return Ok(new { SubList = subs, VideoQuality = quality });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in GetSubList");
            return StatusCode(500, new { Error = "GetSubList failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Match all history series with Sonarr
    /// </summary>
    [HttpPost("sonarr/match-series")]
    public async Task<IActionResult> MatchHistorySeriesWithSonarr([FromQuery] bool updateAll = false)
    {
        try
        {
            var result = await _historyService.MatchHistorySeriesWithSonarrAsync(updateAll);
            string message;
            if (result.SonarrSeriesCount == 0)
                message = "No series returned from Sonarr - check the connection (Settings > Sonarr > Test Connection).";
            else if (result.HistoryTotal == 0)
                message = "No history series to match yet.";
            else
                message = $"Matched {result.Matched} of {result.HistoryTotal} series ({result.SonarrSeriesCount} in Sonarr).";
            return Ok(new { Message = message, result.HistoryTotal, result.Matched, result.SonarrSeriesCount });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in MatchHistorySeriesWithSonarr");
            return StatusCode(500, new { Error = "MatchHistorySeriesWithSonarr failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Match episodes for a specific series with Sonarr
    /// </summary>
    [HttpPost("sonarr/match-episodes/{seriesId}")]
    public async Task<IActionResult> MatchHistoryEpisodesWithSonarr(string seriesId, [FromQuery] bool rematchAll = false)
    {
        if (!IsValidId(seriesId)) return BadRequest(new { Error = "Invalid seriesId" });
        try
        {
            await _historyService.MatchHistoryEpisodesWithSonarrAsync(seriesId, rematchAll);
            return Ok(new { Message = "Episode matching completed" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in MatchHistoryEpisodesWithSonarr");
            return StatusCode(500, new { Error = "MatchHistoryEpisodesWithSonarr failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Update series settings override (quality, dubs, subs)
    /// </summary>
    [HttpPost("series/{seriesId}/settings")]
    public async Task<IActionResult> SetSeriesSettingsOverride(string seriesId, [FromBody] HistorySettingsOverrideRequest request)
    {
        if (!IsValidId(seriesId)) return BadRequest(new { Error = "Invalid seriesId" });
        if (request == null) return BadRequest(new { Error = "Request body is required" });
        try
        {
            await _historyService.SetSeriesSettingsOverrideAsync(seriesId, request.VideoQuality, request.DubLanguages, request.SoftSubs);
            return Ok(new { Message = "Series settings updated" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in SetSeriesSettingsOverride");
            return StatusCode(500, new { Error = "SetSeriesSettingsOverride failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Update season settings override (quality, dubs, subs)
    /// </summary>
    [HttpPost("season/{seasonId}/settings")]
    public async Task<IActionResult> SetSeasonSettingsOverride(string seasonId, [FromBody] HistorySettingsOverrideRequest request)
    {
        if (!IsValidId(seasonId)) return BadRequest(new { Error = "Invalid seasonId" });
        if (request == null) return BadRequest(new { Error = "Request body is required" });
        try
        {
            await _historyService.SetSeasonSettingsOverrideAsync(seasonId, request.VideoQuality, request.DubLanguages, request.SoftSubs);
            return Ok(new { Message = "Season settings updated" });
        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Error in SetSeasonSettingsOverride");
            return StatusCode(500, new { Error = "SetSeasonSettingsOverride failed", Message = ex.Message });
        }
    }

    private static HistorySeriesResponse MapToResponse(HistorySeries series)
    {
        return new HistorySeriesResponse
        {
            SeriesId = series.SeriesId,
            SeriesTitle = series.SeriesTitle,
            SeriesDescription = series.SeriesDescription,
            ThumbnailImageUrl = series.ThumbnailImageUrl,
            HasNewEpisodes = series.HasNewEpisodes,
            DownloadedEpisodes = series.DownloadedEpisodes,
            TotalEpisodes = series.TotalEpisodes,
            SonarrSeriesId = series.SonarrSeriesId,
            SonarrTvDbId = series.SonarrTvDbId,
            SonarrSlugTitle = series.SonarrSlugTitle,
            SonarrNextAirDate = series.SonarrNextAirDate,
            Seasons = series.Seasons?.Select(s => new HistorySeasonResponse
            {
                SeasonId = s.SeasonId,
                SeasonTitle = s.SeasonTitle,
                SeasonNum = s.SeasonNum,
                SpecialSeason = s.SpecialSeason,
                DownloadedEpisodes = s.DownloadedEpisodes,
                Episodes = s.EpisodesList?.Select(e => new HistoryEpisodeResponse
                {
                    EpisodeId = e.EpisodeId,
                    EpisodeTitle = e.EpisodeTitle,
                    EpisodeDescription = e.EpisodeDescription,
                    Episode = e.Episode,
                    EpisodeSeasonNum = e.EpisodeSeasonNum,
                    SpecialEpisode = e.SpecialEpisode,
                    WasDownloaded = e.WasDownloaded,
                    IsEpisodeAvailableOnStreamingService = e.IsEpisodeAvailableOnStreamingService,
                    ThumbnailImageUrl = e.ThumbnailImageUrl,
                    EpisodeCrPremiumAirDate = e.EpisodeCrPremiumAirDate,
                    SonarrEpisodeId = e.SonarrEpisodeId,
                    SonarrEpisodeNumber = e.SonarrEpisodeNumber,
                    SonarrHasFile = e.SonarrHasFile,
                    SonarrIsMonitored = e.SonarrIsMonitored,
                    SonarrAbsolutNumber = e.SonarrAbsolutNumber,
                    SonarrSeasonNumber = e.SonarrSeasonNumber,
                    SonarrSeasonEpisodeText = e.SonarrSeasonEpisodeText,
                    DownloadedDubLang = e.DownloadedDubLang ?? new List<string>(),
                    DownloadedSoftSubs = e.DownloadedSoftSubs ?? new List<string>(),
                    AvailableDubLang = e.HistoryEpisodeAvailableDubLang ?? new List<string>(),
                    AvailableSoftSubs = e.HistoryEpisodeAvailableSoftSubs ?? new List<string>()
                }).ToList() ?? new List<HistoryEpisodeResponse>()
            }).ToList() ?? new List<HistorySeasonResponse>()
        };
    }
}

public class HistoryCheckResponse
{
    public string EpisodeId { get; set; } = "";
    public string AudioLanguage { get; set; } = "";
    public bool Exists { get; set; }
}

public class HistorySeriesResponse
{
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? SeriesDescription { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public bool HasNewEpisodes { get; set; }
    public int DownloadedEpisodes { get; set; }
    public int TotalEpisodes { get; set; }
    public List<HistorySeasonResponse> Seasons { get; set; } = [];
    // Sonarr fields
    public string? SonarrSeriesId { get; set; }
    public string? SonarrTvDbId { get; set; }
    public string? SonarrSlugTitle { get; set; }
    public string? SonarrNextAirDate { get; set; }
}

public class HistorySeasonResponse
{
    public string? SeasonId { get; set; }
    public string? SeasonTitle { get; set; }
    public string? SeasonNum { get; set; }
    public bool SpecialSeason { get; set; }
    public int DownloadedEpisodes { get; set; }
    public List<HistoryEpisodeResponse> Episodes { get; set; } = [];
}

public class HistoryEpisodeResponse
{
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
    // Sonarr fields
    public string? SonarrEpisodeId { get; set; }
    public string? SonarrEpisodeNumber { get; set; }
    public bool SonarrHasFile { get; set; }
    public bool SonarrIsMonitored { get; set; }
    public string? SonarrAbsolutNumber { get; set; }
    public string? SonarrSeasonNumber { get; set; }
    public string SonarrSeasonEpisodeText { get; set; } = "";
    public List<string> DownloadedDubLang { get; set; } = [];
    public List<string> DownloadedSoftSubs { get; set; } = [];
    public List<string> AvailableDubLang { get; set; } = [];
    public List<string> AvailableSoftSubs { get; set; } = [];
}

public class HistorySettingsOverrideRequest
{
    public string? VideoQuality { get; set; }
    public List<string>? DubLanguages { get; set; }
    public List<string>? SoftSubs { get; set; }
}
