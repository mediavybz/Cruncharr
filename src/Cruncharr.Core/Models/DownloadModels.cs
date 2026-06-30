using Newtonsoft.Json;

namespace Cruncharr.Core.Models;

public class DownloadProgress
{
    public DownloadState State { get; set; } = DownloadState.Queued;
    public DownloadState ResumeState { get; set; } = DownloadState.Downloading;
    public double Percent { get; set; }
    public string Doing { get; set; } = "";
    public long DownloadSpeedBytes { get; set; }
    public double Time { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public int RetryAttemptCount { get; set; }

    public bool IsQueued => State == DownloadState.Queued;
    public bool IsDownloading => State == DownloadState.Downloading;
    public bool IsPaused => State == DownloadState.Paused;
    public bool IsProcessing => State == DownloadState.Processing;
    public bool IsDone => State == DownloadState.Done;
    public bool IsError => State == DownloadState.Error;
    public bool IsFinished => State is DownloadState.Done or DownloadState.Error;
    public bool IsRunnable => State is DownloadState.Queued or DownloadState.Error;
    public bool IsWaitingForRetry => RetryAtUtc.HasValue && RetryAtUtc.Value > DateTimeOffset.UtcNow;

    public void ResetForRetry()
    {
        State = DownloadState.Queued;
        ResumeState = DownloadState.Downloading;
        Percent = 0;
        DownloadSpeedBytes = 0;
        Time = 0;
        Doing = "";
        RetryAtUtc = null;
        RetryAttemptCount = 0;
    }

    public void ScheduleRetry(TimeSpan delay, string doing)
    {
        State = DownloadState.Queued;
        ResumeState = DownloadState.Downloading;
        Percent = 0;
        DownloadSpeedBytes = 0;
        Time = 0;
        Doing = doing;
        RetryAtUtc = DateTimeOffset.UtcNow.Add(delay);
        RetryAttemptCount++;
    }

    public void ClearRetryState()
    {
        RetryAtUtc = null;
        RetryAttemptCount = 0;
    }
}

public enum DownloadState
{
    Queued,
    Downloading,
    Processing,
    Done,
    Error,
    Paused,
    Cancelled
}

public class QueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public EpisodeInfo Episode { get; set; } = new();
    public DownloadProgress DownloadProgress { get; set; } = new();
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EpisodeInfo
{
    public string Id { get; set; } = "";
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    public string? SeriesId { get; set; }
    public string? SeasonTitle { get; set; }
    public string? SeasonId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CoverArtUrl { get; set; }
    public List<string> Images { get; set; } = new();
    public Dictionary<string, List<List<object>>>? RawImages { get; set; }
    public string Locale { get; set; } = "ja-JP";
    public bool IsPremium { get; set; }
    public bool IsDubbed { get; set; }
    public bool IsSubbed { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public List<EpisodeVersion>? Versions { get; set; }
    public string AudioLocale { get; set; } = "ja-JP";
    public List<string> SubtitleLocales { get; set; } = new();
    public string? Identifier { get; set; }
    public string? Episode { get; set; }
    public string? Playback { get; set; }
    public string? StreamsLink { get; set; }
    public int DurationMs { get; set; }
    public bool? HideSeasonTitle { get; set; }
    public bool? HideSeasonNumber { get; set; }
    public string? SeqId { get; set; }
    public List<string>? SelectedDubs { get; set; }
    public List<string>? SelectedSubs { get; set; }
}

public class SeriesInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<string> Images { get; set; } = new();
    public List<SeasonInfo> Seasons { get; set; } = new();
    // Dub/audio languages this series offers (CR browse series_metadata.audio_locales).
    public List<string> AudioLocales { get; set; } = new();
    // Maturity ratings this series carries (CR browse series_metadata.maturity_ratings),
    // e.g. ["TV-14"], ["TV-MA"]. Region-specific codes; used by the Browse rating filter.
    public List<string> MaturityRatings { get; set; } = new();
    // Seasonal-browse extras (AniList-backed lineup).
    public int? EpisodeCount { get; set; }
    public bool OnCrunchyroll { get; set; }
    public string? StartDate { get; set; }        // ISO yyyy-MM-dd (AniList season start)
    public int? NextEpisodeNumber { get; set; }   // next un-aired episode #
    public DateTime? NextAirUtc { get; set; }      // when that episode airs (UTC)
}

public class SeasonInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int SeasonNumber { get; set; }
    public string? Identifier { get; set; }
    public List<EpisodeInfo> Episodes { get; set; } = new();
}

public class DownloadHistory
{
    public string EpisodeId { get; set; } = "";
    public string SeriesId { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    public string EpisodeTitle { get; set; } = "";
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string AudioLanguage { get; set; } = "";
    public List<string> SubtitleLanguages { get; set; } = new();
    public DateTime DownloadedAt { get; set; }
    public string OutputPath { get; set; } = "";
    public long FileSizeBytes { get; set; }
}

public class EpisodeVersion
{
    public string AudioLocale { get; set; } = "";
    public string Guid { get; set; } = "";
    [JsonProperty("media_guid")]
    public string? MediaGuid { get; set; }
    public bool Original { get; set; }
    public string SeasonGuid { get; set; } = "";
    public List<string>? Roles { get; set; }
}

public class CrBrowseEpisodeBase
{
    public int Total { get; set; }
    public List<CrBrowseEpisode>? Data { get; set; }
    public Meta? Meta { get; set; }
}

public class CrBrowseEpisode
{
    [JsonProperty("external_id")]
    public string? ExternalId { get; set; }
    [JsonProperty("last_public")]
    public DateTime LastPublic { get; set; }
    public string? Description { get; set; }
    public bool New { get; set; }
    [JsonProperty("linked_resource_key")]
    public string? LinkedResourceKey { get; set; }
    [JsonProperty("slug_title")]
    public string? SlugTitle { get; set; }
    public string? Title { get; set; }
    [JsonProperty("promo_title")]
    public string? PromoTitle { get; set; }
    [JsonProperty("episode_metadata")]
    public CrBrowseEpisodeMetaData EpisodeMetadata { get; set; } = new();
    public string? Id { get; set; }
    public Images? Images { get; set; }
    [JsonProperty("promo_description")]
    public string? PromoDescription { get; set; }
    public string? Slug { get; set; }
    public string? Type { get; set; }
    [JsonProperty("channel_id")]
    public string? ChannelId { get; set; }
    [JsonProperty("streams_link")]
    public string? StreamsLink { get; set; }
}

public class CrBrowseEpisodeMetaData
{
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    [JsonProperty("content_descriptors")]
    public List<string>? ContentDescriptors { get; set; }
    [JsonProperty("availability_notes")]
    public string? AvailabilityNotes { get; set; }
    public string? Episode { get; set; }
    [JsonProperty("episode_air_date")]
    public DateTime EpisodeAirDate { get; set; }
    // CR sends episode_number = null for specials/movies/recaps. Ignore the null so the whole
    // browse page doesn't fail to deserialize (was emptying the entire calendar).
    [JsonProperty("episode_number", NullValueHandling = NullValueHandling.Ignore)]
    public int EpisodeCount { get; set; }
    [JsonProperty("duration_ms")]
    public int DurationMs { get; set; }
    [JsonProperty("extended_maturity_rating")]
    public Dictionary<object, object>? ExtendedMaturityRating { get; set; }
    [JsonProperty("is_dubbed")]
    public bool IsDubbed { get; set; }
    [JsonProperty("is_mature")]
    public bool IsMature { get; set; }
    [JsonProperty("is_subbed")]
    public bool IsSubbed { get; set; }
    [JsonProperty("mature_blocked")]
    public bool MatureBlocked { get; set; }
    [JsonProperty("is_premium_only")]
    public bool IsPremiumOnly { get; set; }
    [JsonProperty("is_clip")]
    public bool IsClip { get; set; }
    [JsonProperty("maturity_ratings")]
    public List<string>? MaturityRatings { get; set; }
    [JsonProperty("season_number")]
    public double SeasonNumber { get; set; }
    [JsonProperty("season_sequence_number")]
    public double SeasonSequenceNumber { get; set; }
    [JsonProperty("sequence_number")]
    public double SequenceNumber { get; set; }
    [JsonProperty("upload_date")]
    public DateTime UploadDate { get; set; }
    [JsonProperty("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }
    [JsonProperty("premium_available_date")]
    public DateTime PremiumAvailableDate { get; set; }
    [JsonProperty("availability_ends")]
    public DateTime AvailabilityEnds { get; set; }
    [JsonProperty("availability_starts")]
    public DateTime AvailabilityStarts { get; set; }
    [JsonProperty("free_available_date")]
    public DateTime FreeAvailableDate { get; set; }
    [JsonProperty("identifier")]
    public string? Identifier { get; set; }
    [JsonProperty("season_id")]
    public string? SeasonId { get; set; }
    [JsonProperty("series_id")]
    public string? SeriesId { get; set; }
    [JsonProperty("season_display_number")]
    public string? SeasonDisplayNumber { get; set; }
    [JsonProperty("eligible_region")]
    public string? EligibleRegion { get; set; }
    [JsonProperty("available_date")]
    public DateTime AvailableDate { get; set; }
    [JsonProperty("premium_date")]
    public DateTime PremiumDate { get; set; }
    [JsonProperty("available_offline")]
    public bool AvailableOffline { get; set; }
    [JsonProperty("closed_captions_available")]
    public bool ClosedCaptionsAvailable { get; set; }
    [JsonProperty("season_slug_title")]
    public string? SeasonSlugTitle { get; set; }
    [JsonProperty("season_title")]
    public string? SeasonTitle { get; set; }
    [JsonProperty("series_slug_title")]
    public string? SeriesSlugTitle { get; set; }
    [JsonProperty("series_title")]
    public string? SeriesTitle { get; set; }
    [JsonProperty("versions")]
    public List<CrBrowseEpisodeVersion>? Versions { get; set; }
}

public class CrBrowseEpisodeVersion
{
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    public string? Guid { get; set; }
    public bool Original { get; set; }
    public string? Variant { get; set; }
    [JsonProperty("season_guid")]
    public string? SeasonGuid { get; set; }
    [JsonProperty("media_guid")]
    public string? MediaGuid { get; set; }
}

public class Meta
{
    public int TotalBeforeFilter { get; set; }
    public int TotalAfterFilter { get; set; }
}

public class Images
{
    public List<List<Thumbnail>>? Thumbnail { get; set; }
}

public class Thumbnail
{
    public string? Source { get; set; }
}

public class DownloadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? OutputPath { get; set; }
    public EpisodeInfo? Episode { get; set; }
    public DownloadErrorType? ErrorType { get; set; }
}

public enum DownloadErrorType
{
    Unknown,
    NotAuthenticated,
    SubscriptionExpired,
    PremiumContent,
    TooManyActiveStreams,
    MaturityRating,
    RateLimited,
    NetworkError,
    ParseError
}

public class DownloadException : Exception
{
    public DownloadErrorType ErrorType { get; }

    public DownloadException(string message, DownloadErrorType errorType) : base(message)
    {
        ErrorType = errorType;
    }
}
