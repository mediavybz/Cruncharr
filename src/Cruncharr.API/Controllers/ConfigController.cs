using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase{
    private readonly CruncharrConfig _config;
    private readonly ILogger<ConfigController> _logger;
    private static readonly object _configLock = new object();

    public ConfigController(CruncharrConfig config, ILogger<ConfigController> logger){
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Get current configuration (sanitized - no passwords)
    /// </summary>
    [HttpGet]
    public ActionResult GetConfig(){
        return Ok(new{
            Crunchyroll = new{
                Email = _config.Crunchyroll.Email,
                UseBetaApi = _config.Crunchyroll.UseBetaApi,
                MarkAsWatched = _config.Crunchyroll.MarkAsWatched,
                SearchFetchFeaturedMusic = _config.Crunchyroll.SearchFetchFeaturedMusic,
                StreamEndpoint = SanitizeStreamEndpoint(_config.Crunchyroll.StreamEndpoint),
                StreamEndpointSecondary = SanitizeStreamEndpoint(_config.Crunchyroll.StreamEndpointSecondary),
                DefaultStreamEndpoint = new{
                    Endpoint = "tv/android_tv",
                    Authorization = "", // Server-managed, not exposed to client
                    UserAgent = "ANDROIDTV/3.61.0_22341 Android/16",
                    DeviceType = "Android TV",
                    DeviceName = "Android TV",
                    Video = true,
                    Audio = true
                },
                DefaultStreamEndpointSecondary = new{
                    Endpoint = "android/phone",
                    Authorization = "", // Server-managed, not exposed to client
                    UserAgent = "Crunchyroll/3.109.2 Android/16 okhttp/4.12.0",
                    DeviceType = "OnePlus CPH2449",
                    DeviceName = "CPH2449",
                    Video = true,
                    Audio = true
                }
            },
            Download = new{
                OutputDirectory = _config.Download.OutputDirectory,
                TempDirectory = _config.Download.TempDirectory,
                UseTempFolder = _config.Download.UseTempFolder,
                Filename = _config.Download.Filename,
                FilenameTemplate = _config.Download.FilenameTemplate,
                FilenameWhitespaceSubstitute = _config.Download.FilenameWhitespaceSubstitute,
                VideoTitle = _config.Download.VideoTitle,
                IncludeVideoDescription = _config.Download.IncludeVideoDescription,
                DescriptionLang = _config.Download.DescriptionLang,
                LeadingNumbers = _config.Download.LeadingNumbers,
                QualityVideo = _config.Download.QualityVideo,
                QualityAudio = _config.Download.QualityAudio,
                DubLanguages = _config.Download.DubLanguages,
                DefaultAudio = _config.Download.DefaultAudio,
                DownloadDescriptionAudio = _config.Download.DownloadDescriptionAudio,
                DownloadFirstAvailableDub = _config.Download.DownloadFirstAvailableDub,
                DlVideoOnce = _config.Download.DlVideoOnce,
                KeepDubsSeparate = _config.Download.KeepDubsSeparate,
                DubDownloadDelaySeconds = _config.Download.DubDownloadDelaySeconds,
                CooldownDelaySeconds = _config.Download.CooldownDelaySeconds,
                HardSubLang = _config.Download.HardSubLang,
                HardSubRawFallback = _config.Download.HardSubRawFallback,
                Kstream = _config.Download.Kstream,
                StreamServer = _config.Download.StreamServer,
                SoftSubs = _config.Download.SoftSubs,
                DefaultSub = _config.Download.DefaultSub,
                IncludeSignsSubs = _config.Download.IncludeSignsSubs,
                SignsSubsAsForced = _config.Download.SignsSubsAsForced,
                IncludeCcSubs = _config.Download.IncludeCcSubs,
                CcSubsMuxingFlag = _config.Download.CcSubsMuxingFlag,
                SubsDownloadDuplicate = _config.Download.SubsDownloadDuplicate,
                FixCccSubtitles = _config.Download.FixCccSubtitles,
                ConvertVttToAss = _config.Download.ConvertVttToAss,
                CcSubsFont = _config.Download.CcSubsFont,
                SubsAddScaledBorder = _config.Download.SubsAddScaledBorder,
                SimultaneousDownloads = _config.Download.SimultaneousDownloads,
                SimultaneousProcessingJobs = _config.Download.SimultaneousProcessingJobs,
                DownloadMethodeNew = _config.Download.DownloadMethodeNew,
                DownloadAllowEarlyStart = _config.Download.DownloadAllowEarlyStart,
                DownloadOnlyWithAllSelectedDubSub = _config.Download.DownloadOnlyWithAllSelectedDubSub,
                DownloadSpeedLimit = _config.Download.DownloadSpeedLimit,
                DownloadSpeedInBits = _config.Download.DownloadSpeedInBits,
                Timeout = _config.Download.Timeout,
                SkipSubs = _config.Download.SkipSubs,
                CcTag = _config.Download.CcTag,
                RetryAttempts = _config.Download.RetryAttempts,
                RetryDelay = _config.Download.RetryDelay,
                PlaybackRateLimitRetryDelaySeconds = _config.Download.PlaybackRateLimitRetryDelaySeconds,
                RetryMaxDelaySeconds = _config.Download.RetryMaxDelaySeconds,
                PartSize = _config.Download.PartSize,
                NoVideo = _config.Download.NoVideo,
                NoAudio = _config.Download.NoAudio,
                IncludeChapters = _config.Download.IncludeChapters,
                SkipMuxing = _config.Download.SkipMuxing,
                MuxMp4 = _config.Download.MuxMp4,
                MuxAudioOnlyToMp3 = _config.Download.MuxAudioOnlyToMp3,
                SkipSubMux = _config.Download.SkipSubMux,
                MuxFonts = _config.Download.MuxFonts,
                MuxTypesettingFonts = _config.Download.MuxTypesettingFonts,
                MuxCover = _config.Download.MuxCover,
                MuxDefaultDub = _config.Download.MuxDefaultDub,
                MuxDefaultSub = _config.Download.MuxDefaultSub,
                MuxDefaultSubSigns = _config.Download.MuxDefaultSubSigns,
                MuxDefaultSubForcedDisplay = _config.Download.MuxDefaultSubForcedDisplay,
                DefaultVideo = _config.Download.DefaultVideo,
                ReplaceExistingFiles = _config.Download.ReplaceExistingFiles,
                SyncTiming = _config.Download.SyncTiming,
                SyncTimingFullQualityFallback = _config.Download.SyncTimingFullQualityFallback,
                SyncHwAccel = _config.Download.SyncHwAccel,
                MkvmergeOptions = _config.Download.MkvmergeOptions,
                FfmpegOptions = _config.Download.FfmpegOptions,
                EncodeEnabled = _config.Download.EncodeEnabled,
                EncodingPreset = _config.Download.EncodingPreset,
                DownloadMultipleDubs = _config.Download.DownloadMultipleDubs
            },
            Queue = new{
                PersistQueue = _config.Queue.PersistQueue,
                AutoDownload = _config.Queue.AutoDownload,
                SimultaneousProcessingJobs = _config.Queue.SimultaneousProcessingJobs,
                QueueFilePath = _config.Queue.QueueFilePath,
                ShutdownWhenQueueEmpty = _config.Queue.ShutdownWhenQueueEmpty
            },
            History = new{
                Enabled = _config.History.Enabled,
                CountMissing = _config.History.CountMissing,
                IncludeCrArtists = _config.History.IncludeCrArtists,
                RemoveMissingEpisodes = _config.History.RemoveMissingEpisodes,
                AddSpecials = _config.History.AddSpecials,
                SkipUnmonitored = _config.History.SkipUnmonitored,
                CountSonarr = _config.History.CountSonarr,
                Lang = _config.History.Lang,
                AutoRefreshIntervalMinutes = _config.History.AutoRefreshIntervalMinutes,
                AutoRefreshMode = _config.History.AutoRefreshMode,
                AutoRefreshAddToQueue = _config.History.AutoRefreshAddToQueue
            },
            HistoryPageProperties = _config.HistoryPageProperties,
            SeasonsPageProperties = _config.SeasonsPageProperties,
            TrackedSeriesReleaseLastCheckUtc = _config.TrackedSeriesReleaseLastCheckUtc,
            Notifications = new{
                WebhookUrl = _config.Notifications.WebhookUrl,
                WebhookEnabled = _config.Notifications.WebhookEnabled,
                WebhookMethod = _config.Notifications.WebhookMethod,
                WebhookContentType = _config.Notifications.WebhookContentType,
                WebhookHeaders = _config.Notifications.WebhookHeaders,
                WebhookBodyTemplate = _config.Notifications.WebhookBodyTemplate,
                NotifyQueueFinished = _config.Notifications.NotifyQueueFinished,
                NotifyDownloadFinished = _config.Notifications.NotifyDownloadFinished,
                NotifyDownloadFailed = _config.Notifications.NotifyDownloadFailed,
                NotifyTrackedSeriesReleased = _config.Notifications.NotifyTrackedSeriesReleased,
                NotifyLoginExpired = _config.Notifications.NotifyLoginExpired,
                NotifyUpdateAvailable = _config.Notifications.NotifyUpdateAvailable,
                DownloadFinishedPlaySound = _config.Notifications.DownloadFinishedPlaySound,
                DownloadFinishedSoundPath = _config.Notifications.DownloadFinishedSoundPath,
                DownloadFinishedExecute = _config.Notifications.DownloadFinishedExecute,
                DownloadFinishedExecutePath = _config.Notifications.DownloadFinishedExecutePath
            },
            Sonarr = new{
                Enabled = _config.Sonarr.Enabled,
                Host = _config.Sonarr.Host,
                Port = _config.Sonarr.Port,
                ApiKey = !string.IsNullOrEmpty(_config.Sonarr.ApiKey) ? "[configured]" : null,
                UseSsl = _config.Sonarr.UseSsl,
                UrlBase = _config.Sonarr.UrlBase,
                UseSonarrNumbering = _config.Sonarr.UseSonarrNumbering
            },
            Proxy = new{
                Enabled = _config.Proxy.Enabled,
                Socks = _config.Proxy.Socks,
                Host = _config.Proxy.Host,
                Port = _config.Proxy.Port,
                Username = _config.Proxy.Username,
                Password = !string.IsNullOrEmpty(_config.Proxy.Password) ? "[configured]" : null
            },
            FlareSolverr = new{
                Enabled = _config.FlareSolverr.Enabled,
                Host = _config.FlareSolverr.Host,
                Port = _config.FlareSolverr.Port,
                UseSsl = _config.FlareSolverr.UseSsl,
                MitmEnabled = _config.FlareSolverr.MitmEnabled,
                MitmHost = _config.FlareSolverr.MitmHost,
                MitmPort = _config.FlareSolverr.MitmPort,
                MitmUseSsl = _config.FlareSolverr.MitmUseSsl
            },
            Calendar = new{
                Language = _config.Calendar.Language,
                DubFilter = _config.Calendar.DubFilter,
                Custom = _config.Calendar.Custom,
                HideDubs = _config.Calendar.HideDubs,
                ShowUpcomingEpisodes = _config.Calendar.ShowUpcomingEpisodes,
                UpdateHistory = _config.Calendar.UpdateHistory
            },
            Appearance = new{
                Theme = _config.Appearance.Theme,
                AccentColor = _config.Appearance.AccentColor,
                BackgroundImagePath = _config.Appearance.BackgroundImagePath,
                BackgroundImageOpacity = _config.Appearance.BackgroundImageOpacity,
                BackgroundImageBlurRadius = _config.Appearance.BackgroundImageBlurRadius
            },
            General = new{
                LogMode = _config.LogMode,
                RemoveFinishedDownload = _config.RemoveFinishedDownload,
                TokenFilePath = _config.TokenFilePath
            }
        });
    }

    /// <summary>
    /// Test webhook by sending a test payload to the provided URL
    /// </summary>
    [HttpPost("webhook/test")]
    public async Task<IActionResult> TestWebhook([FromBody] WebhookTestRequest request){
        if (string.IsNullOrWhiteSpace(request.Url)){
            return BadRequest(new { Success = false, Message = "Webhook URL is required" });
        }
        
        // SSRF protection: validate URL scheme and block private IPs
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)){
            return BadRequest(new { Success = false, Message = "Invalid URL format" });
        }
        
        if (uri.Scheme != "http" && uri.Scheme != "https"){
            return BadRequest(new { Success = false, Message = "Only HTTP and HTTPS URLs are allowed" });
        }
        
        // Block localhost and private IP ranges
        var host = uri.Host;
        if (host == "localhost" || host == "127.0.0.1" || host == "::1"){
            return BadRequest(new { Success = false, Message = "Localhost URLs are not allowed" });
        }
        
        if (System.Net.IPAddress.TryParse(host, out var ip)){
            if (ip.GetAddressBytes()[0] == 10 || // 10.0.0.0/8
                (ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] >= 16 && ip.GetAddressBytes()[1] <= 31) || // 172.16.0.0/12
                (ip.GetAddressBytes()[0] == 192 && ip.GetAddressBytes()[1] == 168) || // 192.168.0.0/16
                (ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254)){ // 169.254.0.0/16
                return BadRequest(new { Success = false, Message = "Private IP addresses are not allowed" });
            }
        }
        
        try{
            using var client = new HttpClient{ Timeout = TimeSpan.FromSeconds(30) };
            var payload = new{
                event_type = "test",
                message = "This is a test webhook from Cruncharr",
                timestamp = DateTime.UtcNow
            };
            var content = System.Net.Http.Json.JsonContent.Create(payload);
            var response = await client.PostAsync(request.Url, content);
            if (response.IsSuccessStatusCode){
                return Ok(new { Success = true, Message = "Webhook test sent successfully" });
            }
            return StatusCode((int)response.StatusCode, new { Success = false, Message = $"Webhook returned {(int)response.StatusCode}" });
        } catch (Exception ex){
            _logger.LogError(ex, "Webhook test failed");
            return StatusCode(500, new { Success = false, Message = ex.Message });
        }
    }

    /// <summary>
    /// Update configuration
    /// </summary>
    [HttpPost]
    public IActionResult UpdateConfig([FromBody] ConfigUpdateRequest request){
        try{
            if (request == null) return BadRequest(new { Success = false, Message = "Request body is required" });
            
            lock (_configLock){
                UpdateConfigFromRequest(request);
                
                var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
                _config.Save(configPath);
                _logger.LogInformation("Configuration saved to {Path}", configPath);
            }
            return Ok(new { Success = true, Message = "Configuration saved" });
        } catch (Exception ex){
            _logger.LogError(ex, "Failed to save configuration");
            return StatusCode(500, new { Success = false, Message = ex.Message });
        }
    }
    
    private static object? SanitizeStreamEndpoint(object? endpoint){
        if (endpoint == null) return null;
        // Create a shallow copy with Authorization stripped
        var json = System.Text.Json.JsonSerializer.Serialize(endpoint);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject()){
            if (prop.NameEquals("Authorization") || prop.NameEquals("authorization")){
                dict[prop.Name] = ""; // Strip auth token
            } else{
                dict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            }
        }
        return dict;
    }

    private static string ValidatePath(string? path, string fieldName){
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        // Reject paths with directory traversal
        if (path.Contains("..") || path.Contains("~")){
            throw new ArgumentException($"Path traversal not allowed in {fieldName}");
        }
        // Normalize path
        return path;
    }

    private void UpdateConfigFromRequest(ConfigUpdateRequest request){
        if (request.Crunchyroll != null){
            if (!string.IsNullOrEmpty(request.Crunchyroll.Email))
                _config.Crunchyroll.Email = request.Crunchyroll.Email;
            if (request.Crunchyroll.UseBetaApi.HasValue)
                _config.Crunchyroll.UseBetaApi = request.Crunchyroll.UseBetaApi.Value;
            if (request.Crunchyroll.MarkAsWatched.HasValue)
                _config.Crunchyroll.MarkAsWatched = request.Crunchyroll.MarkAsWatched.Value;
            if (request.Crunchyroll.SearchFetchFeaturedMusic.HasValue)
                _config.Crunchyroll.SearchFetchFeaturedMusic = request.Crunchyroll.SearchFetchFeaturedMusic.Value;
            if (request.Crunchyroll.StreamEndpoint != null)
                _config.Crunchyroll.StreamEndpoint = request.Crunchyroll.StreamEndpoint;
            if (request.Crunchyroll.StreamEndpointSecondary != null)
                _config.Crunchyroll.StreamEndpointSecondary = request.Crunchyroll.StreamEndpointSecondary;
        }

        if (request.Download != null){
            var dl = request.Download;
            if (!string.IsNullOrEmpty(dl.OutputDirectory)) _config.Download.OutputDirectory = ValidatePath(dl.OutputDirectory, nameof(dl.OutputDirectory));
            if (!string.IsNullOrEmpty(dl.TempDirectory)) _config.Download.TempDirectory = ValidatePath(dl.TempDirectory, nameof(dl.TempDirectory));
            if (dl.UseTempFolder.HasValue) _config.Download.UseTempFolder = dl.UseTempFolder.Value;
            if (!string.IsNullOrEmpty(dl.Filename)) _config.Download.Filename = dl.Filename;
            if (!string.IsNullOrEmpty(dl.FilenameTemplate)) _config.Download.FilenameTemplate = dl.FilenameTemplate;
            if (!string.IsNullOrEmpty(dl.FilenameWhitespaceSubstitute)) _config.Download.FilenameWhitespaceSubstitute = dl.FilenameWhitespaceSubstitute;
            if (dl.VideoTitle != null) _config.Download.VideoTitle = dl.VideoTitle;
            if (dl.IncludeVideoDescription.HasValue) _config.Download.IncludeVideoDescription = dl.IncludeVideoDescription.Value;
            if (!string.IsNullOrEmpty(dl.DescriptionLang)) _config.Download.DescriptionLang = dl.DescriptionLang;
            if (dl.LeadingNumbers.HasValue) _config.Download.LeadingNumbers = dl.LeadingNumbers.Value;
            if (!string.IsNullOrEmpty(dl.QualityVideo)) _config.Download.QualityVideo = dl.QualityVideo;
            if (!string.IsNullOrEmpty(dl.QualityAudio)) _config.Download.QualityAudio = dl.QualityAudio;
            if (dl.DubLanguages != null && dl.DubLanguages.Count > 0) _config.Download.DubLanguages = dl.DubLanguages;
            else if (dl.DubLanguages != null && dl.DubLanguages.Count == 0) _config.Download.DubLanguages = new List<string>{ "ja-JP" }; // Prevent empty - default to Japanese
            if (!string.IsNullOrEmpty(dl.DefaultAudio)) _config.Download.DefaultAudio = dl.DefaultAudio;
            if (dl.DownloadDescriptionAudio.HasValue) _config.Download.DownloadDescriptionAudio = dl.DownloadDescriptionAudio.Value;
            if (dl.DownloadFirstAvailableDub.HasValue) _config.Download.DownloadFirstAvailableDub = dl.DownloadFirstAvailableDub.Value;
            if (dl.DlVideoOnce.HasValue) _config.Download.DlVideoOnce = dl.DlVideoOnce.Value;
            if (dl.KeepDubsSeparate.HasValue) _config.Download.KeepDubsSeparate = dl.KeepDubsSeparate.Value;
            if (dl.DubDownloadDelaySeconds.HasValue) _config.Download.DubDownloadDelaySeconds = dl.DubDownloadDelaySeconds.Value;
            if (dl.CooldownDelaySeconds.HasValue) _config.Download.CooldownDelaySeconds = dl.CooldownDelaySeconds.Value;
            if (!string.IsNullOrEmpty(dl.HardSubLang)) _config.Download.HardSubLang = dl.HardSubLang;
            if (dl.HardSubRawFallback.HasValue) _config.Download.HardSubRawFallback = dl.HardSubRawFallback.Value;
            if (dl.Kstream.HasValue) _config.Download.Kstream = dl.Kstream.Value;
            if (dl.StreamServer.HasValue) _config.Download.StreamServer = dl.StreamServer.Value;
            if (dl.SoftSubs != null && dl.SoftSubs.Count > 0) _config.Download.SoftSubs = dl.SoftSubs;
            else if (dl.SoftSubs != null && dl.SoftSubs.Count == 0) _config.Download.SoftSubs = new List<string>{ "en-US" }; // Prevent empty - default to English
            if (!string.IsNullOrEmpty(dl.DefaultSub)) _config.Download.DefaultSub = dl.DefaultSub;
            if (dl.IncludeSignsSubs.HasValue) _config.Download.IncludeSignsSubs = dl.IncludeSignsSubs.Value;
            if (dl.SignsSubsAsForced.HasValue) _config.Download.SignsSubsAsForced = dl.SignsSubsAsForced.Value;
            if (dl.IncludeCcSubs.HasValue) _config.Download.IncludeCcSubs = dl.IncludeCcSubs.Value;
            if (dl.CcSubsMuxingFlag.HasValue) _config.Download.CcSubsMuxingFlag = dl.CcSubsMuxingFlag.Value;
            if (dl.SubsDownloadDuplicate.HasValue) _config.Download.SubsDownloadDuplicate = dl.SubsDownloadDuplicate.Value;
            if (dl.FixCccSubtitles.HasValue) _config.Download.FixCccSubtitles = dl.FixCccSubtitles.Value;
            if (dl.Timeout.HasValue) _config.Download.Timeout = dl.Timeout.Value;
            if (dl.SkipSubs.HasValue) _config.Download.SkipSubs = dl.SkipSubs.Value;
            if (!string.IsNullOrEmpty(dl.CcTag)) _config.Download.CcTag = dl.CcTag;
            if (dl.ConvertVttToAss.HasValue) _config.Download.ConvertVttToAss = dl.ConvertVttToAss.Value;
            if (!string.IsNullOrEmpty(dl.CcSubsFont)) _config.Download.CcSubsFont = dl.CcSubsFont;
            if (!string.IsNullOrEmpty(dl.SubsAddScaledBorder)) _config.Download.SubsAddScaledBorder = dl.SubsAddScaledBorder;
            if (dl.SimultaneousDownloads.HasValue) _config.Download.SimultaneousDownloads = dl.SimultaneousDownloads.Value;
            if (dl.SimultaneousProcessingJobs.HasValue) _config.Download.SimultaneousProcessingJobs = dl.SimultaneousProcessingJobs.Value;
            if (dl.DownloadMethodeNew.HasValue) _config.Download.DownloadMethodeNew = dl.DownloadMethodeNew.Value;
            if (dl.DownloadAllowEarlyStart.HasValue) _config.Download.DownloadAllowEarlyStart = dl.DownloadAllowEarlyStart.Value;
            if (dl.DownloadOnlyWithAllSelectedDubSub.HasValue) _config.Download.DownloadOnlyWithAllSelectedDubSub = dl.DownloadOnlyWithAllSelectedDubSub.Value;
            if (dl.DownloadSpeedLimit.HasValue) _config.Download.DownloadSpeedLimit = dl.DownloadSpeedLimit.Value;
            if (dl.DownloadSpeedInBits.HasValue) _config.Download.DownloadSpeedInBits = dl.DownloadSpeedInBits.Value;
            if (dl.RetryAttempts.HasValue) _config.Download.RetryAttempts = dl.RetryAttempts.Value;
            if (dl.RetryDelay.HasValue) _config.Download.RetryDelay = dl.RetryDelay.Value;
            if (dl.PlaybackRateLimitRetryDelaySeconds.HasValue) _config.Download.PlaybackRateLimitRetryDelaySeconds = dl.PlaybackRateLimitRetryDelaySeconds.Value;
            if (dl.RetryMaxDelaySeconds.HasValue) _config.Download.RetryMaxDelaySeconds = dl.RetryMaxDelaySeconds.Value;
            if (dl.PartSize.HasValue) _config.Download.PartSize = dl.PartSize.Value;
            if (dl.NoVideo.HasValue) _config.Download.NoVideo = dl.NoVideo.Value;
            if (dl.NoAudio.HasValue) _config.Download.NoAudio = dl.NoAudio.Value;
            if (dl.IncludeChapters.HasValue) _config.Download.IncludeChapters = dl.IncludeChapters.Value;
            if (dl.SkipMuxing.HasValue) _config.Download.SkipMuxing = dl.SkipMuxing.Value;
            if (dl.MuxMp4.HasValue) _config.Download.MuxMp4 = dl.MuxMp4.Value;
            if (dl.MuxAudioOnlyToMp3.HasValue) _config.Download.MuxAudioOnlyToMp3 = dl.MuxAudioOnlyToMp3.Value;
            if (dl.SkipSubMux.HasValue) _config.Download.SkipSubMux = dl.SkipSubMux.Value;
            if (dl.MuxFonts.HasValue) _config.Download.MuxFonts = dl.MuxFonts.Value;
            if (dl.MuxTypesettingFonts.HasValue) _config.Download.MuxTypesettingFonts = dl.MuxTypesettingFonts.Value;
            if (dl.MuxCover.HasValue) _config.Download.MuxCover = dl.MuxCover.Value;
            if (!string.IsNullOrEmpty(dl.MuxDefaultDub)) _config.Download.MuxDefaultDub = dl.MuxDefaultDub;
            if (!string.IsNullOrEmpty(dl.MuxDefaultSub)) _config.Download.MuxDefaultSub = dl.MuxDefaultSub;
            if (dl.MuxDefaultSubSigns.HasValue) _config.Download.MuxDefaultSubSigns = dl.MuxDefaultSubSigns.Value;
            if (dl.MuxDefaultSubForcedDisplay.HasValue) _config.Download.MuxDefaultSubForcedDisplay = dl.MuxDefaultSubForcedDisplay.Value;
            if (!string.IsNullOrEmpty(dl.DefaultVideo)) _config.Download.DefaultVideo = dl.DefaultVideo;
            if (dl.ReplaceExistingFiles.HasValue) _config.Download.ReplaceExistingFiles = dl.ReplaceExistingFiles.Value;
            if (dl.SyncTiming.HasValue) _config.Download.SyncTiming = dl.SyncTiming.Value;
            if (dl.SyncTimingFullQualityFallback.HasValue) _config.Download.SyncTimingFullQualityFallback = dl.SyncTimingFullQualityFallback.Value;
            if (dl.SyncHwAccel != null) _config.Download.SyncHwAccel = dl.SyncHwAccel;
            if (dl.MkvmergeOptions != null) _config.Download.MkvmergeOptions = dl.MkvmergeOptions;
            if (dl.FfmpegOptions != null) _config.Download.FfmpegOptions = dl.FfmpegOptions;
            if (dl.EncodeEnabled.HasValue) _config.Download.EncodeEnabled = dl.EncodeEnabled.Value;
            if (dl.EncodingPreset != null) _config.Download.EncodingPreset = dl.EncodingPreset;
            if (dl.DownloadMultipleDubs.HasValue) _config.Download.DownloadMultipleDubs = dl.DownloadMultipleDubs.Value;
        }
        
        if (request.Queue != null){
            if (request.Queue.PersistQueue.HasValue) _config.Queue.PersistQueue = request.Queue.PersistQueue.Value;
            if (request.Queue.AutoDownload.HasValue) _config.Queue.AutoDownload = request.Queue.AutoDownload.Value;
            if (request.Queue.SimultaneousProcessingJobs.HasValue) _config.Queue.SimultaneousProcessingJobs = request.Queue.SimultaneousProcessingJobs.Value;
            if (!string.IsNullOrEmpty(request.Queue.QueueFilePath)) _config.Queue.QueueFilePath = request.Queue.QueueFilePath;
            if (request.Queue.ShutdownWhenQueueEmpty.HasValue) _config.Queue.ShutdownWhenQueueEmpty = request.Queue.ShutdownWhenQueueEmpty.Value;
        }
        
        if (request.History != null){
            var h = request.History;
            if (h.Enabled.HasValue) _config.History.Enabled = h.Enabled.Value;
            if (h.CountMissing.HasValue) _config.History.CountMissing = h.CountMissing.Value;
            if (h.IncludeCrArtists.HasValue) _config.History.IncludeCrArtists = h.IncludeCrArtists.Value;
            if (h.RemoveMissingEpisodes.HasValue) _config.History.RemoveMissingEpisodes = h.RemoveMissingEpisodes.Value;
            if (h.AddSpecials.HasValue) _config.History.AddSpecials = h.AddSpecials.Value;
            if (h.SkipUnmonitored.HasValue) _config.History.SkipUnmonitored = h.SkipUnmonitored.Value;
            if (h.CountSonarr.HasValue) _config.History.CountSonarr = h.CountSonarr.Value;
            if (!string.IsNullOrEmpty(h.Lang)) _config.History.Lang = h.Lang;
            if (h.AutoRefreshIntervalMinutes.HasValue) _config.History.AutoRefreshIntervalMinutes = h.AutoRefreshIntervalMinutes.Value;
            if (h.AutoRefreshMode.HasValue) _config.History.AutoRefreshMode = h.AutoRefreshMode.Value;
            if (h.AutoRefreshAddToQueue.HasValue) _config.History.AutoRefreshAddToQueue = h.AutoRefreshAddToQueue.Value;
        }
        
        if (request.TrackedSeriesReleaseLastCheckUtc.HasValue) _config.TrackedSeriesReleaseLastCheckUtc = request.TrackedSeriesReleaseLastCheckUtc.Value;
        if (request.HistoryPageProperties != null) _config.HistoryPageProperties = request.HistoryPageProperties;
        if (request.SeasonsPageProperties != null) _config.SeasonsPageProperties = request.SeasonsPageProperties;
        
        if (request.Notifications != null){
            var n = request.Notifications;
            if (n.WebhookUrl != null) _config.Notifications.WebhookUrl = n.WebhookUrl;
            if (n.WebhookEnabled.HasValue) _config.Notifications.WebhookEnabled = n.WebhookEnabled.Value;
            if (!string.IsNullOrEmpty(n.WebhookMethod)) _config.Notifications.WebhookMethod = n.WebhookMethod;
            if (!string.IsNullOrEmpty(n.WebhookContentType)) _config.Notifications.WebhookContentType = n.WebhookContentType;
            if (n.WebhookHeaders != null) _config.Notifications.WebhookHeaders = n.WebhookHeaders;
            if (n.WebhookBodyTemplate != null) _config.Notifications.WebhookBodyTemplate = n.WebhookBodyTemplate;
            if (n.NotifyQueueFinished.HasValue) _config.Notifications.NotifyQueueFinished = n.NotifyQueueFinished.Value;
            if (n.NotifyDownloadFinished.HasValue) _config.Notifications.NotifyDownloadFinished = n.NotifyDownloadFinished.Value;
            if (n.NotifyDownloadFailed.HasValue) _config.Notifications.NotifyDownloadFailed = n.NotifyDownloadFailed.Value;
            if (n.NotifyTrackedSeriesReleased.HasValue) _config.Notifications.NotifyTrackedSeriesReleased = n.NotifyTrackedSeriesReleased.Value;
            if (n.NotifyLoginExpired.HasValue) _config.Notifications.NotifyLoginExpired = n.NotifyLoginExpired.Value;
            if (n.NotifyUpdateAvailable.HasValue) _config.Notifications.NotifyUpdateAvailable = n.NotifyUpdateAvailable.Value;
            if (n.DownloadFinishedPlaySound.HasValue) _config.Notifications.DownloadFinishedPlaySound = n.DownloadFinishedPlaySound.Value;
            if (n.DownloadFinishedSoundPath != null) _config.Notifications.DownloadFinishedSoundPath = n.DownloadFinishedSoundPath;
            if (n.DownloadFinishedExecute.HasValue) _config.Notifications.DownloadFinishedExecute = n.DownloadFinishedExecute.Value;
            if (n.DownloadFinishedExecutePath != null) _config.Notifications.DownloadFinishedExecutePath = n.DownloadFinishedExecutePath;
        }
        
        if (request.Sonarr != null){
            var s = request.Sonarr;
            if (s.Enabled.HasValue) _config.Sonarr.Enabled = s.Enabled.Value;
            if (s.Host != null) _config.Sonarr.Host = s.Host;
            if (s.Port.HasValue) _config.Sonarr.Port = s.Port.Value;
            if (s.ApiKey != null) _config.Sonarr.ApiKey = s.ApiKey;
            if (s.UseSsl.HasValue) _config.Sonarr.UseSsl = s.UseSsl.Value;
            if (s.UrlBase != null) _config.Sonarr.UrlBase = s.UrlBase;
            if (s.UseSonarrNumbering.HasValue) _config.Sonarr.UseSonarrNumbering = s.UseSonarrNumbering.Value;
        }
        
        if (request.Proxy != null){
            var p = request.Proxy;
            if (p.Enabled.HasValue) _config.Proxy.Enabled = p.Enabled.Value;
            if (p.Socks.HasValue) _config.Proxy.Socks = p.Socks.Value;
            if (p.Host != null) _config.Proxy.Host = p.Host;
            if (p.Port.HasValue) _config.Proxy.Port = p.Port.Value;
            if (p.Username != null) _config.Proxy.Username = p.Username;
            if (p.Password != null) _config.Proxy.Password = p.Password;
        }
        
        if (request.FlareSolverr != null){
            var f = request.FlareSolverr;
            if (f.Enabled.HasValue) _config.FlareSolverr.Enabled = f.Enabled.Value;
            if (!string.IsNullOrEmpty(f.Host)) _config.FlareSolverr.Host = f.Host;
            if (f.Port.HasValue) _config.FlareSolverr.Port = f.Port.Value;
            if (f.UseSsl.HasValue) _config.FlareSolverr.UseSsl = f.UseSsl.Value;
            if (f.MitmEnabled.HasValue) _config.FlareSolverr.MitmEnabled = f.MitmEnabled.Value;
            if (!string.IsNullOrEmpty(f.MitmHost)) _config.FlareSolverr.MitmHost = f.MitmHost;
            if (f.MitmPort.HasValue) _config.FlareSolverr.MitmPort = f.MitmPort.Value;
            if (f.MitmUseSsl.HasValue) _config.FlareSolverr.MitmUseSsl = f.MitmUseSsl.Value;
        }
        
        if (request.Calendar != null){
            var c = request.Calendar;
            if (!string.IsNullOrEmpty(c.Language)) _config.Calendar.Language = c.Language;
            if (!string.IsNullOrEmpty(c.DubFilter)) _config.Calendar.DubFilter = c.DubFilter;
            if (c.Custom.HasValue) _config.Calendar.Custom = c.Custom.Value;
            if (c.HideDubs.HasValue) _config.Calendar.HideDubs = c.HideDubs.Value;
            if (c.ShowUpcomingEpisodes.HasValue) _config.Calendar.ShowUpcomingEpisodes = c.ShowUpcomingEpisodes.Value;
            if (c.UpdateHistory.HasValue) _config.Calendar.UpdateHistory = c.UpdateHistory.Value;
        }
        
        if (request.Appearance != null){
            var a = request.Appearance;
            if (!string.IsNullOrEmpty(a.Theme)) _config.Appearance.Theme = a.Theme;
            if (a.AccentColor != null) _config.Appearance.AccentColor = a.AccentColor;
            if (a.BackgroundImagePath != null) _config.Appearance.BackgroundImagePath = a.BackgroundImagePath;
            if (a.BackgroundImageOpacity.HasValue) _config.Appearance.BackgroundImageOpacity = a.BackgroundImageOpacity.Value;
            if (a.BackgroundImageBlurRadius.HasValue) _config.Appearance.BackgroundImageBlurRadius = a.BackgroundImageBlurRadius.Value;
        }
        
        if (request.General != null){
            if (request.General.LogMode.HasValue) _config.LogMode = request.General.LogMode.Value;
            if (request.General.RemoveFinishedDownload.HasValue) _config.RemoveFinishedDownload = request.General.RemoveFinishedDownload.Value;
            if (request.General.TokenFilePath != null) _config.TokenFilePath = request.General.TokenFilePath;
        }
    }
}

public class ConfigUpdateRequest{
    public CrunchyrollUpdateConfig? Crunchyroll { get; set; }
    public DownloadUpdateConfig? Download { get; set; }
    public QueueUpdateConfig? Queue { get; set; }
    public HistoryUpdateConfig? History { get; set; }
    public NotificationsUpdateConfig? Notifications { get; set; }
    public SonarrUpdateConfig? Sonarr { get; set; }
    public ProxyUpdateConfig? Proxy { get; set; }
    public FlareSolverrUpdateConfig? FlareSolverr { get; set; }
    public CalendarUpdateConfig? Calendar { get; set; }
    public AppearanceUpdateConfig? Appearance { get; set; }
    public GeneralUpdateConfig? General { get; set; }
    public DateTime? TrackedSeriesReleaseLastCheckUtc { get; set; }
    public HistoryPageProperties? HistoryPageProperties { get; set; }
    public SeasonsPageProperties? SeasonsPageProperties { get; set; }
}

public class CrunchyrollUpdateConfig{
    public string? Email { get; set; }
    public bool? UseBetaApi { get; set; }
    public bool? MarkAsWatched { get; set; }
    public bool? SearchFetchFeaturedMusic { get; set; }
    public StreamEndpointConfig? StreamEndpoint { get; set; }
    public StreamEndpointConfig? StreamEndpointSecondary { get; set; }
}

public class DownloadUpdateConfig{
    public string? OutputDirectory { get; set; }
    public string? TempDirectory { get; set; }
    public bool? UseTempFolder { get; set; }
    public string? Filename { get; set; }
    public string? FilenameTemplate { get; set; }
    public string? FilenameWhitespaceSubstitute { get; set; }
    public string? VideoTitle { get; set; }
    public bool? IncludeVideoDescription { get; set; }
    public string? DescriptionLang { get; set; }
    public int? LeadingNumbers { get; set; }
    public string? QualityVideo { get; set; }
    public string? QualityAudio { get; set; }
    public List<string>? DubLanguages { get; set; }
    public string? DefaultAudio { get; set; }
    public bool? DownloadDescriptionAudio { get; set; }
    public bool? DownloadFirstAvailableDub { get; set; }
    public bool? DlVideoOnce { get; set; }
    public bool? KeepDubsSeparate { get; set; }
    public int? DubDownloadDelaySeconds { get; set; }
    public int? CooldownDelaySeconds { get; set; }
    public string? HardSubLang { get; set; }
    public bool? HardSubRawFallback { get; set; }
    public int? Kstream { get; set; }
    public int? StreamServer { get; set; }
    public List<string>? SoftSubs { get; set; }
    public string? DefaultSub { get; set; }
    public bool? IncludeSignsSubs { get; set; }
    public bool? SignsSubsAsForced { get; set; }
    public bool? IncludeCcSubs { get; set; }
    public bool? CcSubsMuxingFlag { get; set; }
    public bool? SubsDownloadDuplicate { get; set; }
    public bool? FixCccSubtitles { get; set; }
    public int? Timeout { get; set; }
    public bool? SkipSubs { get; set; }
    public string? CcTag { get; set; }
    public bool? ConvertVttToAss { get; set; }
    public string? CcSubsFont { get; set; }
    public string? SubsAddScaledBorder { get; set; }
    public int? SimultaneousDownloads { get; set; }
    public int? SimultaneousProcessingJobs { get; set; }
    public bool? DownloadMethodeNew { get; set; }
    public bool? DownloadAllowEarlyStart { get; set; }
    public bool? DownloadOnlyWithAllSelectedDubSub { get; set; }
    public int? DownloadSpeedLimit { get; set; }
    public bool? DownloadSpeedInBits { get; set; }
    public int? RetryAttempts { get; set; }
    public int? RetryDelay { get; set; }
    public int? PlaybackRateLimitRetryDelaySeconds { get; set; }
    public int? RetryMaxDelaySeconds { get; set; }
    public int? PartSize { get; set; }
    public bool? NoVideo { get; set; }
    public bool? NoAudio { get; set; }
    public bool? IncludeChapters { get; set; }
    public bool? SkipMuxing { get; set; }
    public bool? MuxMp4 { get; set; }
    public bool? MuxAudioOnlyToMp3 { get; set; }
    public bool? SkipSubMux { get; set; }
    public bool? MuxFonts { get; set; }
    public bool? MuxTypesettingFonts { get; set; }
    public bool? MuxCover { get; set; }
    public string? MuxDefaultDub { get; set; }
    public string? MuxDefaultSub { get; set; }
    public bool? MuxDefaultSubSigns { get; set; }
    public bool? MuxDefaultSubForcedDisplay { get; set; }
    public string? DefaultVideo { get; set; }
    public bool? ReplaceExistingFiles { get; set; }
    public bool? SyncTiming { get; set; }
    public bool? SyncTimingFullQualityFallback { get; set; }
    public string? SyncHwAccel { get; set; }
    public List<string>? MkvmergeOptions { get; set; }
    public List<string>? FfmpegOptions { get; set; }
    public bool? EncodeEnabled { get; set; }
    public string? EncodingPreset { get; set; }
    public bool? DownloadMultipleDubs { get; set; }
}

public class QueueUpdateConfig{
    public bool? PersistQueue { get; set; }
    public bool? AutoDownload { get; set; }
    public int? SimultaneousProcessingJobs { get; set; }
    public string? QueueFilePath { get; set; }
    public bool? ShutdownWhenQueueEmpty { get; set; }
}

public class HistoryUpdateConfig{
    public bool? Enabled { get; set; }
    public bool? CountMissing { get; set; }
    public bool? IncludeCrArtists { get; set; }
    public bool? RemoveMissingEpisodes { get; set; }
    public bool? AddSpecials { get; set; }
    public bool? SkipUnmonitored { get; set; }
    public bool? CountSonarr { get; set; }
    public string? Lang { get; set; }
    public int? AutoRefreshIntervalMinutes { get; set; }
    public int? AutoRefreshMode { get; set; }
    public bool? AutoRefreshAddToQueue { get; set; }
}

public class NotificationsUpdateConfig{
    public string? WebhookUrl { get; set; }
    public bool? WebhookEnabled { get; set; }
    public string? WebhookMethod { get; set; }
    public string? WebhookContentType { get; set; }
    public Dictionary<string, string>? WebhookHeaders { get; set; }
    public string? WebhookBodyTemplate { get; set; }
    public bool? NotifyQueueFinished { get; set; }
    public bool? NotifyDownloadFinished { get; set; }
    public bool? NotifyDownloadFailed { get; set; }
    public bool? NotifyTrackedSeriesReleased { get; set; }
    public bool? NotifyLoginExpired { get; set; }
    public bool? NotifyUpdateAvailable { get; set; }
    public bool? DownloadFinishedPlaySound { get; set; }
    public string? DownloadFinishedSoundPath { get; set; }
    public bool? DownloadFinishedExecute { get; set; }
    public string? DownloadFinishedExecutePath { get; set; }
}

public class SonarrUpdateConfig{
    public bool? Enabled { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? ApiKey { get; set; }
    public bool? UseSsl { get; set; }
    public string? UrlBase { get; set; }
    public bool? UseSonarrNumbering { get; set; }
}

public class ProxyUpdateConfig{
    public bool? Enabled { get; set; }
    public bool? Socks { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class FlareSolverrUpdateConfig{
    public bool? Enabled { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseSsl { get; set; }
    public bool? MitmEnabled { get; set; }
    public string? MitmHost { get; set; }
    public int? MitmPort { get; set; }
    public bool? MitmUseSsl { get; set; }
}

public class CalendarUpdateConfig{
    public string? Language { get; set; }
    public string? DubFilter { get; set; }
    public bool? Custom { get; set; }
    public bool? HideDubs { get; set; }
    public bool? ShowUpcomingEpisodes { get; set; }
    public bool? UpdateHistory { get; set; }
}

public class AppearanceUpdateConfig{
    public string? Theme { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundImagePath { get; set; }
    public double? BackgroundImageOpacity { get; set; }
    public double? BackgroundImageBlurRadius { get; set; }
}

public class GeneralUpdateConfig{
    public bool? LogMode { get; set; }
    public bool? RemoveFinishedDownload { get; set; }
    public string? TokenFilePath { get; set; }
}

public class WebhookTestRequest{
    public string Url { get; set; } = "";
}
