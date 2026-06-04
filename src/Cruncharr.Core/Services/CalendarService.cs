using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

public interface ICalendarService
{
    Task<CalendarWeek> GetCalendarForDateAsync(string weeksMondayDate, string language, bool forceUpdate = false);
    Task<CalendarWeek> GetCustomCalendarAsync(DateTime targetDate, string language, bool forceUpdate = false);
    Task<List<CalendarEpisode>> GetUpcomingEpisodesAsync(string language);
}

public class CalendarService : ICalendarService
{
    private readonly ILogger<CalendarService>? _logger;
    private readonly ICrunchyrollApiService _apiService;
    private readonly ICrunchyrollAuthService _authService;
    private readonly HttpClientWrapper _httpClient;

    private readonly Dictionary<string, CalendarWeek> _calendarCache = new();
    private readonly Dictionary<string, List<CalendarEpisode>> _anilistCache = new();

    private readonly Dictionary<string, string> _calendarLanguageUrls = new(){
        { "en-us", "https://www.crunchyroll.com/simulcastcalendar" },
        { "es", "https://www.crunchyroll.com/es/simulcastcalendar" },
        { "es-es", "https://www.crunchyroll.com/es-es/simulcastcalendar" },
        { "pt-br", "https://www.crunchyroll.com/pt-br/simulcastcalendar" },
        { "pt-pt", "https://www.crunchyroll.com/pt-pt/simulcastcalendar" },
        { "fr", "https://www.crunchyroll.com/fr/simulcastcalendar" },
        { "de", "https://www.crunchyroll.com/de/simulcastcalendar" },
        { "ar", "https://www.crunchyroll.com/ar/simulcastcalendar" },
        { "it", "https://www.crunchyroll.com/it/simulcastcalendar" },
        { "ru", "https://www.crunchyroll.com/ru/simulcastcalendar" },
        { "hi", "https://www.crunchyroll.com/hi/simulcastcalendar" },
    };

    public CalendarService(ICrunchyrollApiService apiService, ICrunchyrollAuthService authService, ILogger<CalendarService>? logger = null)
    {
        _apiService = apiService;
        _authService = authService;
        _logger = logger;
        _httpClient = new HttpClientWrapper();
    }

    public async Task<CalendarWeek> GetCalendarForDateAsync(string weeksMondayDate, string language, bool forceUpdate = false)
    {
        var cacheKey = $"{language}_{weeksMondayDate}";

        if (!forceUpdate && _calendarCache.TryGetValue(cacheKey, out var cachedWeek))
        {
            return cachedWeek;
        }

        var url = _calendarLanguageUrls.ContainsKey(language ?? "en-us")
            ? $"{_calendarLanguageUrls[language ?? "en-us"]}?filter=premium&date={weeksMondayDate}"
            : $"{_calendarLanguageUrls["en-us"]}?filter=premium&date={weeksMondayDate}";

        var request = HttpClientWrapper.CreateRequest(url, HttpMethod.Get, false);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");

        var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

        if (!isOk)
        {
            if (content.Contains("<title>Just a moment...</title>") ||
                content.Contains("<title>Access denied</title>") ||
                content.Contains("<title>Attention Required! | Cloudflare</title>") ||
                content.Trim().Equals("error code: 1020") ||
                content.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1)
            {
                _logger?.LogError("Blocked by Cloudflare. Use the custom calendar.");
            }
            else
            {
                _logger?.LogError("Calendar request failed: {Error}", error);
            }

            return new CalendarWeek { CalendarDays = new List<CalendarDay>() };
        }

        var week = ParseCalendarHtml(content);

        if (week != null)
        {
            _calendarCache[cacheKey] = week;
        }

        return week ?? new CalendarWeek { CalendarDays = new List<CalendarDay>() };
    }

    private CalendarWeek? ParseCalendarHtml(string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(WebUtility.HtmlDecode(html));

            var week = new CalendarWeek
            {
                CalendarDays = new List<CalendarDay>()
            };

            var dayNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'day')]");

            if (dayNodes != null)
            {
                foreach (var day in dayNodes)
                {
                    var date = day.SelectSingleNode(".//time[@datetime]")?.GetAttributeValue("datetime", null);
                    if (date != null)
                    {
                        DateTime dayDateTime = DateTime.Parse(date, null, DateTimeStyles.RoundtripKind);

                        if (week.FirstDayOfWeek == DateTime.MinValue)
                        {
                            week.FirstDayOfWeek = dayDateTime;
                            week.FirstDayOfWeekString = dayDateTime.ToString("yyyy-MM-dd");
                        }

                        var dayName = day.SelectSingleNode(".//h1[@class='day-name']/time")?.InnerText.Trim();

                        var calDay = new CalendarDay
                        {
                            CalendarEpisodes = new List<CalendarEpisode>(),
                            DayName = dayName,
                            DateTime = dayDateTime
                        };

                        var episodes = day.SelectNodes(".//article[contains(@class, 'release')]");
                        if (episodes != null)
                        {
                            foreach (var episode in episodes)
                            {
                                var episodeTimeStr = episode.SelectSingleNode(".//time[contains(@class, 'available-time')]")?.GetAttributeValue("datetime", null);
                                if (episodeTimeStr != null)
                                {
                                    DateTime episodeTime = DateTime.Parse(episodeTimeStr, null, DateTimeStyles.RoundtripKind);
                                    var hasPassed = DateTime.Now > episodeTime;

                                    var episodeName = episode.SelectSingleNode(".//h1[contains(@class, 'episode-name')]")?.SelectSingleNode(".//cite[@itemprop='name']")?.InnerText.Trim();
                                    var seasonLink = episode.SelectSingleNode(".//a[contains(@class, 'js-season-name-link')]")?.GetAttributeValue("href", null);
                                    var episodeLink = episode.SelectSingleNode(".//a[contains(@class, 'available-episode-link')]")?.GetAttributeValue("href", null);
                                    var thumbnailUrl = episode.SelectSingleNode(".//img[contains(@class, 'thumbnail')]")?.GetAttributeValue("src", null);
                                    var isPremiumOnly = episode.SelectSingleNode(".//svg[contains(@class, 'premium-flag')]") != null;
                                    var isPremiere = episode.SelectSingleNode(".//div[contains(@class, 'premiere-flag')]") != null;
                                    var seasonName = episode.SelectSingleNode(".//a[contains(@class, 'js-season-name-link')]")?.SelectSingleNode(".//cite[@itemprop='name']")?.InnerText.Trim();
                                    var episodeNumber = episode.SelectSingleNode(".//meta[contains(@itemprop, 'episodeNumber')]")?.GetAttributeValue("content", "?");

                                    var calEpisode = new CalendarEpisode
                                    {
                                        DateTime = episodeTime,
                                        HasPassed = hasPassed,
                                        EpisodeName = episodeName,
                                        SeriesUrl = seasonLink,
                                        EpisodeUrl = episodeLink,
                                        ThumbnailUrl = thumbnailUrl,
                                        IsPremiumOnly = isPremiumOnly,
                                        IsPremiere = isPremiere,
                                        SeasonName = seasonName,
                                        EpisodeNumber = episodeNumber
                                    };

                                    calDay.CalendarEpisodes.Add(calEpisode);
                                }
                            }
                        }

                        week.CalendarDays.Add(calDay);
                    }
                }
            }

            return week;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse calendar HTML");
            return null;
        }
    }

    public async Task<CalendarWeek> GetCustomCalendarAsync(DateTime targetDate, string language, bool forceUpdate = false)
    {
        var cacheKey = $"C_{language}_{targetDate:yyyy-MM-dd}";

        if (!forceUpdate && _calendarCache.TryGetValue(cacheKey, out var cachedWeek))
        {
            return cachedWeek;
        }

        var week = new CalendarWeek
        {
            CalendarDays = new List<CalendarDay>()
        };

        for (int i = 0; i < 7; i++)
        {
            var calDay = new CalendarDay
            {
                CalendarEpisodes = new List<CalendarEpisode>(),
                DateTime = targetDate.AddDays(-i),
                DayName = targetDate.AddDays(-i).DayOfWeek.ToString()
            };
            week.CalendarDays.Add(calDay);
        }

        week.CalendarDays.Reverse();
        week.FirstDayOfWeek = week.CalendarDays.First().DateTime;

        // Get new episodes from API
        var newEpisodes = await GetNewEpisodesFromApiAsync(language);

        if (newEpisodes != null && newEpisodes.Count > 0)
        {
            foreach (var episode in newEpisodes)
            {
                var targetDay = week.CalendarDays.FirstOrDefault(d => d.DateTime.Date == episode.DateTime.Date);
                if (targetDay != null)
                {
                    // Check for existing episode with same series and locale
                    var existingEpisode = targetDay.CalendarEpisodes
                        .FirstOrDefault(e => e.CrSeriesID == episode.CrSeriesID && e.AudioLocale == episode.AudioLocale);

                    if (existingEpisode != null)
                    {
                        // Merge episode numbers
                        if (!int.TryParse(existingEpisode.EpisodeNumber, out _))
                        {
                            existingEpisode.EpisodeNumber = "...";
                        }
                        else
                        {
                            var existingNumbers = existingEpisode.EpisodeNumber
                                .Split('-')
                                .Select(n => int.TryParse(n, out var num) ? num : 0)
                                .Where(n => n > 0)
                                .ToList();

                            if (int.TryParse(episode.EpisodeNumber, out var newEpisodeNumber))
                            {
                                existingNumbers.Add(newEpisodeNumber);
                            }

                            existingNumbers.Sort();
                            var lowest = existingNumbers.First();
                            var highest = existingNumbers.Last();

                            existingEpisode.EpisodeNumber = lowest == highest
                                ? lowest.ToString()
                                : $"{lowest}-{highest}";

                            if (lowest == 1)
                            {
                                existingEpisode.IsPremiere = true;
                            }
                        }

                        existingEpisode.CalendarEpisodes.Add(episode);
                    }
                    else
                    {
                        targetDay.CalendarEpisodes.Add(episode);
                    }
                }
            }
        }

        // Sort episodes
        foreach (var day in week.CalendarDays)
        {
            if (day.CalendarEpisodes.Count > 0)
            {
                day.CalendarEpisodes = day.CalendarEpisodes
                    .Where(e => !e.FilteredOut)
                    .OrderBy(e => e.AnilistEpisode) // False first, then true
                    .ThenBy(e => e.DateTime)
                    .ThenBy(e => e.SeasonName)
                    .ThenBy(e =>
                    {
                        double parsedNumber;
                        return double.TryParse(e.EpisodeNumber, out parsedNumber) ? parsedNumber : double.MinValue;
                    })
                    .ToList();
            }
        }

        _calendarCache[cacheKey] = week;
        return week;
    }

    private async Task<List<CalendarEpisode>> GetNewEpisodesFromApiAsync(string language)
    {
        try
        {
            _logger?.LogInformation("Fetching new episodes from API for calendar");

            var newEpisodesBase = await _apiService.GetNewEpisodesAsync(language, 2000, true);

            if (newEpisodesBase?.Data == null || newEpisodesBase.Data.Count == 0)
            {
                return new List<CalendarEpisode>();
            }

            var calendarEpisodes = new List<CalendarEpisode>();

            foreach (var crBrowseEpisode in newEpisodesBase.Data)
            {
                var metadata = crBrowseEpisode.EpisodeMetadata;

                // Determine target date for calendar placement
                DateTime episodeAirDate = metadata.EpisodeAirDate.Kind == DateTimeKind.Utc
                    ? metadata.EpisodeAirDate.ToLocalTime()
                    : metadata.EpisodeAirDate;

                DateTime premiumAvailableStart = metadata.PremiumAvailableDate.Kind == DateTimeKind.Utc
                    ? metadata.PremiumAvailableDate.ToLocalTime()
                    : metadata.PremiumAvailableDate;

                DateTime targetDate = premiumAvailableStart;
                DateTime oneYearFromNow = DateTime.Now.AddYears(1);

                if (targetDate >= oneYearFromNow)
                {
                    DateTime freeAvailableStart = metadata.FreeAvailableDate.Kind == DateTimeKind.Utc
                        ? metadata.FreeAvailableDate.ToLocalTime()
                        : metadata.FreeAvailableDate;

                    if (freeAvailableStart <= oneYearFromNow)
                    {
                        targetDate = freeAvailableStart;
                    }
                    else
                    {
                        targetDate = episodeAirDate;
                    }
                }

                // Build season title
                string? seasonTitle = string.IsNullOrEmpty(metadata.SeasonTitle)
                    ? metadata.SeriesTitle
                    : LooksLikeGenericSeasonLabel(metadata.SeasonTitle, metadata.SeasonNumber)
                        ? $"{metadata.SeriesTitle} {metadata.SeasonTitle}"
                        : metadata.SeasonTitle;

                var calEpisode = new CalendarEpisode
                {
                    DateTime = targetDate,
                    HasPassed = DateTime.Now > targetDate,
                    EpisodeName = crBrowseEpisode.Title,
                    SeriesUrl = $"https://www.crunchyroll.com/{language}/series/" + metadata.SeriesId,
                    EpisodeUrl = $"https://www.crunchyroll.com/{language}/watch/{crBrowseEpisode.Id}/",
                    ThumbnailUrl = crBrowseEpisode.Images?.Thumbnail?.FirstOrDefault()?.FirstOrDefault()?.Source ?? "",
                    IsPremiumOnly = metadata.IsPremiumOnly,
                    IsPremiere = metadata.Episode == "1",
                    SeasonName = seasonTitle,
                    EpisodeNumber = metadata.Episode ?? "?",
                    CrSeriesID = metadata.SeriesId,
                    AudioLocale = metadata.AudioLocale
                };

                calendarEpisodes.Add(calEpisode);
            }

            return calendarEpisodes;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch new episodes for calendar");
            return new List<CalendarEpisode>();
        }
    }

    private static bool LooksLikeGenericSeasonLabel(string? seasonTitle, double seasonNumber)
    {
        if (string.IsNullOrEmpty(seasonTitle)) return false;
        var genericLabels = new[] { "Season", "Cour" };
        return genericLabels.Any(label => seasonTitle.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<CalendarEpisode>> GetUpcomingEpisodesAsync(string language)
    {
        try
        {
            await LoadAnilistUpcomingAsync(language);

            var upcoming = new List<CalendarEpisode>();
            var today = DateTime.Today.ToString("yyyy-MM-dd");

            if (_anilistCache.TryGetValue(today, out var cached))
            {
                upcoming.AddRange(cached.Where(e => e.DateTime.Date >= DateTime.Today));
            }

            return upcoming.OrderBy(e => e.DateTime).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get upcoming episodes");
            return new List<CalendarEpisode>();
        }
    }

    private async Task LoadAnilistUpcomingAsync(string language)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        if (_anilistCache.ContainsKey(today))
        {
            return;
        }

        var todayMidnight = new DateTimeOffset(DateTime.Now.Date, TimeSpan.Zero);
        var todayMidnightUnix = todayMidnight.ToUnixTimeSeconds();
        var sevenDaysLaterUnix = todayMidnight.AddDays(8).ToUnixTimeSeconds();

        var query = @"query ($weekStart: Int, $weekEnd: Int, $page: Int) {
  Page(page: $page) {
    pageInfo {
      hasNextPage
      total
    }
    airingSchedules(
      airingAt_greater: $weekStart
      airingAt_lesser: $weekEnd
    ) {
      id
      episode
      airingAt
      media {
        id
        title {
          romaji
          native
          english
        }
        coverImage {
          extraLarge
          color
        }
        externalLinks {
          site
          url
        }
      }
    }
  }
}";

        var allSchedules = new List<AniListAiringSchedule>();
        int currentPage = 1;
        bool hasNextPage;

        do
        {
            var variables = new
            {
                weekStart = todayMidnightUnix,
                weekEnd = sevenDaysLaterUnix,
                page = currentPage
            };

            var payload = new { query, variables };
            var jsonPayload = JsonConvert.SerializeObject(payload, Formatting.Indented);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

            if (!isOk)
            {
                _logger?.LogError("AniList request failed: {Error}", error);
                return;
            }

            var response = JsonConvert.DeserializeObject<AniListResponseCalendar>(content);
            var schedules = response?.Data?.Page?.AiringSchedules ?? new List<AniListAiringSchedule>();

            allSchedules.AddRange(schedules);
            hasNextPage = response?.Data?.Page?.PageInfo?.HasNextPage ?? false;
            currentPage++;
        } while (hasNextPage && currentPage < 20);

        // Filter for Crunchyroll content
        var crunchyrollSchedules = allSchedules
            .Where(s => s.Media?.ExternalLinks != null &&
                       s.Media.ExternalLinks.Any(e => string.Equals(e.Site, "Crunchyroll", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var calendarEpisodes = new List<CalendarEpisode>();

        foreach (var schedule in crunchyrollSchedules)
        {
            var calEp = new CalendarEpisode
            {
                DateTime = DateTimeOffset.FromUnixTimeSeconds(schedule.AiringAt).UtcDateTime.ToLocalTime(),
                HasPassed = false,
                EpisodeName = schedule.Media?.Title?.English ?? schedule.Media?.Title?.Romaji,
                SeriesUrl = $"https://www.crunchyroll.com/{language}/series/",
                EpisodeUrl = $"https://www.crunchyroll.com/{language}/watch/",
                ThumbnailUrl = schedule.Media?.CoverImage?.ExtraLarge ?? "",
                IsPremiumOnly = true,
                IsPremiere = schedule.Episode == 1,
                SeasonName = schedule.Media?.Title?.English ?? schedule.Media?.Title?.Romaji,
                EpisodeNumber = schedule.Episode.ToString(),
                AnilistEpisode = true
            };

            // Extract Crunchyroll series ID from external links
            if (schedule.Media?.ExternalLinks != null)
            {
                var crLink = schedule.Media.ExternalLinks.FirstOrDefault(e =>
                    string.Equals(e.Site, "Crunchyroll", StringComparison.OrdinalIgnoreCase));

                if (crLink?.Url != null)
                {
                    var match = Regex.Match(crLink.Url, @"series/([^/]+)");
                    if (match.Success)
                    {
                        calEp.CrSeriesID = match.Groups[1].Value;
                        calEp.SeriesUrl += calEp.CrSeriesID;
                    }
                }
            }

            calendarEpisodes.Add(calEp);
        }

        // Group by date
        foreach (var episode in calendarEpisodes)
        {
            var airDate = episode.DateTime.ToString("yyyy-MM-dd");
            if (!_anilistCache.TryGetValue(airDate, out var value))
            {
                value = new List<CalendarEpisode>();
                _anilistCache[airDate] = value;
            }
            value.Add(episode);
        }
    }
}
