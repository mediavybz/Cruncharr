using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CRD.Downloader.Crunchyroll;
using CRD.Downloader.Crunchyroll.Utils;
using CRD.Utils;
using CRD.Utils.Files;
using CRD.Utils.Http;
using CRD.Utils.Structs;
using CRD.Utils.Structs.History;
using CRD.Views;
using HtmlAgilityPack;
using Newtonsoft.Json;
using ReactiveUI;

namespace CRD.Downloader;

public class CalendarManager{
    #region Calendar Variables

    private Dictionary<string, CalendarWeek> calendar = new();
    private DateTime? anilistUpcomingLoadedDate;
    private static readonly Regex GenericSeasonLabelRegex = new(
        @"^(?<word>\p{L}+(?:[\p{L}\p{Mn}'\.\- ]*\p{L})?)\s+(?<n>\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private Dictionary<string, string> calendarLanguage = new(){
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

    #endregion


    #region Singelton

    private static CalendarManager? _instance;
    private static readonly object Padlock = new();

    public static CalendarManager Instance{
        get{
            if (_instance == null){
                lock (Padlock){
                    if (_instance == null){
                        _instance = new CalendarManager();
                    }
                }
            }

            return _instance;
        }
    }

    #endregion


    public async Task<CalendarWeek> GetCalendarForDate(string weeksMondayDate, bool forceUpdate){
        if (!forceUpdate && calendar.TryGetValue(weeksMondayDate, out var forDate)){
            RefreshHistoryStatuses(forDate);
            return forDate;
        }

        if (CrunchyrollManager.Instance.CrunOptions.CalendarShowUpcomingEpisodes){
            await LoadAnilistUpcoming();
        }

        var request = calendarLanguage.ContainsKey(CrunchyrollManager.Instance.CrunOptions.SelectedCalendarLanguage ?? "en-us")
            ? HttpClientReq.CreateRequestMessage($"{calendarLanguage[CrunchyrollManager.Instance.CrunOptions.SelectedCalendarLanguage ?? "en-us"]}?filter=premium&date={weeksMondayDate}", HttpMethod.Get, false)
            : HttpClientReq.CreateRequestMessage($"{calendarLanguage["en-us"]}?filter=premium&date={weeksMondayDate}", HttpMethod.Get, false);


        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");

        (bool IsOk, string ResponseContent, string error, Dictionary<string,string> Headers) response;
        if (!HttpClientReq.Instance.UseFlareSolverr){
            response = await HttpClientReq.Instance.SendHttpRequest(request);
        } else{
            response = await HttpClientReq.Instance.SendFlareSolverrHttpRequest(request);
        }


        if (!response.IsOk){
            if (response.ResponseContent.Contains("<title>Just a moment...</title>") ||
                response.ResponseContent.Contains("<title>Access denied</title>") ||
                response.ResponseContent.Contains("<title>Attention Required! | Cloudflare</title>") ||
                response.ResponseContent.Trim().Equals("error code: 1020") ||
                response.ResponseContent.IndexOf("<title>DDOS-GUARD</title>", StringComparison.OrdinalIgnoreCase) > -1){
                MessageBus.Current.SendMessage(new ToastMessage("Blocked by Cloudflare. Use the custom calendar.", ToastType.Error, 5));
                Console.Error.WriteLine($"Blocked by Cloudflare. Use the custom calendar.");
            } else{
                Console.Error.WriteLine($"Calendar request failed");
            }

            return new CalendarWeek();
        }

        CalendarWeek week = new CalendarWeek();
        week.CalendarDays = new List<CalendarDay>();

        // Load the HTML content from a file
        HtmlDocument doc = new HtmlDocument();
        doc.LoadHtml(WebUtility.HtmlDecode(response.ResponseContent));

        // Select each 'li' element with class 'day'
        var dayNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'day')]");

        if (dayNodes != null){
            foreach (var day in dayNodes){
                // Extract the date and day name
                var date = day.SelectSingleNode(".//time[@datetime]")?.GetAttributeValue("datetime", "No date");
                if (date != null){
                    DateTime dayDateTime = DateTime.Parse(date, null, DateTimeStyles.RoundtripKind);

                    if (week.FirstDayOfWeek == DateTime.MinValue){
                        week.FirstDayOfWeek = dayDateTime;
                        week.FirstDayOfWeekString = dayDateTime.ToString("yyyy-MM-dd");
                    }

                    var dayName = day.SelectSingleNode(".//h1[@class='day-name']/time")?.InnerText.Trim();

                    CalendarDay calDay = new CalendarDay();

                    calDay.CalendarEpisodes = new List<CalendarEpisode>();
                    calDay.DayName = dayName;
                    calDay.DateTime = dayDateTime;

                    // Iterate through each episode listed under this day
                    var episodes = day.SelectNodes(".//article[contains(@class, 'release')]");
                    if (episodes != null){
                        foreach (var episode in episodes){
                            var episodeTimeStr = episode.SelectSingleNode(".//time[contains(@class, 'available-time')]")?.GetAttributeValue("datetime", null);
                            if (episodeTimeStr != null){
                                DateTime episodeTime = DateTime.Parse(episodeTimeStr, null, DateTimeStyles.RoundtripKind);
                                var hasPassed = DateTime.Now > episodeTime;

                                var episodeName = episode.SelectSingleNode(".//h1[contains(@class, 'episode-name')]")?.SelectSingleNode(".//cite[@itemprop='name']")?.InnerText.Trim();
                                var seasonLink = episode.SelectSingleNode(".//a[contains(@class, 'js-season-name-link')]")?.GetAttributeValue("href", "No link");
                                var episodeLink = episode.SelectSingleNode(".//a[contains(@class, 'available-episode-link')]")?.GetAttributeValue("href", "No link");
                                var thumbnailUrl = episode.SelectSingleNode(".//img[contains(@class, 'thumbnail')]")?.GetAttributeValue("src", "No image");
                                var isPremiumOnly = episode.SelectSingleNode(".//svg[contains(@class, 'premium-flag')]") != null;
                                var isPremiere = episode.SelectSingleNode(".//div[contains(@class, 'premiere-flag')]") != null;
                                var seasonName = episode.SelectSingleNode(".//a[contains(@class, 'js-season-name-link')]")?.SelectSingleNode(".//cite[@itemprop='name']")?.InnerText.Trim();
                                var episodeNumber = episode.SelectSingleNode(".//meta[contains(@itemprop, 'episodeNumber')]")?.GetAttributeValue("content", "?");

                                CalendarEpisode calEpisode = new CalendarEpisode();

                                calEpisode.DateTime = episodeTime;
                                calEpisode.HasPassed = hasPassed;
                                calEpisode.EpisodeName = episodeName;
                                calEpisode.SeriesUrl = seasonLink;
                                calEpisode.EpisodeUrl = episodeLink;
                                calEpisode.ThumbnailUrl = thumbnailUrl;
                                calEpisode.IsPremiumOnly = isPremiumOnly;
                                calEpisode.IsPremiere = isPremiere;
                                calEpisode.SeasonName = seasonName;
                                calEpisode.EpisodeNumber = episodeNumber;

                                calDay.CalendarEpisodes.Add(calEpisode);
                            }
                        }
                    }

                    week.CalendarDays.Add(calDay);
                }
            }
        } else{
            Console.Error.WriteLine("No days found in the HTML document.");
        }

        if (CrunchyrollManager.Instance.CrunOptions.CalendarShowUpcomingEpisodes){
            foreach (var calendarDay in week.CalendarDays){
                if (calendarDay.DateTime.Date >= DateTime.Now.Date){
                    if (ProgramManager.Instance.AnilistUpcoming.ContainsKey(calendarDay.DateTime.ToString("yyyy-MM-dd"))){
                        var list = ProgramManager.Instance.AnilistUpcoming[calendarDay.DateTime.ToString("yyyy-MM-dd")];

                        foreach (var calendarEpisode in list
                                     .Where(e => calendarDay.DateTime.Date.Day == e.DateTime.Date.Day)
                                     .Where(e => calendarDay.CalendarEpisodes.All(ele =>
                                         ele.CrSeriesID != e.CrSeriesID &&
                                         !CrSimulcastCalendarFilter.IsMatch(ele.SeasonName, e.SeasonName, similarityThreshold: 0.5)))){
                            calendarDay.CalendarEpisodes.Add(calendarEpisode);
                        }
                    }
                }
            }
        }

        calendar[weeksMondayDate] = week;
        RefreshHistoryStatuses(week);


        return week;
    }


    public async Task<CalendarWeek> BuildCustomCalendar(DateTime calTargetDate, bool forceUpdate){
        var crunInstance = CrunchyrollManager.Instance;
        var crunOptions = crunInstance.CrunOptions;
        var calendarKey = "C" + calTargetDate.ToString("yyyy-MM-dd");

        if (!forceUpdate && calendar.TryGetValue(calendarKey, out var forDate)){
            RefreshHistoryStatuses(forDate);
            return forDate;
        }

        CalendarWeek week = new CalendarWeek();
        week.CalendarDays = new List<CalendarDay>();

        DateTime targetDay = calTargetDate;

        for (int i = 0; i < 7; i++){
            CalendarDay calDay = new CalendarDay();

            calDay.CalendarEpisodes = new List<CalendarEpisode>();
            calDay.DateTime = targetDay.AddDays(-i);
            calDay.DayName = calDay.DateTime.DayOfWeek.ToString();

            week.CalendarDays.Add(calDay);
        }

        week.CalendarDays.Reverse();

        var firstDayOfWeek = week.CalendarDays.First().DateTime;
        week.FirstDayOfWeek = firstDayOfWeek;

        var calendarDaysByDate = week.CalendarDays.ToDictionary(day => day.DateTime.Date);
        var episodeMergeIndexByDate = week.CalendarDays.ToDictionary(
            day => day.DateTime.Date,
            _ => new Dictionary<(string? CrSeriesID, Locale? AudioLocale), CalendarEpisode>());

        Task anilistUpcomingTask = crunOptions.CalendarShowUpcomingEpisodes
            ? LoadAnilistUpcoming()
            : Task.CompletedTask;
        Task<CrBrowseEpisodeBase?> newEpisodesTask = crunInstance.CrEpisode.GetNewEpisodes(crunOptions.HistoryLang, 2000, firstDayOfWeek, true);

        await Task.WhenAll(anilistUpcomingTask, newEpisodesTask);

        var newEpisodesBase = await newEpisodesTask;

        if (newEpisodesBase is{ Data.Count: > 0 }){
            var newEpisodes = newEpisodesBase.Data ?? [];

            if (crunOptions.UpdateHistoryFromCalendar){
                QueueHistoryUpdateFromCalendar(newEpisodes);
            }

            DateTime now = DateTime.Now;
            DateTime nowDate = now.Date;
            DateTime oneYearFromNow = now.AddYears(1);
            var dubFilter = crunOptions.CalendarDubFilter;
            bool hasDubFilter = !string.IsNullOrEmpty(dubFilter) && dubFilter != "none";
            string? historyLang = crunOptions.HistoryLang;

            //EpisodeAirDate
            foreach (var crBrowseEpisode in newEpisodes){
                bool filtered = false;
                DateTime episodeAirDate = crBrowseEpisode.EpisodeMetadata.EpisodeAirDate.Kind == DateTimeKind.Utc
                    ? crBrowseEpisode.EpisodeMetadata.EpisodeAirDate.ToLocalTime()
                    : crBrowseEpisode.EpisodeMetadata.EpisodeAirDate;

                DateTime premiumAvailableStart = crBrowseEpisode.EpisodeMetadata.PremiumAvailableDate.Kind == DateTimeKind.Utc
                    ? crBrowseEpisode.EpisodeMetadata.PremiumAvailableDate.ToLocalTime()
                    : crBrowseEpisode.EpisodeMetadata.PremiumAvailableDate;

                DateTime targetDate;


                targetDate = premiumAvailableStart;

                if (targetDate >= oneYearFromNow){
                    DateTime freeAvailableStart = crBrowseEpisode.EpisodeMetadata.FreeAvailableDate.Kind == DateTimeKind.Utc
                        ? crBrowseEpisode.EpisodeMetadata.FreeAvailableDate.ToLocalTime()
                        : crBrowseEpisode.EpisodeMetadata.FreeAvailableDate;

                    if (freeAvailableStart <= oneYearFromNow){
                        targetDate = freeAvailableStart;
                    } else{
                        targetDate = episodeAirDate;
                    }
                }


                if (crunOptions.CalendarHideDubs && crBrowseEpisode.EpisodeMetadata.SeasonTitle != null &&
                    (crBrowseEpisode.EpisodeMetadata.SeasonTitle.EndsWith("Dub)") || crBrowseEpisode.EpisodeMetadata.SeasonTitle.EndsWith("Audio)")) &&
                    (!hasDubFilter || (crBrowseEpisode.EpisodeMetadata.AudioLocale != null && crBrowseEpisode.EpisodeMetadata.AudioLocale.GetEnumMemberValue() != dubFilter))){
                    //|| crBrowseEpisode.EpisodeMetadata.AudioLocale != Locale.JaJp
                    filtered = true;
                }


                if (hasDubFilter){
                    if (crBrowseEpisode.EpisodeMetadata.AudioLocale != null && crBrowseEpisode.EpisodeMetadata.AudioLocale.GetEnumMemberValue() != dubFilter){
                        filtered = true;
                    }
                }

                if (calendarDaysByDate.TryGetValue(targetDate.Date, out var calendarDay)){
                    CalendarEpisode calEpisode = new CalendarEpisode();

                    string? seasonTitle = string.IsNullOrEmpty(crBrowseEpisode.EpisodeMetadata.SeasonTitle)
                        ? crBrowseEpisode.EpisodeMetadata.SeriesTitle
                        : LooksLikeGenericSeasonLabel(crBrowseEpisode.EpisodeMetadata.SeasonTitle)
                            ? $"{crBrowseEpisode.EpisodeMetadata.SeriesTitle} {crBrowseEpisode.EpisodeMetadata.SeasonTitle}"
                            : crBrowseEpisode.EpisodeMetadata.SeasonTitle;

                    calEpisode.DateTime = targetDate;
                    calEpisode.HasPassed = now > targetDate;
                    calEpisode.EpisodeName = crBrowseEpisode.Title;
                    calEpisode.SeriesUrl = $"https://www.crunchyroll.com/{historyLang}/series/" + crBrowseEpisode.EpisodeMetadata.SeriesId;
                    calEpisode.EpisodeUrl = $"https://www.crunchyroll.com/{historyLang}/watch/{crBrowseEpisode.Id}/";
                    calEpisode.ThumbnailUrl = crBrowseEpisode.Images.Thumbnail?.FirstOrDefault()?.FirstOrDefault()?.Source ?? ""; //https://www.crunchyroll.com/i/coming_soon_beta_thumb.jpg
                    calEpisode.IsPremiumOnly = crBrowseEpisode.EpisodeMetadata.IsPremiumOnly;
                    calEpisode.IsPremiere = crBrowseEpisode.EpisodeMetadata.Episode == "1";
                    calEpisode.SeasonName = seasonTitle;
                    calEpisode.EpisodeNumber = crBrowseEpisode.EpisodeMetadata.Episode;
                    calEpisode.CrSeriesID = crBrowseEpisode.EpisodeMetadata.SeriesId;
                    calEpisode.CrSeasonID = crBrowseEpisode.EpisodeMetadata.SeasonId;
                    calEpisode.CrEpisodeID = crBrowseEpisode.Id;
                    calEpisode.FilteredOut = filtered;
                    calEpisode.AudioLocale = crBrowseEpisode.EpisodeMetadata.AudioLocale;
                    calEpisode.Versions = crBrowseEpisode.EpisodeMetadata.versions;
                    ExtractVersionGuids(calEpisode);
                    ApplyHistoryStatus(calEpisode);

                    var episodeMergeKey = (calEpisode.CrSeriesID, calEpisode.AudioLocale);
                    var episodeMergeIndex = episodeMergeIndexByDate[calendarDay.DateTime.Date];

                    if (episodeMergeIndex.TryGetValue(episodeMergeKey, out var existingEpisode)){
                        if (!int.TryParse(existingEpisode.EpisodeNumber, out _)){
                            existingEpisode.EpisodeNumber = "...";
                        } else{
                            var existingNumbers = existingEpisode.EpisodeNumber
                                .Split('-')
                                .Select(n => int.TryParse(n, out var num) ? num : 0)
                                .Where(n => n > 0)
                                .ToList();

                            if (int.TryParse(calEpisode.EpisodeNumber, out var newEpisodeNumber)){
                                existingNumbers.Add(newEpisodeNumber);
                            }

                            existingNumbers.Sort();
                            var lowest = existingNumbers.First();
                            var highest = existingNumbers.Last();

                            // Update the existing episode's number to the new range
                            existingEpisode.EpisodeNumber = lowest == highest
                                ? lowest.ToString()
                                : $"{lowest}-{highest}";

                            if (lowest == 1){
                                existingEpisode.IsPremiere = true;
                            }
                        }

                        existingEpisode.CalendarEpisodes.Add(calEpisode);
                        ApplyMergedHistoryStatus(existingEpisode);
                    } else{
                        calendarDay.CalendarEpisodes.Add(calEpisode);
                        episodeMergeIndex[episodeMergeKey] = calEpisode;
                    }
                }
            }

            if (crunOptions.CalendarShowUpcomingEpisodes){
                foreach (var calendarDay in week.CalendarDays){
                    if (calendarDay.DateTime.Date >= nowDate){
                        if (ProgramManager.Instance.AnilistUpcoming.TryGetValue(calendarDay.DateTime.ToString("yyyy-MM-dd"), out var list)){

                            foreach (var calendarEpisode in list.Where(calendarEpisodeAnilist => calendarDay.DateTime.Date.Day == calendarEpisodeAnilist.DateTime.Date.Day)
                                         .Where(calendarEpisodeAnilist =>
                                             calendarDay.CalendarEpisodes.All(ele => ele.CrSeriesID != calendarEpisodeAnilist.CrSeriesID && ele.SeasonName != calendarEpisodeAnilist.SeasonName))){
                                calendarDay.CalendarEpisodes.Add(calendarEpisode);
                            }
                        }
                    }
                }
            }

            foreach (var weekCalendarDay in week.CalendarDays){
                if (weekCalendarDay.CalendarEpisodes.Count > 0)
                    weekCalendarDay.CalendarEpisodes = weekCalendarDay.CalendarEpisodes
                        .Where(e => !e.FilteredOut)
                        .OrderBy(e => e.AnilistEpisode) // False first, then true
                        .ThenBy(e => e.DateTime)
                        .ThenBy(e => e.SeasonName)
                        .ThenBy(e => {
                            double parsedNumber;
                            return double.TryParse(e.EpisodeNumber, out parsedNumber) ? parsedNumber : double.MinValue;
                        })
                        .ToList();
            }
        }


        // foreach (var day in week.CalendarDays){
        //     if (day.CalendarEpisodes != null) day.CalendarEpisodes = day.CalendarEpisodes.OrderBy(e => e.DateTime).ToList();
        // }

        calendar[calendarKey] = week;
        RefreshHistoryStatuses(week);


        return week;
    }

    private static void QueueHistoryUpdateFromCalendar(List<CrBrowseEpisode> newEpisodes){
        Dispatcher.UIThread.Post(async () => {
            try{
                await CrunchyrollManager.Instance.History.UpdateWithEpisode(newEpisodes);
                CfgManager.UpdateHistoryFile();
            } catch (Exception){
                Console.Error.WriteLine("Failed to update History from calendar");
            }
        }, DispatcherPriority.Background);
    }

    private static void ExtractVersionGuids(CalendarEpisode calEpisode){
        calEpisode.VersionGuids = calEpisode.Versions?
            .Select(version => version.Guid)
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var originalVersion = calEpisode.Versions?
            .FirstOrDefault(version => version.Original);

        calEpisode.OriginalEpisodeGuid = originalVersion?.Guid;
        calEpisode.OriginalSeasonGuid = originalVersion?.SeasonGuid;
    }

    private static void RefreshHistoryStatuses(CalendarWeek week){
        if (week.CalendarDays == null){
            return;
        }

        foreach (var calendarEpisode in week.CalendarDays.SelectMany(day => day.CalendarEpisodes)){
            RefreshHistoryStatus(calendarEpisode);
        }
    }

    private static void RefreshHistoryStatus(CalendarEpisode calendarEpisode){
        foreach (var childEpisode in calendarEpisode.CalendarEpisodes){
            ApplyHistoryStatus(childEpisode);
        }

        ApplyHistoryStatus(calendarEpisode);

        if (calendarEpisode.CalendarEpisodes.Count > 0){
            ApplyMergedHistoryStatus(calendarEpisode);
        }
    }

    private static void ApplyHistoryStatus(CalendarEpisode calEpisode){
        calEpisode.ShowHistoryMark = CrunchyrollManager.Instance.CrunOptions.CalendarShowHistoryMark;
        calEpisode.HistoryDownloadState = CalendarHistoryDownloadState.None;
        calEpisode.IsInHistory = false;

        if (!CrunchyrollManager.Instance.CrunOptions.History || string.IsNullOrWhiteSpace(calEpisode.CrSeriesID)){
            return;
        }

        var historySeries = CrunchyrollManager.Instance.HistoryList
            .FirstOrDefault(series => string.Equals(series.SeriesId, calEpisode.CrSeriesID, StringComparison.OrdinalIgnoreCase));

        if (historySeries == null){
            return;
        }

        calEpisode.IsInHistory = true;

        var historyMatch = FindHistoryMatch(historySeries, calEpisode);
        if (historyMatch.HistoryEpisode == null){
            calEpisode.HistoryDownloadState = CalendarHistoryDownloadState.NotDownloaded;
            return;
        }

        if (!historyMatch.HistoryEpisode.WasDownloaded){
            calEpisode.HistoryDownloadState = CalendarHistoryDownloadState.NotDownloaded;
            return;
        }

        if (CrunchyrollManager.Instance.CrunOptions.HistoryCheckPartialDownloads){
            var requestedDubs = HistorySeries.GetEffectiveDubLang(historySeries, historyMatch.HistorySeason);
            var requestedSoftSubs = HistorySeries.GetEffectiveSoftSubs(historySeries, historyMatch.HistorySeason, historyMatch.HistoryEpisode);
            calEpisode.HistoryDownloadState = historyMatch.HistoryEpisode.IsPartiallyDownloaded(requestedDubs, requestedSoftSubs)
                ? CalendarHistoryDownloadState.PartlyDownloaded
                : CalendarHistoryDownloadState.Downloaded;
        } else{
            calEpisode.HistoryDownloadState = CalendarHistoryDownloadState.Downloaded;
        }
    }

    private static (HistorySeason? HistorySeason, HistoryEpisode? HistoryEpisode) FindHistoryMatch(
        HistorySeries historySeries,
        CalendarEpisode calEpisode){
        var candidateSeasonIds = new[]{
                calEpisode.OriginalSeasonGuid,
                calEpisode.CrSeasonID
            }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidateEpisodeIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(calEpisode.OriginalEpisodeGuid)){
            candidateEpisodeIds.Add(calEpisode.OriginalEpisodeGuid);
        }

        candidateEpisodeIds.AddRange(calEpisode.VersionGuids);

        if (!string.IsNullOrWhiteSpace(calEpisode.CrEpisodeID)){
            candidateEpisodeIds.Add(calEpisode.CrEpisodeID);
        }

        candidateEpisodeIds = candidateEpisodeIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var historySeason in historySeries.Seasons
                     .Where(historySeason => candidateSeasonIds.Count == 0 || candidateSeasonIds.Contains(historySeason.SeasonId ?? string.Empty, StringComparer.OrdinalIgnoreCase))){
            var historyEpisode = historySeason.EpisodesList
                .FirstOrDefault(episode => candidateEpisodeIds.Contains(episode.EpisodeId ?? string.Empty, StringComparer.OrdinalIgnoreCase));

            if (historyEpisode != null){
                return (historySeason, historyEpisode);
            }
        }

        foreach (var historySeason in historySeries.Seasons){
            var historyEpisode = historySeason.EpisodesList
                .FirstOrDefault(episode => candidateEpisodeIds.Contains(episode.EpisodeId ?? string.Empty, StringComparer.OrdinalIgnoreCase));

            if (historyEpisode != null){
                return (historySeason, historyEpisode);
            }
        }

        return (null, null);
    }

    private static void ApplyMergedHistoryStatus(CalendarEpisode calendarEpisode){
        var episodes = new[]{ calendarEpisode }
            .Concat(calendarEpisode.CalendarEpisodes)
            .Where(episode => episode.IsInHistory)
            .ToList();

        calendarEpisode.IsInHistory = episodes.Count > 0;

        if (episodes.Count == 0){
            calendarEpisode.HistoryDownloadState = CalendarHistoryDownloadState.None;
        } else if (episodes.Any(episode => episode.HistoryDownloadState == CalendarHistoryDownloadState.PartlyDownloaded) ||
                   episodes.Select(episode => episode.HistoryDownloadState).Distinct().Count() > 1){
            calendarEpisode.HistoryDownloadState = CalendarHistoryDownloadState.PartlyDownloaded;
        } else{
            calendarEpisode.HistoryDownloadState = episodes[0].HistoryDownloadState;
        }
    }


    private async Task LoadAnilistUpcoming(){
        DateTime today = DateTime.Today;

        string formattedDate = today.ToString("yyyy-MM-dd");

        if (anilistUpcomingLoadedDate == today || ProgramManager.Instance.AnilistUpcoming.ContainsKey(formattedDate)){
            anilistUpcomingLoadedDate = today;
            return;
        }

        DateTimeOffset todayMidnight = DateTimeOffset.Now.Date;

        long todayMidnightUnix = todayMidnight.ToUnixTimeSeconds();
        long sevenDaysLaterUnix = todayMidnight.AddDays(8).ToUnixTimeSeconds();

        AniListResponseCalendar? aniListResponse = null;

        int currentPage = 1; // Start from page 1
        bool hasNextPage;

        do{
            var variables = new{
                weekStart = todayMidnightUnix,
                weekEnd = sevenDaysLaterUnix,
                page = currentPage
            };

            var payload = new{
                query,
                variables
            };

            string jsonPayload = JsonConvert.SerializeObject(payload, Formatting.Indented);

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrls.Anilist){
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            var response = await HttpClientReq.Instance.SendHttpRequest(request);

            if (!response.IsOk){
                Console.Error.WriteLine("Anilist Request Failed for upcoming calendar episodes");
                return;
            }

            AniListResponseCalendar? currentResponse = Helpers.Deserialize<AniListResponseCalendar>(
                response.ResponseContent, CrunchyrollManager.Instance.SettingsJsonSerializerSettings
            );

            if (currentResponse?.Data?.Page == null){
                Console.Error.WriteLine("Anilist response could not be parsed for upcoming calendar episodes");
                return;
            }


            aniListResponse ??= currentResponse;

            if (aniListResponse != currentResponse){
                aniListResponse.Data?.Page?.AiringSchedules?.AddRange(currentResponse.Data?.Page?.AiringSchedules ?? []);
            }

            hasNextPage = currentResponse.Data?.Page?.PageInfo?.HasNextPage ?? false;

            currentPage++;
        } while (hasNextPage && currentPage < 20);


        var list = aniListResponse.Data?.Page?.AiringSchedules ?? [];

        list = list.Where(ele => ele.Media?.ExternalLinks != null && ele.Media.ExternalLinks.Any(external =>
            string.Equals(external.Site, "Crunchyroll", StringComparison.OrdinalIgnoreCase))).ToList();

        List<CalendarEpisode> calendarEpisodes = [];

        foreach (var anilistEle in list){
            var calEp = new CalendarEpisode();

            calEp.DateTime = DateTimeOffset.FromUnixTimeSeconds(anilistEle.AiringAt).UtcDateTime.ToLocalTime();
            calEp.HasPassed = false;
            calEp.EpisodeName = anilistEle.Media?.Title.English;
            calEp.SeriesUrl = $"https://www.crunchyroll.com/{CrunchyrollManager.Instance.CrunOptions.HistoryLang}/series/";
            calEp.EpisodeUrl = $"https://www.crunchyroll.com/{CrunchyrollManager.Instance.CrunOptions.HistoryLang}/watch/";
            calEp.ThumbnailUrl = anilistEle.Media?.CoverImage.ExtraLarge ?? ""; //https://www.crunchyroll.com/i/coming_soon_beta_thumb.jpg
            calEp.IsPremiumOnly = true;
            calEp.IsPremiere = anilistEle.Episode == 1;
            calEp.SeasonName = anilistEle.Media?.Title.English;
            calEp.EpisodeNumber = anilistEle.Episode.ToString();
            calEp.AnilistEpisode = true;

            if (anilistEle.Media?.ExternalLinks != null){
                var url = anilistEle.Media.ExternalLinks.First(external =>
                    string.Equals(external.Site, "Crunchyroll", StringComparison.OrdinalIgnoreCase)).Url;

                string pattern = @"series\/([^\/]+)";

                Match match = Regex.Match(url, pattern);
                string crunchyrollId;
                if (match.Success){
                    crunchyrollId = match.Groups[1].Value;

                    AdjustReleaseTimeToHistory(calEp, crunchyrollId);
                } else{
                    Uri uri = new Uri(url);

                    if (uri.Host == "www.crunchyroll.com"
                        && uri.AbsolutePath != "/"
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)){
                        HttpRequestMessage getUrlRequest = new HttpRequestMessage(HttpMethod.Head, url);

                        string? finalUrl = "";

                        try{
                            HttpResponseMessage getUrlResponse = await HttpClientReq.Instance.GetHttpClient().SendAsync(getUrlRequest);

                            finalUrl = getUrlResponse.RequestMessage?.RequestUri?.ToString();
                        } catch (Exception ex){
                            Console.WriteLine($"Error: {ex.Message}");
                        }

                        Match match2 = Regex.Match(finalUrl ?? string.Empty, pattern);
                        if (match2.Success){
                            crunchyrollId = match2.Groups[1].Value;

                            AdjustReleaseTimeToHistory(calEp, crunchyrollId);
                        }
                    }
                }
            }

            calendarEpisodes.Add(calEp);
        }

        foreach (var calendarEpisode in calendarEpisodes){
            var airDate = calendarEpisode.DateTime.ToString("yyyy-MM-dd");

            if (!ProgramManager.Instance.AnilistUpcoming.TryGetValue(airDate, out var value)){
                value = new List<CalendarEpisode>();
                ProgramManager.Instance.AnilistUpcoming[airDate] = value;
            }

            value.Add(calendarEpisode);
        }

        anilistUpcomingLoadedDate = today;
    }

    private static void AdjustReleaseTimeToHistory(CalendarEpisode calEp, string crunchyrollId){
        calEp.CrSeriesID = crunchyrollId;

        if (CrunchyrollManager.Instance.CrunOptions.History){
            var historySeries = CrunchyrollManager.Instance.HistoryList.FirstOrDefault(item => item.SeriesId == crunchyrollId);

            if (historySeries != null){
                var oldestRelease = DateTime.MinValue;
                foreach (var historySeriesSeason in historySeries.Seasons){
                    if (historySeriesSeason.EpisodesList.Any()){
                        var releaseDate = historySeriesSeason.EpisodesList.Last().EpisodeCrPremiumAirDate;

                        if (releaseDate.HasValue && oldestRelease < releaseDate.Value){
                            oldestRelease = releaseDate.Value;
                        }
                    }
                }

                if (oldestRelease != DateTime.MinValue){
                    var adjustedDate = new DateTime(
                        calEp.DateTime.Year,
                        calEp.DateTime.Month,
                        calEp.DateTime.Day,
                        oldestRelease.Hour,
                        oldestRelease.Minute,
                        oldestRelease.Second,
                        calEp.DateTime.Kind
                    );

                    if ((adjustedDate - oldestRelease).TotalDays is < 6 and > 1){
                        adjustedDate = oldestRelease.AddDays(7);
                    }

                    calEp.DateTime = adjustedDate;
                }
            }
        }
    }

    private bool LooksLikeGenericSeasonLabel(string? seasonTitle){
        if (string.IsNullOrWhiteSpace(seasonTitle))
            return true;

        var t = seasonTitle.Trim();

        var m = GenericSeasonLabelRegex.Match(t);

        if (!m.Success)
            return false;

        var word = m.Groups["word"].Value.Trim();

        return word.Equals("Season", StringComparison.OrdinalIgnoreCase);
    }

    #region Query

    private string query = @"query ($weekStart: Int, $weekEnd: Int, $page: Int) {
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
        idMal
        title {
          romaji
          native
          english
        }
        startDate {
          year
          month
          day
        }
        endDate {
          year
          month
          day
        }
        status
        season
        format
        synonyms
        episodes
        description
        bannerImage
        isAdult
        coverImage {
          extraLarge
          color
        }
        trailer {
          id
          site
          thumbnail
        }
        externalLinks {
          site
          icon
          color
          url
        }
        relations {
          edges {
            relationType(version: 2)
            node {
              id
              title {
                romaji
                native
                english
              }
              siteUrl
            }
          }
        }
      }
    }
  }
}";

    #endregion
}
