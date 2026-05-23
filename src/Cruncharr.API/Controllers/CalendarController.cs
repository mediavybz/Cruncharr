using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CalendarController : ControllerBase{
    private readonly ICalendarService _calendarService;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(ICalendarService calendarService, ILogger<CalendarController> logger){
        _calendarService = calendarService;
        _logger = logger;
    }

    /// <summary>
    /// Get calendar for a specific week
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CalendarWeekResponse>> GetCalendar(
        [FromQuery] string? date = null,
        [FromQuery] string language = "en-us",
        [FromQuery] bool forceUpdate = false){
        try{
            var targetDate = string.IsNullOrEmpty(date) 
                ? DateTime.Now 
                : DateTime.Parse(date);
            
            // Get Monday of the week
            var monday = targetDate.AddDays(-(int)targetDate.DayOfWeek + (int)DayOfWeek.Monday);
            if (targetDate.DayOfWeek == DayOfWeek.Sunday) monday = monday.AddDays(-7);
            
            var week = await _calendarService.GetCalendarForDateAsync(
                monday.ToString("yyyy-MM-dd"), 
                language, 
                forceUpdate);

            var response = MapToResponse(week);
            return Ok(response);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get calendar");
            return StatusCode(500, new { Error = "Failed to get calendar", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get custom calendar using API data
    /// </summary>
    [HttpGet("custom")]
    public async Task<ActionResult<CalendarWeekResponse>> GetCustomCalendar(
        [FromQuery] string? date = null,
        [FromQuery] string language = "en-us",
        [FromQuery] bool forceUpdate = false){
        try{
            var targetDate = string.IsNullOrEmpty(date) 
                ? DateTime.Now 
                : DateTime.Parse(date);
            
            var week = await _calendarService.GetCustomCalendarAsync(targetDate, language, forceUpdate);
            var response = MapToResponse(week);
            return Ok(response);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get custom calendar");
            return StatusCode(500, new { Error = "Failed to get custom calendar", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get upcoming episodes for the next 7 days
    /// </summary>
    [HttpGet("upcoming")]
    public async Task<ActionResult<List<CalendarEpisodeResponse>>> GetUpcoming([FromQuery] string language = "en-us"){
        try{
            var upcoming = await _calendarService.GetUpcomingEpisodesAsync(language);
            var response = upcoming.Select(MapEpisodeToResponse).ToList();
            return Ok(response);
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to get upcoming episodes");
            return StatusCode(500, new { Error = "Failed to get upcoming episodes", Message = ex.Message });
        }
    }

    private static CalendarWeekResponse MapToResponse(CalendarWeek week){
        return new CalendarWeekResponse{
            StartDate = week.FirstDayOfWeek,
            Days = week.CalendarDays?.Select(day => new CalendarDayResponse{
                Date = day.DateTime,
                DayName = day.DayName ?? day.DateTime.ToString("dddd"),
                Episodes = day.CalendarEpisodes.Select(MapEpisodeToResponse).ToList()
            }).ToList() ?? new List<CalendarDayResponse>()
        };
    }

    private static CalendarEpisodeResponse MapEpisodeToResponse(CalendarEpisode episode){
        return new CalendarEpisodeResponse{
            Id = episode.CrSeriesID ?? Guid.NewGuid().ToString(),
            Title = episode.EpisodeName ?? "",
            SeriesTitle = episode.SeasonName ?? "",
            SeriesId = episode.CrSeriesID,
            EpisodeNumber = episode.EpisodeNumber ?? "",
            AirDate = episode.DateTime,
            IsPremiumOnly = episode.IsPremiumOnly,
            IsPremiere = episode.IsPremiere,
            ThumbnailUrl = episode.ThumbnailUrl,
            HasAired = episode.HasPassed ?? false
        };
    }
}

public class CalendarWeekResponse{
    public DateTime StartDate { get; set; }
    public List<CalendarDayResponse> Days { get; set; } = new();
}

public class CalendarDayResponse{
    public DateTime Date { get; set; }
    public string DayName { get; set; } = "";
    public List<CalendarEpisodeResponse> Episodes { get; set; } = new();
}

public class CalendarEpisodeResponse{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    public string? SeriesId { get; set; }
    public string EpisodeNumber { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public bool IsPremiumOnly { get; set; }
    public bool IsPremiere { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool HasAired { get; set; }
}
