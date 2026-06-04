using System.Text.Json.Serialization;
using System.Linq;
using Cruncharr.Core.Models;
using YamlDotNet.Serialization;

namespace Cruncharr.Core.Configuration;

public class CruncharrConfig
{
    [JsonPropertyName("crunchyroll")]
    [YamlMember(Alias = "crunchyroll", ApplyNamingConventions = false)]
    public CrunchyrollConfig Crunchyroll { get; set; } = new();

    [JsonPropertyName("download")]
    [YamlMember(Alias = "download", ApplyNamingConventions = false)]
    public DownloadConfig Download { get; set; } = new();

    [JsonPropertyName("history")]
    [YamlMember(Alias = "history", ApplyNamingConventions = false)]
    public HistoryConfig History { get; set; } = new();

    [JsonPropertyName("history_page_properties")]
    [YamlMember(Alias = "history_page_properties", ApplyNamingConventions = false)]
    public HistoryPageProperties HistoryPageProperties { get; set; } = new();

    [JsonPropertyName("seasons_page_properties")]
    [YamlMember(Alias = "seasons_page_properties", ApplyNamingConventions = false)]
    public SeasonsPageProperties SeasonsPageProperties { get; set; } = new();

    [JsonPropertyName("queue")]
    [YamlMember(Alias = "queue", ApplyNamingConventions = false)]
    public QueueConfig Queue { get; set; } = new();

    [JsonPropertyName("notifications")]
    [YamlMember(Alias = "notifications", ApplyNamingConventions = false)]
    public NotificationsConfig Notifications { get; set; } = new();

    [JsonPropertyName("sonarr")]
    [YamlMember(Alias = "sonarr", ApplyNamingConventions = false)]
    public SonarrConfig Sonarr { get; set; } = new();

    [JsonPropertyName("proxy")]
    [YamlMember(Alias = "proxy", ApplyNamingConventions = false)]
    public ProxyConfig Proxy { get; set; } = new();

    [JsonPropertyName("flare_solverr")]
    [YamlMember(Alias = "flare_solverr", ApplyNamingConventions = false)]
    public FlareSolverrConfig FlareSolverr { get; set; } = new();

    [JsonPropertyName("calendar")]
    [YamlMember(Alias = "calendar", ApplyNamingConventions = false)]
    public CalendarConfig Calendar { get; set; } = new();

    [JsonPropertyName("appearance")]
    [YamlMember(Alias = "appearance", ApplyNamingConventions = false)]
    public AppearanceConfig Appearance { get; set; } = new();

    [JsonPropertyName("token_file")]
    [YamlMember(Alias = "token_file", ApplyNamingConventions = false)]
    public string TokenFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cruncharr", "token.json");

    [JsonPropertyName("log_mode")]
    [YamlMember(Alias = "log_mode", ApplyNamingConventions = false)]
    public bool LogMode { get; set; } = false;

    [JsonPropertyName("remove_finished_download")]
    [YamlMember(Alias = "remove_finished_download", ApplyNamingConventions = false)]
    public bool RemoveFinishedDownload { get; set; } = false;

    [JsonPropertyName("tracked_series_release_last_check_utc")]
    [YamlMember(Alias = "tracked_series_release_last_check_utc", ApplyNamingConventions = false)]
    public DateTime? TrackedSeriesReleaseLastCheckUtc { get; set; }

    public static CruncharrConfig Load(string configPath)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath);

                if (configPath.EndsWith(".yaml") || configPath.EndsWith(".yml"))
                {
                    var deserializer = new DeserializerBuilder()
                        .IgnoreUnmatchedProperties()
                        .Build();
                    return deserializer.Deserialize<CruncharrConfig>(content) ?? new CruncharrConfig();
                }

                return Newtonsoft.Json.JsonConvert.DeserializeObject<CruncharrConfig>(content) ?? new CruncharrConfig();
            }
            catch
            {
                return new CruncharrConfig();
            }
        }
        return new CruncharrConfig();
    }

    public void Save(string configPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            if (configPath.EndsWith(".yaml") || configPath.EndsWith(".yml"))
            {
                var serializer = new SerializerBuilder()
                    .Build();
                File.WriteAllText(configPath, serializer.Serialize(this));
            }
            else
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
        }
        catch
        {
            // Ignore save failures - config will be re-saved on next change
        }
    }

    public void NormalizeNotificationSettings()
    {
        if (Notifications == null)
        {
            Notifications = new NotificationsConfig();
        }

        // Ensure webhook URL is valid or null
        if (string.IsNullOrWhiteSpace(Notifications.WebhookUrl))
        {
            Notifications.WebhookUrl = null;
        }

        // Ensure webhook method is a valid HTTP method
        var validMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" };
        if (string.IsNullOrWhiteSpace(Notifications.WebhookMethod) ||
            !validMethods.Contains(Notifications.WebhookMethod.ToUpper()))
        {
            Notifications.WebhookMethod = "POST";
        }

        // Ensure content type is valid
        if (string.IsNullOrWhiteSpace(Notifications.WebhookContentType))
        {
            Notifications.WebhookContentType = "application/json";
        }

        // Ensure headers dictionary is initialized
        Notifications.WebhookHeaders ??= new Dictionary<string, string>();

        // Normalize boolean flags - ensure they have explicit values
        // (booleans are already non-nullable with defaults in the class)
    }

    public void SyncLegacyNotificationFields()
    {
        // Map old field names to new ones if config was loaded from older version
        // Currently no legacy field renames, but this method provides the hook
        // for future migrations without breaking existing configs

        // Example: if (Notifications.LegacyField != default) Notifications.NewField = Notifications.LegacyField;
    }

    public void ApplyEnvironmentVariables()
    {
        // Apply common environment variables for Docker deployment
        var email = Environment.GetEnvironmentVariable("CRUNCHYROLL_EMAIL");
        if (!string.IsNullOrEmpty(email)) Crunchyroll.Email = email;

        var password = Environment.GetEnvironmentVariable("CRUNCHYROLL_PASSWORD");
        if (!string.IsNullOrEmpty(password)) Crunchyroll.Password = password;

        var outputDir = Environment.GetEnvironmentVariable("CRUNCHYROLL_OUTPUT_DIR");
        if (!string.IsNullOrEmpty(outputDir)) Download.OutputDirectory = outputDir;

        var tempDir = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEMP_DIR");
        if (!string.IsNullOrEmpty(tempDir)) Download.TempDirectory = tempDir;

        var sonarrHost = Environment.GetEnvironmentVariable("SONARR_HOST");
        if (!string.IsNullOrEmpty(sonarrHost)) Sonarr.Host = sonarrHost;

        var sonarrPort = Environment.GetEnvironmentVariable("SONARR_PORT");
        if (!string.IsNullOrEmpty(sonarrPort) && int.TryParse(sonarrPort, out var port)) Sonarr.Port = port;

        var sonarrApiKey = Environment.GetEnvironmentVariable("SONARR_API_KEY");
        if (!string.IsNullOrEmpty(sonarrApiKey)) Sonarr.ApiKey = sonarrApiKey;
    }
}

public class CrunchyrollConfig
{
    [YamlMember(Alias = "email", ApplyNamingConventions = false)]
    public string Email { get; set; } = "";

    [YamlMember(Alias = "password", ApplyNamingConventions = false)]
    public string Password { get; set; } = "";

    [YamlMember(Alias = "use_beta_api", ApplyNamingConventions = false)]
    public bool UseBetaApi { get; set; } = true;

    [YamlMember(Alias = "mark_as_watched", ApplyNamingConventions = false)]
    public bool MarkAsWatched { get; set; } = false;

    [YamlMember(Alias = "search_fetch_featured_music", ApplyNamingConventions = false)]
    public bool SearchFetchFeaturedMusic { get; set; } = false;

    [YamlMember(Alias = "stream_endpoint", ApplyNamingConventions = false)]
    public StreamEndpointConfig StreamEndpoint { get; set; } = new();

    [YamlMember(Alias = "stream_endpoint_secondary", ApplyNamingConventions = false)]
    public StreamEndpointConfig StreamEndpointSecondary { get; set; } = new();
}

public class StreamEndpointConfig
{
    [YamlMember(Alias = "endpoint", ApplyNamingConventions = false)]
    public string Endpoint { get; set; } = "tv/android_tv";

    [YamlMember(Alias = "authorization", ApplyNamingConventions = false)]
    public string Authorization { get; set; } = "";

    [YamlMember(Alias = "user_agent", ApplyNamingConventions = false)]
    public string UserAgent { get; set; } = "";

    [YamlMember(Alias = "device_type", ApplyNamingConventions = false)]
    public string DeviceType { get; set; } = "";

    [YamlMember(Alias = "device_name", ApplyNamingConventions = false)]
    public string DeviceName { get; set; } = "";

    [YamlMember(Alias = "video", ApplyNamingConventions = false)]
    public bool Video { get; set; } = true;

    [YamlMember(Alias = "audio", ApplyNamingConventions = false)]
    public bool Audio { get; set; } = true;

    [YamlMember(Alias = "use_default", ApplyNamingConventions = false)]
    public bool UseDefault { get; set; } = true;
}

public class DownloadConfig
{
    [YamlMember(Alias = "output_dir", ApplyNamingConventions = false)]
    public string OutputDirectory { get; set; } = "/downloads";

    [YamlMember(Alias = "temp_dir", ApplyNamingConventions = false)]
    public string TempDirectory { get; set; } = "/tmp/cruncharr";

    [YamlMember(Alias = "use_temp_folder", ApplyNamingConventions = false)]
    public bool UseTempFolder { get; set; } = false;

    [YamlMember(Alias = "filename_template", ApplyNamingConventions = false)]
    public string FilenameTemplate { get; set; } = "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}";

    [YamlMember(Alias = "filename", ApplyNamingConventions = false)]
    public string Filename { get; set; } = "${seriesTitle} - S${season}E${episode} [${height}p]";

    [YamlMember(Alias = "filename_whitespace_substitute", ApplyNamingConventions = false)]
    public string FilenameWhitespaceSubstitute { get; set; } = "";

    [YamlMember(Alias = "video_title", ApplyNamingConventions = false)]
    public string? VideoTitle { get; set; }

    [YamlMember(Alias = "include_video_description", ApplyNamingConventions = false)]
    public bool IncludeVideoDescription { get; set; } = false;

    [YamlMember(Alias = "description_lang", ApplyNamingConventions = false)]
    public string DescriptionLang { get; set; } = "en-US";

    [YamlMember(Alias = "leading_numbers", ApplyNamingConventions = false)]
    public int LeadingNumbers { get; set; } = 2;

    [YamlMember(Alias = "quality", ApplyNamingConventions = false)]
    public string Quality { get; set; } = "best";

    [YamlMember(Alias = "quality_video", ApplyNamingConventions = false)]
    public string QualityVideo { get; set; } = "best";

    [YamlMember(Alias = "encoding_preset", ApplyNamingConventions = false)]
    public string? EncodingPreset { get; set; }

    [YamlMember(Alias = "quality_audio", ApplyNamingConventions = false)]
    public string QualityAudio { get; set; } = "best";

    [YamlMember(Alias = "dub_languages", ApplyNamingConventions = false)]
    public List<string> DubLanguages { get; set; } = new() { "ja-JP" };

    [YamlMember(Alias = "default_audio", ApplyNamingConventions = false)]
    public string DefaultAudio { get; set; } = "ja-JP";

    [YamlMember(Alias = "download_description_audio", ApplyNamingConventions = false)]
    public bool DownloadDescriptionAudio { get; set; } = false;

    [YamlMember(Alias = "download_first_available_dub", ApplyNamingConventions = false)]
    public bool DownloadFirstAvailableDub { get; set; } = false;

    [YamlMember(Alias = "download_multiple_dubs", ApplyNamingConventions = false)]
    public bool DownloadMultipleDubs { get; set; } = false;

    [YamlMember(Alias = "dl_video_once", ApplyNamingConventions = false)]
    public bool DlVideoOnce { get; set; } = true;

    [YamlMember(Alias = "download_allow_early_start", ApplyNamingConventions = false)]
    public bool DownloadAllowEarlyStart { get; set; } = false;

    [YamlMember(Alias = "keep_dubs_separate", ApplyNamingConventions = false)]
    public bool KeepDubsSeparate { get; set; } = false;

    [YamlMember(Alias = "dub_download_delay_seconds", ApplyNamingConventions = false)]
    public int DubDownloadDelaySeconds { get; set; } = 0;

    [YamlMember(Alias = "cooldown_delay_seconds", ApplyNamingConventions = false)]
    public int CooldownDelaySeconds { get; set; } = 0;

    [YamlMember(Alias = "hard_sub_lang", ApplyNamingConventions = false)]
    public string HardSubLang { get; set; } = "none";

    [YamlMember(Alias = "hard_sub_raw_fallback", ApplyNamingConventions = false)]
    public bool HardSubRawFallback { get; set; } = false;

    [YamlMember(Alias = "kstream", ApplyNamingConventions = false)]
    public int Kstream { get; set; } = 0;

    [YamlMember(Alias = "stream_server", ApplyNamingConventions = false)]
    public int StreamServer { get; set; } = 0;

    [YamlMember(Alias = "subtitle_languages", ApplyNamingConventions = false)]
    public List<string> SubtitleLanguages { get; set; } = new() { "en-US" };

    [YamlMember(Alias = "soft_subs", ApplyNamingConventions = false)]
    public List<string> SoftSubs { get; set; } = new() { "en-US" };

    [YamlMember(Alias = "default_sub", ApplyNamingConventions = false)]
    public string DefaultSub { get; set; } = "en-US";

    [YamlMember(Alias = "include_signs_subs", ApplyNamingConventions = false)]
    public bool IncludeSignsSubs { get; set; } = false;

    [YamlMember(Alias = "signs_subs_as_forced", ApplyNamingConventions = false)]
    public bool SignsSubsAsForced { get; set; } = false;

    [YamlMember(Alias = "include_cc_subs", ApplyNamingConventions = false)]
    public bool IncludeCcSubs { get; set; } = false;

    [YamlMember(Alias = "cc_subs_muxing_flag", ApplyNamingConventions = false)]
    public bool CcSubsMuxingFlag { get; set; } = false;

    [YamlMember(Alias = "subs_download_duplicate", ApplyNamingConventions = false)]
    public bool SubsDownloadDuplicate { get; set; } = false;

    [YamlMember(Alias = "fix_ccc_subtitles", ApplyNamingConventions = false)]
    public bool FixCccSubtitles { get; set; } = true;

    [YamlMember(Alias = "timeout", ApplyNamingConventions = false)]
    public int Timeout { get; set; } = 15000;

    [YamlMember(Alias = "skip_subs", ApplyNamingConventions = false)]
    public bool SkipSubs { get; set; } = false;

    [YamlMember(Alias = "cc_tag", ApplyNamingConventions = false)]
    public string CcTag { get; set; } = "CC";

    [YamlMember(Alias = "convert_vtt_to_ass", ApplyNamingConventions = false)]
    public bool ConvertVttToAss { get; set; } = true;



    [YamlMember(Alias = "cc_subs_font", ApplyNamingConventions = false)]
    public string CcSubsFont { get; set; } = "Trebuchet MS";

    [YamlMember(Alias = "subs_add_scaled_border", ApplyNamingConventions = false)]
    public string SubsAddScaledBorder { get; set; } = "DontAdd";

    [YamlMember(Alias = "simultaneous_downloads", ApplyNamingConventions = false)]
    public int SimultaneousDownloads { get; set; } = 2;

    [YamlMember(Alias = "simultaneous_processing_jobs", ApplyNamingConventions = false)]
    public int SimultaneousProcessingJobs { get; set; } = 2;

    [YamlMember(Alias = "download_methode_new", ApplyNamingConventions = false)]
    public bool DownloadMethodeNew { get; set; } = false;

    [YamlMember(Alias = "download_only_with_all_selected_dubsub", ApplyNamingConventions = false)]
    public bool DownloadOnlyWithAllSelectedDubSub { get; set; } = false;

    [YamlMember(Alias = "download_speed_limit", ApplyNamingConventions = false)]
    public int DownloadSpeedLimit { get; set; } = 0;

    [YamlMember(Alias = "download_speed_bits", ApplyNamingConventions = false)]
    public bool DownloadSpeedInBits { get; set; } = false;

    [YamlMember(Alias = "retry_attempts", ApplyNamingConventions = false)]
    public int RetryAttempts { get; set; } = 5;

    [YamlMember(Alias = "retry_delay", ApplyNamingConventions = false)]
    public int RetryDelay { get; set; } = 5;

    [YamlMember(Alias = "retry_delay_seconds", ApplyNamingConventions = false)]
    public int RetryDelaySeconds { get; set; } = 5;

    [YamlMember(Alias = "playback_rate_limit_retry_delay_seconds", ApplyNamingConventions = false)]
    public int PlaybackRateLimitRetryDelaySeconds { get; set; } = 30;

    [YamlMember(Alias = "retry_max_delay_seconds", ApplyNamingConventions = false)]
    public int RetryMaxDelaySeconds { get; set; } = 3600;

    [YamlMember(Alias = "download_part_size", ApplyNamingConventions = false)]
    public int PartSize { get; set; } = 10;

    [YamlMember(Alias = "no_video", ApplyNamingConventions = false)]
    public bool NoVideo { get; set; } = false;

    [YamlMember(Alias = "no_audio", ApplyNamingConventions = false)]
    public bool NoAudio { get; set; } = false;

    [YamlMember(Alias = "include_chapters", ApplyNamingConventions = false)]
    public bool IncludeChapters { get; set; } = true;



    [YamlMember(Alias = "skip_muxing", ApplyNamingConventions = false)]
    public bool SkipMuxing { get; set; } = false;

    [YamlMember(Alias = "mux_mp4", ApplyNamingConventions = false)]
    public bool MuxMp4 { get; set; } = false;

    [YamlMember(Alias = "mux_audio_only_to_mp3", ApplyNamingConventions = false)]
    public bool MuxAudioOnlyToMp3 { get; set; } = false;

    [YamlMember(Alias = "mux_skip_subs", ApplyNamingConventions = false)]
    public bool SkipSubMux { get; set; } = false;

    [YamlMember(Alias = "mux_fonts", ApplyNamingConventions = false)]
    public bool MuxFonts { get; set; } = false;

    [YamlMember(Alias = "mux_typesetting_fonts", ApplyNamingConventions = false)]
    public bool MuxTypesettingFonts { get; set; } = false;

    [YamlMember(Alias = "mux_cover", ApplyNamingConventions = false)]
    public bool MuxCover { get; set; } = false;

    [YamlMember(Alias = "mux_default_video", ApplyNamingConventions = false)]
    public string DefaultVideo { get; set; } = "ja-JP";

    [YamlMember(Alias = "mux_default_dub", ApplyNamingConventions = false)]
    public string MuxDefaultDub { get; set; } = "ja-JP";

    [YamlMember(Alias = "mux_default_sub", ApplyNamingConventions = false)]
    public string MuxDefaultSub { get; set; } = "en-US";

    [YamlMember(Alias = "mux_default_sub_signs", ApplyNamingConventions = false)]
    public bool MuxDefaultSubSigns { get; set; } = false;

    [YamlMember(Alias = "mux_default_sub_forced_display", ApplyNamingConventions = false)]
    public bool MuxDefaultSubForcedDisplay { get; set; } = false;

    [YamlMember(Alias = "mux_sync_dubs", ApplyNamingConventions = false)]
    public bool SyncTiming { get; set; } = false;

    [YamlMember(Alias = "mux_sync_fallback_full_quality", ApplyNamingConventions = false)]
    public bool SyncTimingFullQualityFallback { get; set; } = false;

    [YamlMember(Alias = "mux_sync_hwaccel", ApplyNamingConventions = false)]
    public string? SyncHwAccel { get; set; }

    [YamlMember(Alias = "mux_mkvmerge", ApplyNamingConventions = false)]
    public List<string> MkvmergeOptions { get; set; } = new();

    [YamlMember(Alias = "mux_ffmpeg", ApplyNamingConventions = false)]
    public List<string> FfmpegOptions { get; set; } = new();

    [YamlMember(Alias = "encode_enabled", ApplyNamingConventions = false)]
    public bool EncodeEnabled { get; set; } = false;

    [YamlMember(Alias = "history_enabled", ApplyNamingConventions = false)]
    public bool HistoryEnabled { get; set; } = true;

    [YamlMember(Alias = "no_cleanup", ApplyNamingConventions = false)]
    public bool NoCleanup { get; set; } = false;

    [YamlMember(Alias = "force_override", ApplyNamingConventions = false)]
    public bool ForceOverride { get; set; } = false;

    [YamlMember(Alias = "replace_existing_files", ApplyNamingConventions = false)]
    public bool ReplaceExistingFiles { get; set; } = false;
}

public class HistoryConfig
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "count_missing", ApplyNamingConventions = false)]
    public bool CountMissing { get; set; } = false;

    [YamlMember(Alias = "include_cr_artists", ApplyNamingConventions = false)]
    public bool IncludeCrArtists { get; set; } = false;

    [YamlMember(Alias = "remove_missing_episodes", ApplyNamingConventions = false)]
    public bool RemoveMissingEpisodes { get; set; } = true;

    [YamlMember(Alias = "add_specials", ApplyNamingConventions = false)]
    public bool AddSpecials { get; set; } = false;

    [YamlMember(Alias = "skip_unmonitored", ApplyNamingConventions = false)]
    public bool SkipUnmonitored { get; set; } = false;

    [YamlMember(Alias = "count_sonarr", ApplyNamingConventions = false)]
    public bool CountSonarr { get; set; } = false;

    [YamlMember(Alias = "lang", ApplyNamingConventions = false)]
    public string Lang { get; set; } = "en-US";

    [YamlMember(Alias = "auto_refresh_interval_minutes", ApplyNamingConventions = false)]
    public int AutoRefreshIntervalMinutes { get; set; } = 0;

    [YamlMember(Alias = "auto_refresh_mode", ApplyNamingConventions = false)]
    public int AutoRefreshMode { get; set; } = 50;

    [YamlMember(Alias = "auto_refresh_add_to_queue", ApplyNamingConventions = false)]
    public bool AutoRefreshAddToQueue { get; set; } = true;
}

public class QueueConfig
{
    [YamlMember(Alias = "persist", ApplyNamingConventions = false)]
    public bool PersistQueue { get; set; } = false;

    [YamlMember(Alias = "auto_download", ApplyNamingConventions = false)]
    public bool AutoDownload { get; set; } = false;

    [YamlMember(Alias = "simultaneous_processing_jobs", ApplyNamingConventions = false)]
    public int SimultaneousProcessingJobs { get; set; } = 2;

    [YamlMember(Alias = "queue_file_path", ApplyNamingConventions = false)]
    public string QueueFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cruncharr", "queue.json");

    [YamlMember(Alias = "shutdown_when_queue_empty", ApplyNamingConventions = false)]
    public bool ShutdownWhenQueueEmpty { get; set; } = false;
}

public class NotificationsConfig
{
    [YamlMember(Alias = "webhook_url", ApplyNamingConventions = false)]
    public string? WebhookUrl { get; set; }

    [YamlMember(Alias = "on_complete", ApplyNamingConventions = false)]
    public bool OnComplete { get; set; } = true;

    [YamlMember(Alias = "on_error", ApplyNamingConventions = false)]
    public bool OnError { get; set; } = true;

    [YamlMember(Alias = "webhook_enabled", ApplyNamingConventions = false)]
    public bool WebhookEnabled { get; set; } = false;

    [YamlMember(Alias = "webhook_method", ApplyNamingConventions = false)]
    public string WebhookMethod { get; set; } = "POST";

    [YamlMember(Alias = "webhook_content_type", ApplyNamingConventions = false)]
    public string WebhookContentType { get; set; } = "application/json";

    [YamlMember(Alias = "webhook_headers", ApplyNamingConventions = false)]
    public Dictionary<string, string> WebhookHeaders { get; set; } = new();

    [YamlMember(Alias = "webhook_body_template", ApplyNamingConventions = false)]
    public string WebhookBodyTemplate { get; set; } = "";

    [YamlMember(Alias = "notify_queue_finished", ApplyNamingConventions = false)]
    public bool NotifyQueueFinished { get; set; } = false;

    [YamlMember(Alias = "notify_download_finished", ApplyNamingConventions = false)]
    public bool NotifyDownloadFinished { get; set; } = false;

    [YamlMember(Alias = "notify_download_failed", ApplyNamingConventions = false)]
    public bool NotifyDownloadFailed { get; set; } = false;

    [YamlMember(Alias = "notify_tracked_series_released", ApplyNamingConventions = false)]
    public bool NotifyTrackedSeriesReleased { get; set; } = false;

    [YamlMember(Alias = "notify_login_expired", ApplyNamingConventions = false)]
    public bool NotifyLoginExpired { get; set; } = false;

    [YamlMember(Alias = "notify_update_available", ApplyNamingConventions = false)]
    public bool NotifyUpdateAvailable { get; set; } = false;

    [YamlMember(Alias = "download_finished_play_sound", ApplyNamingConventions = false)]
    public bool DownloadFinishedPlaySound { get; set; } = false;

    [YamlMember(Alias = "download_finished_sound_path", ApplyNamingConventions = false)]
    public string? DownloadFinishedSoundPath { get; set; }

    [YamlMember(Alias = "download_finished_execute", ApplyNamingConventions = false)]
    public bool DownloadFinishedExecute { get; set; } = false;

    [YamlMember(Alias = "download_finished_execute_path", ApplyNamingConventions = false)]
    public string? DownloadFinishedExecutePath { get; set; }
}

public class SonarrConfig
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = false;

    [YamlMember(Alias = "host", ApplyNamingConventions = false)]
    public string? Host { get; set; }

    [YamlMember(Alias = "port", ApplyNamingConventions = false)]
    public int Port { get; set; } = 0;

    [YamlMember(Alias = "api_key", ApplyNamingConventions = false)]
    public string? ApiKey { get; set; }

    [YamlMember(Alias = "use_ssl", ApplyNamingConventions = false)]
    public bool UseSsl { get; set; } = false;

    [YamlMember(Alias = "url_base", ApplyNamingConventions = false)]
    public string? UrlBase { get; set; }

    [YamlMember(Alias = "use_sonarr_numbering", ApplyNamingConventions = false)]
    public bool UseSonarrNumbering { get; set; } = false;
}

public class ProxyConfig
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = false;

    [YamlMember(Alias = "socks", ApplyNamingConventions = false)]
    public bool Socks { get; set; } = false;

    [YamlMember(Alias = "host", ApplyNamingConventions = false)]
    public string? Host { get; set; }

    [YamlMember(Alias = "port", ApplyNamingConventions = false)]
    public int Port { get; set; } = 0;

    [YamlMember(Alias = "username", ApplyNamingConventions = false)]
    public string? Username { get; set; }

    [YamlMember(Alias = "password", ApplyNamingConventions = false)]
    public string? Password { get; set; }
}

public class FlareSolverrConfig
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = false;

    [YamlMember(Alias = "host", ApplyNamingConventions = false)]
    public string Host { get; set; } = "localhost";

    [YamlMember(Alias = "port", ApplyNamingConventions = false)]
    public int Port { get; set; } = 0;

    [YamlMember(Alias = "use_ssl", ApplyNamingConventions = false)]
    public bool UseSsl { get; set; } = false;

    [YamlMember(Alias = "mitm_enabled", ApplyNamingConventions = false)]
    public bool MitmEnabled { get; set; } = false;

    [YamlMember(Alias = "mitm_host", ApplyNamingConventions = false)]
    public string MitmHost { get; set; } = "localhost";

    [YamlMember(Alias = "mitm_port", ApplyNamingConventions = false)]
    public int MitmPort { get; set; } = 8080;

    [YamlMember(Alias = "mitm_use_ssl", ApplyNamingConventions = false)]
    public bool MitmUseSsl { get; set; } = false;
}

public class CalendarConfig
{
    [YamlMember(Alias = "language", ApplyNamingConventions = false)]
    public string Language { get; set; } = "en-us";

    [YamlMember(Alias = "dub_filter", ApplyNamingConventions = false)]
    public string DubFilter { get; set; } = "none";

    [YamlMember(Alias = "custom", ApplyNamingConventions = false)]
    public bool Custom { get; set; } = true;

    [YamlMember(Alias = "hide_dubs", ApplyNamingConventions = false)]
    public bool HideDubs { get; set; } = false;

    [YamlMember(Alias = "show_upcoming_episodes", ApplyNamingConventions = false)]
    public bool ShowUpcomingEpisodes { get; set; } = false;

    [YamlMember(Alias = "update_history", ApplyNamingConventions = false)]
    public bool UpdateHistory { get; set; } = false;
}

public class AppearanceConfig
{
    [YamlMember(Alias = "theme", ApplyNamingConventions = false)]
    public string Theme { get; set; } = "System";

    [YamlMember(Alias = "accent_color", ApplyNamingConventions = false)]
    public string? AccentColor { get; set; }

    [YamlMember(Alias = "background_image_path", ApplyNamingConventions = false)]
    public string? BackgroundImagePath { get; set; }

    [YamlMember(Alias = "background_image_opacity", ApplyNamingConventions = false)]
    public double BackgroundImageOpacity { get; set; } = 0.5;

    [YamlMember(Alias = "background_image_blur_radius", ApplyNamingConventions = false)]
    public double BackgroundImageBlurRadius { get; set; } = 10;
}

public enum HistoryRefreshMode
{
    DefaultAll = 0,
    DefaultActive = 1,
    FastNewReleases = 50
}

public enum ScaledBorderAndShadowSelection
{
    DontAdd,
    ScaledBorderAndShadowYes,
    ScaledBorderAndShadowNo
}
