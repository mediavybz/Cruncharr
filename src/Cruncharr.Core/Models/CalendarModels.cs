using System;
using System.Collections.Generic;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Models;

// [PT] Ported from upstream CalendarStructs.CalendarHistoryDownloadState
public enum CalendarHistoryDownloadState
{
    None,
    NotDownloaded,
    PartlyDownloaded,
    Downloaded
}

public class CalendarWeek
{
    public DateTime FirstDayOfWeek { get; set; }
    public string? FirstDayOfWeekString { get; set; }
    public List<CalendarDay>? CalendarDays { get; set; }
}

public class CalendarDay
{
    public DateTime DateTime { get; set; }
    public string? DayName { get; set; }
    public List<CalendarEpisode> CalendarEpisodes { get; set; } = [];
}

public class CalendarEpisode
{
    public DateTime DateTime { get; set; }
    public bool? HasPassed { get; set; }
    public string? EpisodeName { get; set; }
    public string? SeriesUrl { get; set; }
    public string? EpisodeUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? EpisodeNumber { get; set; }
    public bool IsPremiumOnly { get; set; }
    public bool IsPremiere { get; set; }
    public string? SeasonName { get; set; }
    public string? CrSeriesID { get; set; }
    public string? CrSeasonID { get; set; }
    public string? CrEpisodeID { get; set; }
    public bool AnilistEpisode { get; set; }
    public bool FilteredOut { get; set; }
    public string? AudioLocale { get; set; }

    // [PT] Upstream calendar history marks
    public List<Services.CrBrowseEpisodeVersion>? Versions { get; set; }
    public string? OriginalEpisodeGuid { get; set; }
    public string? OriginalSeasonGuid { get; set; }
    public List<string> VersionGuids { get; set; } = [];
    public bool IsInHistory { get; set; }
    public bool ShowHistoryMark { get; set; } = true;
    public CalendarHistoryDownloadState HistoryDownloadState { get; set; }

    public List<CalendarEpisode> CalendarEpisodes { get; set; } = [];
}

public class AniListResponseCalendar
{
    public AniListData2? Data { get; set; }
}

public class AniListData2
{
    public AniListPage2? Page { get; set; }
}

public class AniListPage2
{
    public AniListPageInfo? PageInfo { get; set; }
    public List<AniListAiringSchedule>? AiringSchedules { get; set; }
}

public class AniListPageInfo
{
    public bool HasNextPage { get; set; }
    public int Total { get; set; }
}

public class AniListAiringSchedule
{
    public int Id { get; set; }
    public int Episode { get; set; }
    public int AiringAt { get; set; }
    public AniListMedia? Media { get; set; }
}

public class AniListMedia
{
    public int Id { get; set; }
    public AniListTitle? Title { get; set; }
    public string? BannerImage { get; set; }
    public AniListCoverImage? CoverImage { get; set; }
    public List<AniListExternalLink>? ExternalLinks { get; set; }
}

public class AniListTitle
{
    public string? Romaji { get; set; }
    public string? Native { get; set; }
    public string? English { get; set; }
}

public class AniListCoverImage
{
    public string? ExtraLarge { get; set; }
    public string? Color { get; set; }
}

public class AniListExternalLink
{
    public string? Site { get; set; }
    public string? Url { get; set; }
}
