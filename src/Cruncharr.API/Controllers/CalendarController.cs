using System.Globalization;
using System.Text.RegularExpressions;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CalendarController : ControllerBase
{
    private readonly ICalendarService _calendarService;
    private readonly CruncharrConfig _config;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(ICalendarService calendarService, CruncharrConfig config, ILogger<CalendarController> logger)
    {
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
        [FromQuery] bool forceUpdate = false)
    {
        try
        {
            DateTime targetDate;
            if (string.IsNullOrEmpty(date))
            {
                targetDate = DateTime.Now;
            }
            else if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out targetDate))
            {
                return BadRequest(new { Error = "Invalid date format. Use yyyy-MM-dd" });
            }

            // Get Monday of the week
            var monday = targetDate.AddDays(-(int)targetDate.DayOfWeek + (int)DayOfWeek.Monday);
            if (targetDate.DayOfWeek == DayOfWeek.Sunday) monday = monday.AddDays(-7);

            var week = await _calendarService.GetCalendarForDateAsync(
                monday.ToString("yyyy-MM-dd"),
                language,
                forceUpdate);

            var response = MapToResponse(week, language);
            return Ok(response);
        }
        catch (Exception ex)
        {
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
        [FromQuery] bool forceUpdate = false,
        [FromQuery] string? dubFilter = null)
    {
        try
        {
            DateTime targetDate;
            if (string.IsNullOrEmpty(date))
            {
                targetDate = DateTime.Now;
            }
            else if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate))
            {
                return BadRequest(new { Error = "Invalid date format", Message = "Date must be in yyyy-MM-dd format" });
            }

            var week = await _calendarService.GetCustomCalendarAsync(targetDate, language, forceUpdate);
            // Query param overrides the configured calendar dub filter
            var effectiveDubFilter = dubFilter ?? _config.Calendar?.DubFilter;
            var response = MapCustomToResponse(week, effectiveDubFilter, _config.Calendar?.HideDubs ?? false);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get custom calendar");
            return StatusCode(500, new { Error = "Failed to get custom calendar", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get upcoming episodes for the next 7 days
    /// </summary>
    [HttpGet("upcoming")]
    public async Task<ActionResult<List<CalendarEpisodeResponse>>> GetUpcoming([FromQuery] string language = "en-us")
    {
        try
        {
            var upcoming = await _calendarService.GetUpcomingEpisodesAsync(language);
            var response = upcoming.Select(MapEpisodeToResponse).ToList();
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get upcoming episodes");
            return StatusCode(500, new { Error = "Failed to get upcoming episodes", Message = ex.Message });
        }
    }

    private CalendarWeekResponse MapToResponse(CalendarWeek week, string language = "en-us")
    {
        var hideDubs = _config.Calendar?.HideDubs ?? false;
        return new CalendarWeekResponse
        {
            StartDate = week.FirstDayOfWeek,
            Days = week.CalendarDays?.Select(day => new CalendarDayResponse
            {
                Date = day.DateTime,
                DayName = day.DayName ?? day.DateTime.ToString("dddd"),
                Episodes = day.CalendarEpisodes
                    .Where(e => !hideDubs || !CrSimulcastCalendarFilter.IsDubOrAltLanguageSeason(e.SeasonName))
                    .Where(e => CrSimulcastCalendarFilter.MatchesLanguage(e.SeasonName, language))
                    .Select(MapEpisodeToResponse)
                    .ToList()
            }).ToList() ?? new List<CalendarDayResponse>()
        };
    }

    // Custom (API-based) calendar: filter by actual episode audio locale instead of
    // season-name keyword matching - mirrors upstream CalendarManager dub filtering
    private CalendarWeekResponse MapCustomToResponse(CalendarWeek week, string? dubFilter, bool hideDubs)
    {
        return new CalendarWeekResponse
        {
            StartDate = week.FirstDayOfWeek,
            Days = week.CalendarDays?.Select(day => new CalendarDayResponse
            {
                Date = day.DateTime,
                DayName = day.DayName ?? day.DateTime.ToString("dddd"),
                Episodes = day.CalendarEpisodes
                    .Where(e => !e.FilteredOut)
                    .Where(e => IncludeCustomEpisode(e, dubFilter, hideDubs))
                    .Select(MapEpisodeToResponse)
                    .ToList()
            }).ToList() ?? new List<CalendarDayResponse>()
        };
    }

    private static bool IncludeCustomEpisode(CalendarEpisode episode, string? dubFilter, bool hideDubs)
    {
        bool hasFilter = !string.IsNullOrEmpty(dubFilter) &&
                         !string.Equals(dubFilter, "none", StringComparison.OrdinalIgnoreCase);

        bool MatchesFilter(CalendarEpisode ep) =>
            string.Equals(ep.AudioLocale, dubFilter, StringComparison.OrdinalIgnoreCase);

        // AniList upcoming entries carry no audio locale - upstream shows them regardless
        if (episode.AnilistEpisode)
        {
            return true;
        }

        if (hasFilter && !MatchesFilter(episode) && !episode.CalendarEpisodes.Any(MatchesFilter))
        {
            return false;
        }

        if (hideDubs && episode.SeasonName != null &&
            (episode.SeasonName.EndsWith("Dub)") || episode.SeasonName.EndsWith("Audio)")) &&
            (!hasFilter || !MatchesFilter(episode)))
        {
            return false;
        }

        return true;
    }

    private static CalendarEpisodeResponse MapEpisodeToResponse(CalendarEpisode episode)
    {
        // Extract episode ID from EpisodeUrl
        // Handles: /watch/G0DUN2EZP/... , /es/watch/G0DUN2EZP/... , https://crunchyroll.com/watch/G0DUN2EZP/...
        var episodeId = episode.CrSeriesID ?? "";
        if (!string.IsNullOrEmpty(episode.EpisodeUrl))
        {
            var url = episode.EpisodeUrl.Trim('/');
            // Find the segment after "watch/"
            var watchIndex = url.IndexOf("watch/", StringComparison.OrdinalIgnoreCase);
            if (watchIndex >= 0)
            {
                var afterWatch = url.Substring(watchIndex + 6); // 6 = "watch/".Length
                var parts = afterWatch.Split('/');
                if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                {
                    episodeId = parts[0]; // The episode ID is the first segment after watch/
                }
            }
        }

        return new CalendarEpisodeResponse
        {
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
            AudioLocale = episode.AudioLocale,
            // [PT] Upstream calendar history marks
            IsInHistory = episode.IsInHistory,
            ShowHistoryMark = episode.ShowHistoryMark,
            HistoryDownloadState = episode.HistoryDownloadState.ToString(),
        };

    }
}

public class CalendarWeekResponse
{
    public DateTime StartDate { get; set; }
    public List<CalendarDayResponse> Days { get; set; } = new();
}

public class CalendarDayResponse
{
    public DateTime Date { get; set; }
    public string DayName { get; set; } = "";
    public List<CalendarEpisodeResponse> Episodes { get; set; } = new();
}

public class CalendarEpisodeResponse
{
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
    public string? AudioLocale { get; set; }
    public bool IsInHistory { get; set; }
    public bool ShowHistoryMark { get; set; }
    public string HistoryDownloadState { get; set; } = "None";
}
