using System.Text.RegularExpressions;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CalendarController : ControllerBase{
    private readonly ICalendarService _calendarService;
    private readonly CruncharrConfig _config;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(ICalendarService calendarService, CruncharrConfig config, ILogger<CalendarController> logger){
        _calendarService = calendarService;
        _config = config;
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

            var response = MapToResponse(week, language);
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
            var response = MapToResponse(week, language);
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

    private CalendarWeekResponse MapToResponse(CalendarWeek week, string language = "en-us"){
        var hideDubs = _config.Calendar.HideDubs;
        return new CalendarWeekResponse{
            StartDate = week.FirstDayOfWeek,
            Days = week.CalendarDays?.Select(day => new CalendarDayResponse{
                Date = day.DateTime,
                DayName = day.DayName ?? day.DateTime.ToString("dddd"),
                Episodes = day.CalendarEpisodes
                    .Where(e => !hideDubs || !CrSimulcastCalendarFilter.IsDubOrAltLanguageSeason(e.SeasonName))
                    .Where(e => MatchesLanguage(e.SeasonName, language))
                    .Select(MapEpisodeToResponse)
                    .ToList()
            }).ToList() ?? new List<CalendarDayResponse>()
        };
    }

    private static bool MatchesLanguage(string? seasonName, string language){
        if (string.IsNullOrEmpty(seasonName)) return true;
        
        // Extract content inside last parentheses (handles nested parens like "(Português (Brasil))")
        var lastOpenParen = seasonName.LastIndexOf('(');
        var lastCloseParen = seasonName.LastIndexOf(')');
        
        if (lastOpenParen == -1 || lastCloseParen == -1 || lastOpenParen > lastCloseParen)
            return true; // No language tag = original/Japanese, always show
        
        // Extract the full parenthetical content
        var tag = seasonName.Substring(lastOpenParen + 1, lastCloseParen - lastOpenParen - 1).Trim().ToLowerInvariant();
        
        // Map language codes to common tags
        var langMap = new Dictionary<string, string[]>{
            ["en-us"] = new[] { "english", "en-us" },
            ["ja-jp"] = new[] { "japanese", "ja-jp", "日本語" },
            ["es"] = new[] { "español", "espanol", "spanish", "américa latina", "america latina", "latin america", "español (latinoamérica)", "espanol (latinoamerica)" },
            ["es-es"] = new[] { "español (españa)", "espanol (espana)", "spanish (spain)", "españa", "espana" },
            ["pt-br"] = new[] { "português (brasil)", "portugues (brasil)", "portuguese (brazil)", "brasil", "brazil" },
            ["pt-pt"] = new[] { "português (portugal)", "portugues (portugal)", "portuguese (portugal)", "portugal" },
            ["fr"] = new[] { "français", "francais", "french" },
            ["de"] = new[] { "deutsch", "german" },
            ["it"] = new[] { "italiano", "italian" },
            ["ru"] = new[] { "рус", "russian", "русский" },
            ["ar"] = new[] { "العربية", "arabic" },
            ["hi"] = new[] { "हिन्दी", "hindi" }
        };
        
        if (langMap.TryGetValue(language.ToLowerInvariant(), out var keywords)){
            return keywords.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        
        return true;
    }

    private static CalendarEpisodeResponse MapEpisodeToResponse(CalendarEpisode episode){
        // Extract episode ID from EpisodeUrl (e.g., /watch/G0DUN2EZP/to-defeat-muzan-kibutsuji)
        var episodeId = episode.CrSeriesID ?? "";
        if (!string.IsNullOrEmpty(episode.EpisodeUrl)){
            var parts = episode.EpisodeUrl.Trim('/').Split('/');
            if (parts.Length >= 2 && parts[^2] == "watch" && parts[^1].Length > 0){
                // URL format: /watch/{episodeId}/{slug}
                episodeId = parts[^1];
            } else if (parts.Length >= 2 && parts[0] == "watch"){
                // URL format: watch/{episodeId}/{slug}
                episodeId = parts[1];
            }
        }
        
        return new CalendarEpisodeResponse{
            Id = episodeId,
            Title = episode.EpisodeName ?? "",
            SeriesTitle = episode.SeasonName ?? "",
            SeriesId = episode.CrSeriesID,
            EpisodeNumber = episode.EpisodeNumber ?? "",
            AirDate = episode.DateTime,
            IsPremiumOnly = episode.IsPremiumOnly,
            IsPremiere = episode.IsPremiere,
            ThumbnailUrl = episode.ThumbnailUrl,
            HasAired = episode.HasPassed ?? false,
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
