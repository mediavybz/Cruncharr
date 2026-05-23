namespace Cruncharr.Core.Models;

public class DownloadProgress{
    public DownloadState State { get; set; } = DownloadState.Queued;
    public DownloadState ResumeState { get; set; } = DownloadState.Downloading;
    public double Percent { get; set; }
    public string Doing { get; set; } = "";
    public long DownloadSpeedBytes { get; set; }
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

    public void ResetForRetry(){
        State = DownloadState.Queued;
        ResumeState = DownloadState.Downloading;
        Percent = 0;
        DownloadSpeedBytes = 0;
        Doing = "";
        RetryAtUtc = null;
        RetryAttemptCount = 0;
    }

    public void ScheduleRetry(TimeSpan delay, string doing){
        State = DownloadState.Queued;
        ResumeState = DownloadState.Downloading;
        Percent = 0;
        DownloadSpeedBytes = 0;
        Doing = doing;
        RetryAtUtc = DateTimeOffset.UtcNow.Add(delay);
        RetryAttemptCount++;
    }

    public void ClearRetryState(){
        RetryAtUtc = null;
        RetryAttemptCount = 0;
    }
}

public enum DownloadState{
    Queued,
    Downloading,
    Processing,
    Done,
    Error,
    Paused,
    Cancelled
}

public class QueueItem{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public EpisodeInfo Episode { get; set; } = new();
    public DownloadProgress DownloadProgress { get; set; } = new();
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EpisodeInfo{
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
    public string Locale { get; set; } = "ja-JP";
    public bool IsPremium { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public List<EpisodeVersion>? Versions { get; set; }
    public string? AudioLocale { get; set; }
    public List<string> SubtitleLocales { get; set; } = new();
}

public class SeriesInfo{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<string> Images { get; set; } = new();
    public List<SeasonInfo> Seasons { get; set; } = new();
}

public class SeasonInfo{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int SeasonNumber { get; set; }
    public List<EpisodeInfo> Episodes { get; set; } = new();
}

public class DownloadHistory{
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

public class EpisodeVersion{
    public string AudioLocale{ get; set; } = "";
    public string Guid{ get; set; } = "";
    public bool Original{ get; set; }
    public string SeasonGuid{ get; set; } = "";
}

public class DownloadResult{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? OutputPath { get; set; }
    public EpisodeInfo? Episode { get; set; }
}
