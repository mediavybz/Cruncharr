using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.DRM;
using Cruncharr.Core.Utils.HLS;
using Cruncharr.Core.Utils.Muxing;
using Cruncharr.Core.Utils.Muxing.Structs;
using Cruncharr.Core.Utils.Muxing.Syncing;
using Cruncharr.Core.Utils.Parser;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

public interface IDownloadService
{
    Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, Action? onDownloadComplete = null);
    Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

public class DownloadService : IDownloadService
{
    // Dedupe post-download full-series enrichment so a batch download of many episodes of the same
    // show triggers the heavy CR series-fetch + Sonarr match once, not once per episode.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSeriesEnrich = new();

    private readonly ILogger<DownloadService>? _logger;
    private readonly ICrunchyrollAuthService _auth;
    private readonly ICrunchyrollApiService _api;
    private readonly HttpClientWrapper _httpClient;
    private readonly WidevineCdm _widevine;
    private readonly IChapterService _chapterService;
    private readonly IFontService _fontService;
    private readonly IFilenameService _filenameService;
    private readonly IVideoSyncer? _videoSyncer;
    private readonly IEncodingService? _encodingService;

    private readonly IHistoryService? _history;
    private readonly IQueueService? _queueService;
    private readonly ISonarrService? _sonarrService;

    public DownloadService(ICrunchyrollAuthService auth, ICrunchyrollApiService api, ILogger<DownloadService>? logger = null, IHistoryService? history = null, IVideoSyncer? videoSyncer = null, IEncodingService? encodingService = null, IQueueService? queueService = null, ISonarrService? sonarrService = null)
    {
        _auth = auth;
        _api = api;
        _logger = logger;
        _history = history;
        _videoSyncer = videoSyncer;
        _encodingService = encodingService;
        _queueService = queueService;
        _sonarrService = sonarrService;
        _httpClient = auth.HttpClient;
        // Use /widevine for Docker, fallback to default path
        var widevineDir = "/widevine";
        if (!Directory.Exists(widevineDir))
        {
            widevineDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cruncharr", "widevine");
        }
        _widevine = new WidevineCdm(widevineDir);
        _chapterService = new ChapterService(_httpClient, null);
        _fontService = new FontService(null);
        _filenameService = new FilenameService();
    }

    /// <summary>
    /// REGRESSION GUARD — do not weaken without a replacement. Decides whether to re-fetch the
    /// episode's versions from Crunchyroll instead of trusting whatever the caller posted.
    ///
    /// A specific dub's correct stream id (the per-version MediaGuid) ONLY exists in a fresh CR
    /// fetch. Client-built versions (e.g. the Add Download flow) carry the BASE episode guid for
    /// every dub and no MediaGuid, so trusting them streams the ORIGINAL (ja-JP) audio under the
    /// requested dub's label ("English label, Japanese audio"). So: always refetch when versions
    /// are absent OR a specific dub was requested. Keeps every add-path resolving the SAME correct
    /// per-dub stream. See memory: download-mux-gotchas (Add Download versions desync, beta.120).
    /// </summary>
    // Resolve the id used to GROUP a download under a series in History. Prefer the real CR series
    // id, then the series title (stable across a show's episodes), and only as a last resort the
    // per-episode Guid. Using the Guid before the title made every download its own one-episode
    // "series" (the History grouping bug). Returns null only when the episode carries no identity.
    internal static string? ResolveRichSeriesId(EpisodeInfo episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.SeriesId)) return episode.SeriesId;
        if (!string.IsNullOrWhiteSpace(episode.SeriesTitle)) return episode.SeriesTitle;
        return string.IsNullOrWhiteSpace(episode.Guid) ? null : episode.Guid;
    }

    public static bool ShouldRefetchVersions(EpisodeInfo episode)
    {
        if (episode.Versions == null || episode.Versions.Count == 0) return true;
        // A dub was explicitly requested -> never trust posted versions; the real per-dub
        // MediaGuid must come from Crunchyroll.
        if (episode.SelectedDubs != null && episode.SelectedDubs.Any(d => !string.IsNullOrWhiteSpace(d))) return true;
        return false;
    }

    /// <summary>Make a series/season string safe as a single path segment (strip invalid chars,
    /// collapse whitespace, drop trailing dots/spaces which are illegal on Windows).</summary>
    private static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? ' ' : c);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
    }

    public async Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, Action? onDownloadComplete = null)
    {
        _logger?.LogInformation("Starting download: {EpisodeId} - {Title}", episode.Id, episode.Title);
        progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 0, Doing = "Authenticating..." });

        // Authenticate (use beta API)
        try
        {
            if (!await _auth.AuthenticateAsync(true, cancellationToken))
            {
                return new DownloadResult { Success = false, ErrorMessage = "Authentication failed. Please log in to your Crunchyroll account.", ErrorType = DownloadErrorType.NotAuthenticated };
            }
        }
        catch (Exception ex)
        {
            return new DownloadResult { Success = false, ErrorMessage = $"Authentication error: {ex.Message}", ErrorType = DownloadErrorType.NotAuthenticated };
        }

        // Fetch full episode details when we need fresh per-dub versions OR when the episode is
        // missing the series identity used to group it in History. A queue-added episode often
        // carries only an id, so without fetching series_id/series_title every download would land
        // under a per-episode id and show as its own "Episode N" row instead of nesting under the
        // show (upstream groups by the real CR series id).
        // [PT] Using ParseEpisodeByIdAsync instead of GetEpisodeAsync to get version deduplication.
        bool needVersions = ShouldRefetchVersions(episode);
        bool needSeriesMeta = string.IsNullOrWhiteSpace(episode.SeriesId) || string.IsNullOrWhiteSpace(episode.SeriesTitle);
        if (needVersions || needSeriesMeta)
        {
            progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 10, Doing = "Fetching episode info..." });
            var fullEpisode = await _api.ParseEpisodeByIdAsync(episode.Id, null, false, cancellationToken);
            if (fullEpisode != null)
            {
                if (needVersions)
                {
                    episode.Versions = fullEpisode.Versions;
                    episode.AudioLocale = fullEpisode.AudioLocale;
                }
                episode.Guid = fullEpisode.Guid ?? episode.Guid;
                if (!string.IsNullOrEmpty(fullEpisode.SeriesId)) episode.SeriesId = fullEpisode.SeriesId;
                if (!string.IsNullOrEmpty(fullEpisode.SeriesTitle)) episode.SeriesTitle = fullEpisode.SeriesTitle;
                if (!string.IsNullOrEmpty(fullEpisode.SeasonId)) episode.SeasonId = fullEpisode.SeasonId;
                if (!string.IsNullOrEmpty(fullEpisode.SeasonTitle)) episode.SeasonTitle = fullEpisode.SeasonTitle;
                if (fullEpisode.SeasonNumber > 0 && episode.SeasonNumber <= 0) episode.SeasonNumber = fullEpisode.SeasonNumber;
                if (fullEpisode.EpisodeNumber > 0 && episode.EpisodeNumber <= 0) episode.EpisodeNumber = fullEpisode.EpisodeNumber;
                if (!string.IsNullOrEmpty(fullEpisode.Episode)) episode.Episode = fullEpisode.Episode;
                _logger?.LogInformation("Fetched episode details: {EpisodeId}, Versions={VersionCount}, Series={Series}, Season={Season}",
                    fullEpisode.Id, fullEpisode.Versions?.Count ?? 0, fullEpisode.SeriesTitle, fullEpisode.SeasonTitle);
            }
            else
            {
                _logger?.LogWarning("Failed to fetch full episode details for {EpisodeId}", episode.Id);
            }
        }

        // Check if episode has all selected dubs/subs before downloading
        if (config.Download.DownloadOnlyWithAllSelectedDubSub)
        {
            var availableDubs = episode.Versions?.Select(v => v.AudioLocale).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            // Use episode's SelectedDubs if set, otherwise fall back to config
            var requiredDubs = episode.SelectedDubs?.Count > 0
                ? episode.SelectedDubs
                : config.Download.DubLanguages;
            var missingDubs = requiredDubs
                .Where(d => !availableDubs.Contains(d, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var availableSubs = episode.SubtitleLocales ?? new List<string>();
            // Use episode's SelectedSubs if set, otherwise fall back to config
            var requiredSubs = episode.SelectedSubs?.Count > 0
                ? episode.SelectedSubs
                : config.Download.SoftSubs;
            var missingSubs = requiredSubs
                .Where(s => !availableSubs.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missingDubs.Count > 0 || missingSubs.Count > 0)
            {
                var reasons = new List<string>();
                if (missingDubs.Count > 0) reasons.Add($"missing dubs: {string.Join(", ", missingDubs)}");
                if (missingSubs.Count > 0) reasons.Add($"missing subs: {string.Join(", ", missingSubs)}");
                var message = $"Skipping download - episode does not have all selected languages ({string.Join("; ", reasons)})";
                _logger?.LogWarning(message);
                return new DownloadResult { Success = false, ErrorMessage = message };
            }
        }

        // Select correct episode version based on DubLanguages (ported from upstream CrunchyrollManager.DownloadMediaList)
        // Upstream sorts data.Data by DubLang priority, then processes each version
        // Default to episode.Id (the actual version ID for this language)
        string mediaGuid = episode.Id;
        string mediaId = episode.Id;

        _logger?.LogInformation("Episode {EpisodeId} has {VersionCount} versions", episode.Id, episode.Versions?.Count ?? 0);
        if (episode.Versions != null)
        {
            foreach (var v in episode.Versions)
            {
                _logger?.LogDebug("Version: Guid={Guid}, MediaGuid={MediaGuid}, AudioLocale={AudioLocale}, Original={Original}", v.Guid, v.MediaGuid, v.AudioLocale, v.Original);
            }
        }

        if (episode.Versions != null && episode.Versions.Count > 0)
        {
            EpisodeVersion? currentVersion = null;
            EpisodeVersion? primaryVersion = null;

            // Ported from upstream: find version matching episode's language
            if (!string.IsNullOrEmpty(episode.AudioLocale))
            {
                currentVersion = episode.Versions.FirstOrDefault(v =>
                    string.Equals(v.AudioLocale, episode.AudioLocale, StringComparison.OrdinalIgnoreCase));
            }

            // Use episode's SelectedDubs if set (from queue item), otherwise fall back to config
            var dubLangs = episode.SelectedDubs?.Count > 0
                ? episode.SelectedDubs
                : config.Download.DubLanguages;

            // DownloadFirstAvailableDub: ignore the AudioLocale shortcut and pick strictly
            // by requested priority so we end up with the first *available* requested dub.
            if (currentVersion == null ||
                config.Download.DownloadFirstAvailableDub ||
                (dubLangs.Count > 0 && !dubLangs.Any(d => d.Equals(episode.AudioLocale, StringComparison.OrdinalIgnoreCase))))
            {

                // Try each DubLanguage in order
                foreach (var dubLang in dubLangs)
                {
                    var matchingVersion = episode.Versions.FirstOrDefault(v =>
                        string.Equals(v.AudioLocale, dubLang, StringComparison.OrdinalIgnoreCase));
                    if (matchingVersion != null)
                    {
                        currentVersion = matchingVersion;
                        _logger?.LogInformation("SelectedDubs override: selected {DubLang} version instead of {OriginalLocale}",
                            dubLang, episode.AudioLocale);
                        break;
                    }
                }
            }

            // Fallback: try config's default audio
            if (currentVersion == null && !string.IsNullOrEmpty(config.Download.DefaultAudio))
            {
                currentVersion = episode.Versions.FirstOrDefault(v =>
                    string.Equals(v.AudioLocale, config.Download.DefaultAudio, StringComparison.OrdinalIgnoreCase));
            }

            // Fallback: if only one version, use it
            if (currentVersion == null && episode.Versions.Count == 1)
            {
                currentVersion = episode.Versions[0];
            }

            // Fallback: use original version
            if (currentVersion == null)
            {
                currentVersion = episode.Versions.FirstOrDefault(v => v.Original) ?? episode.Versions[0];
            }

            if (currentVersion != null)
            {
                mediaGuid = currentVersion.Guid;
                if (!string.IsNullOrEmpty(currentVersion.MediaGuid))
                {
                    mediaId = currentVersion.MediaGuid;
                }

                // Track if this is the primary (original) version
                bool isPrimary = currentVersion.Original;
                if (!isPrimary)
                {
                    primaryVersion = episode.Versions.FirstOrDefault(v => v.Original) ?? currentVersion;
                }
                else
                {
                    primaryVersion = currentVersion;
                }

                _logger?.LogInformation("Selected version: Guid={Guid}, MediaGuid={MediaGuid}, audio_locale={AudioLocale}, original={Original}, isPrimary={IsPrimary}",
                    currentVersion.Guid, currentVersion.MediaGuid, currentVersion.AudioLocale, currentVersion.Original, isPrimary);
            }
            else
            {
                _logger?.LogWarning("Could not find matching version for audio locale {AudioLocale}, using default episode.Id", episode.AudioLocale);
            }
        }

        // Strip any prefix from mediaId/mediaGuid
        if (!string.IsNullOrEmpty(mediaId) && mediaId.Contains(':'))
        {
            mediaId = mediaId.Split(':')[1];
        }
        if (!string.IsNullOrEmpty(mediaGuid) && mediaGuid.Contains(':'))
        {
            mediaGuid = mediaGuid.Split(':')[1];
        }

        _logger?.LogInformation("Using mediaGuid={MediaGuid}, mediaId={MediaId} for playback API", mediaGuid, mediaId);

        // Get playback data (use beta API)
        progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 20, Doing = "Fetching playback data..." });
        var playbackData = await GetPlaybackDataAsync(mediaGuid, true, cancellationToken, config);
        if (playbackData == null)
        {
            return new DownloadResult { Success = false, ErrorMessage = "Failed to fetch playback data" };
        }

        // Fetch DRM keys if needed
        List<ContentKey>? decryptionKeys = null;

        // For DASH, PSSH might be in the manifest instead of the JSON response
        string? pssh = playbackData.Pssh;
        if (string.IsNullOrEmpty(pssh) && playbackData.VideoUrl?.Contains(".mpd") == true)
        {
            _logger?.LogInformation("No PSSH in playback data, trying to extract from DASH manifest...");
            var manifestRequest = new HttpRequestMessage(HttpMethod.Get, playbackData.VideoUrl);
            manifestRequest.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");
            var (manifestOk, manifestContent, _) = await _httpClient.SendRequestAsync(manifestRequest);
            if (manifestOk && !string.IsNullOrEmpty(manifestContent))
            {
                var manifest = await DashDownloader.ParseManifestAsync(manifestContent, playbackData.VideoUrl, _httpClient.Client);
                pssh = manifest.VideoTracks.FirstOrDefault()?.Pssh ?? manifest.AudioTracks.FirstOrDefault()?.Pssh;
                _logger?.LogInformation("PSSH from manifest: {Pssh}", pssh ?? "(null)");
            }
        }

        // Select stream based on HardSubLang setting (ported from upstream DownloadMediaList)
        var streamSelection = SelectStreamWithHardsub(playbackData, config);
        if (!streamSelection.Success)
        {
            return new DownloadResult { Success = false, ErrorMessage = streamSelection.ErrorMessage };
        }

        _logger?.LogInformation("PSSH: {Pssh}", pssh ?? "(null)");
        if (!string.IsNullOrEmpty(pssh) && _widevine.CanDecrypt)
        {
            progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 25, Doing = "Fetching decryption keys..." });
            // Refresh token before license request (matches desktop source behavior)
            await _auth.RefreshTokenAsync(true, cancellationToken);
            var authData = new Dictionary<string, string>{
                { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                { "x-cr-content-id", mediaGuid },
                { "x-cr-video-token", playbackData.VideoToken ?? "" }
            };
            _logger?.LogInformation("Fetching Widevine keys for PSSH: {Pssh}", pssh);
            decryptionKeys = await _widevine.GetKeysAsync(pssh, ApiUrls.WidevineLicenceUrl, authData, _httpClient.Client);
            _logger?.LogInformation("Got {Count} decryption keys", decryptionKeys.Count);
            if (decryptionKeys.Count == 0)
            {
                _logger?.LogWarning("Failed to get decryption keys, stream may be undecryptable");
            }
        }
        else
        {
            _logger?.LogWarning("Skipping decryption - PSSH: {HasPssh}, CanDecrypt: {CanDecrypt}", !string.IsNullOrEmpty(playbackData.Pssh), _widevine.CanDecrypt);
        }

        // Prepare output path
        var outputDir = config.Download.OutputDirectory;
        Directory.CreateDirectory(outputDir);

        // Fetch Sonarr data for filename variables if enabled
        SonarrSeries? sonarrSeries = null;
        SonarrEpisode? sonarrEpisode = null;
        if (config.Sonarr.Enabled && _sonarrService != null)
        {
            try
            {
                sonarrSeries = await _sonarrService.GetSeriesByTitleAsync(episode.SeriesTitle, config.Sonarr);
                if (sonarrSeries != null)
                {
                    var sonarrEpisodes = await _sonarrService.GetEpisodesAsync(sonarrSeries.Id, config.Sonarr);
                    // CR and Sonarr/TVDB often number episodes differently: CR groups long-running
                    // anime into a few "seasons" with CONTINUOUS episode numbers, while Sonarr/TVDB
                    // splits them into many seasons. So (season, episode) equality usually misses and
                    // UseSonarrNumbering silently fell back to CR numbers (e.g. CR Fairy Tail S3E278 ->
                    // Sonarr has it as S8E1, absoluteEpisodeNumber 278). Try, in order: exact
                    // season+episode, then absolute number (CR episode no. == Sonarr absolute), then
                    // air date.
                    sonarrEpisode =
                        sonarrEpisodes.FirstOrDefault(ep => ep.SeasonNumber == episode.SeasonNumber && ep.EpisodeNumber == episode.EpisodeNumber)
                        ?? sonarrEpisodes.FirstOrDefault(ep => ep.AbsoluteEpisodeNumber > 0 && ep.AbsoluteEpisodeNumber == episode.EpisodeNumber)
                        ?? (episode.ReleaseDate.HasValue
                            ? sonarrEpisodes.FirstOrDefault(ep => ep.AirDateUtc != default && ep.AirDateUtc.UtcDateTime.Date == episode.ReleaseDate.Value.Date)
                            : null);
                    _logger?.LogInformation("Sonarr match: {SeriesTitle} CR S{CrS}E{CrE} -> Sonarr S{SnS}E{SnE} (abs {Abs}) \"{EpTitle}\"",
                        sonarrSeries.Title, episode.SeasonNumber, episode.EpisodeNumber,
                        sonarrEpisode?.SeasonNumber, sonarrEpisode?.EpisodeNumber, sonarrEpisode?.AbsoluteEpisodeNumber, sonarrEpisode?.Title);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch Sonarr data for filename variables");
            }
        }

        // Use FilenameTemplate if user configured it, otherwise fall back to Filename
        var filenameTemplate = !string.IsNullOrEmpty(config.Download.FilenameTemplate) &&
                               config.Download.FilenameTemplate != "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}"
            ? config.Download.FilenameTemplate
            : config.Download.Filename;

        var filenameOptions = new FilenameOptions
        {
            NumberPadding = config.Download.LeadingNumbers,
            WhitespaceReplace = config.Download.FilenameWhitespaceSubstitute,
            Quality = config.Download.QualityVideo,
            AudioLanguage = config.Download.DefaultAudio,
            SonarrSeries = sonarrSeries,
            SonarrEpisode = sonarrEpisode,
            UseSonarrNumbering = config.Sonarr.UseSonarrNumbering,
            SelectedDubs = episode.SelectedDubs?.Count > 0
                ? episode.SelectedDubs
                : config.Download.DubLanguages
        };
        var fileName = _filenameService.FormatFilename(filenameTemplate, episode, filenameOptions);

        // Organize into <Series Title>/Season NN/ folders (Sonarr/Plex layout) when enabled. The
        // season mirrors what the FILENAME uses (Sonarr numbering when active) so folder + name
        // agree. Without this every show dumps into the download root.
        if (config.Download.OrganizeIntoFolders && !string.IsNullOrWhiteSpace(episode.SeriesTitle))
        {
            var folderSeason = (config.Sonarr.UseSonarrNumbering && sonarrEpisode != null)
                ? sonarrEpisode.SeasonNumber
                : episode.SeasonNumber;
            var seriesFolder = SanitizeFolderName(episode.SeriesTitle);
            if (!string.IsNullOrEmpty(seriesFolder))
            {
                outputDir = Path.Combine(outputDir, seriesFolder, $"Season {folderSeason:00}");
            }
        }

        string outputExtension;
        if (config.Download.MuxAudioOnlyToMp3 && config.Download.NoVideo)
        {
            outputExtension = ".mp3";
        }
        else if (config.Download.MuxMp4)
        {
            outputExtension = ".mp4";
        }
        else
        {
            outputExtension = ".mkv";
        }
        var outputPath = Path.Combine(outputDir, fileName + outputExtension);

        // Ensure the full output directory exists. Covers the Series/Season folders above AND any
        // subfolders a user put in their filename template (previously a template with "/" wrote to
        // a non-existent dir and failed at mux time).
        var outputParentDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputParentDir)) Directory.CreateDirectory(outputParentDir);

        // Replace existing file if configured
        if (config.Download.ReplaceExistingFiles && File.Exists(outputPath))
        {
            _logger?.LogInformation("Replacing existing file: {OutputPath}", outputPath);
            File.Delete(outputPath);
        }

        // Download streams. Always use a unique per-download working directory for the
        // intermediate segment/audio/sub files. Previously, with UseTempFolder=false the
        // temp files went straight into the output dir with FIXED names (video.enc.m4s,
        // audio_<lang>.m4s, ...), so two concurrent downloads (SimultaneousDownloads>1)
        // collided and corrupted each other. The final muxed file still lands in outputDir.
        var tempBase = config.Download.UseTempFolder ? config.Download.TempDirectory : outputDir;
        var tempDir = Path.Combine(tempBase, ".crtmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // When the temp folder is enabled, also mux + encode INSIDE tempDir (which a user can
        // point at a tmpfs/RAM disk) and only move the finished file to the output volume at the
        // end. This keeps the heavy mux/transcode read-write off the output SSD. With the temp
        // folder disabled, mux/encode write straight to the output dir as before (no extra move).
        var transcodeInTemp = config.Download.UseTempFolder;

        try
        {
            var downloadedFiles = new List<string>();
            var audioTrackLanguages = new List<(string Path, string Lang)>();
            var syncVideos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var videoLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Track video file -> locale mapping

            // Handle DASH manifest (contains both video and audio)
            if (playbackData.VideoUrl != null && (playbackData.VideoUrl.Contains(".mpd") || playbackData.VideoUrl.Contains("/dash/")))
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 30, Doing = "Downloading DASH streams..." });
                var (videoPath, audioPaths) = await DownloadDashTracksAsync(playbackData.VideoUrl, tempDir, config, progress, 30, 78, cancellationToken, playbackData.VideoToken, mediaId, episode.SelectedDubs);
                if (videoPath != null && !config.Download.NoVideo)
                {
                    downloadedFiles.Add(videoPath);
                    videoLocales[videoPath] = episode.AudioLocale;
                }
                foreach (var (path, _) in audioPaths)
                {
                    downloadedFiles.Add(path);
                }
                audioTrackLanguages = audioPaths;

                // [PT] Multi-dub for DASH: Crunchyroll's per-version manifest only contains
                // that version's audio, so each additional selected dub must be fetched from
                // its own version's playback (the HLS branch below does the equivalent). Without
                // this, selecting e.g. English + Japanese only ever downloaded the primary dub.
                var dashSelectedDubs = episode.SelectedDubs?.Count > 0
                    ? episode.SelectedDubs
                    : config.Download.DubLanguages;
                // DownloadFirstAvailableDub limits the result to a single (first-available) dub,
                // so skip downloading any additional dubs when it is enabled.
                if (!config.Download.NoAudio && !config.Download.DownloadFirstAvailableDub &&
                    episode.Versions != null && episode.Versions.Count > 1 &&
                    (config.Download.DownloadMultipleDubs || dashSelectedDubs.Count > 1))
                {
                    var primaryLocale = episode.AudioLocale;
                    var extraDubs = dashSelectedDubs
                        .Where(d => !string.IsNullOrEmpty(d) && !string.Equals(d, primaryLocale, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(d => !audioTrackLanguages.Any(a => string.Equals(a.Lang, d, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    foreach (var dub in extraDubs)
                    {
                        var dubVersion = episode.Versions.FirstOrDefault(v =>
                            string.Equals(v.AudioLocale, dub, StringComparison.OrdinalIgnoreCase));
                        if (dubVersion == null) continue;

                        var dubGuid = dubVersion.Guid;
                        if (!string.IsNullOrEmpty(dubGuid) && dubGuid.Contains(':')) dubGuid = dubGuid.Split(':')[1];
                        if (string.IsNullOrEmpty(dubGuid)) continue;

                        try
                        {
                            progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 78, Doing = $"Downloading audio ({dub})..." });
                            var dubPlayback = await GetPlaybackDataAsync(dubGuid, true, cancellationToken, config);

                            // Merge this version's subtitles into the pool. The primary
                            // playback is often a dub whose (TV) endpoint omits most subs,
                            // while the original/sub version carries the full set - so union
                            // subtitles across every fetched version before downloading them.
                            if (dubPlayback?.Subtitles != null && dubPlayback.Subtitles.Count > 0)
                            {
                                playbackData.Subtitles ??= new List<SubtitleInfo>();
                                // Key on lang+CC+signs so a Closed Caption track is not deduped away
                                // by a regular subtitle that shares the same locale, and a full
                                // dialogue track (from the original version) is not deduped away by
                                // a same-locale signs track from a dub version (or vice versa).
                                var existingSubLangs = new HashSet<string>(
                                    playbackData.Subtitles.Where(s => s.Lang != null).Select(s => $"{s.Lang}|{s.IsCC}|{IsSignsSubtitle(s)}"),
                                    StringComparer.OrdinalIgnoreCase);
                                foreach (var s in dubPlayback.Subtitles)
                                {
                                    if (!string.IsNullOrEmpty(s.Lang) && existingSubLangs.Add($"{s.Lang}|{s.IsCC}|{IsSignsSubtitle(s)}"))
                                    {
                                        playbackData.Subtitles.Add(s);
                                    }
                                }
                            }

                            if (dubPlayback?.VideoUrl != null &&
                                (dubPlayback.VideoUrl.Contains(".mpd") || dubPlayback.VideoUrl.Contains("/dash/")))
                            {
                                var (_, dubAudioPaths) = await DownloadDashTracksAsync(dubPlayback.VideoUrl, tempDir, config, progress, 78, 80, cancellationToken, dubPlayback.VideoToken, dubGuid, new List<string> { dub }, audioOnly: true);
                                foreach (var ap in dubAudioPaths)
                                {
                                    downloadedFiles.Add(ap.Path);
                                    audioTrackLanguages.Add(ap);
                                    _logger?.LogInformation("Downloaded additional DASH dub audio: {Dub} -> {Path}", dub, ap.Path);
                                }
                            }

                            if (config.Download.DownloadDelayUseDubBased && config.Download.DownloadDelaySeconds > 0)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(config.Download.DownloadDelaySeconds), cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to download additional DASH dub: {Dub}", dub);
                        }
                    }
                }

                // Single-dub subtitles: the dub's own playback has no subs (CR keeps the full set on
                // the ORIGINAL version), and the multi-dub loop above only runs when extra dubs were
                // requested. So if a requested subtitle is still missing, fetch the ORIGINAL version's
                // playback HERE — the same LATE point (after the main download) where the dub-audio
                // fetches above succeed. A 2nd /play done IMMEDIATELY after the primary fails CR with
                // 40016 "Outdated Token"; done after the download it works (the prior play settles).
                // Union its subtitles only — do NOT add its audio. Best-effort.
                if (!config.Download.SkipSubs && episode.Versions != null)
                {
                    var wantedSubs = (episode.SelectedSubs?.Count > 0 ? episode.SelectedSubs : config.Download.SoftSubs) ?? new List<string>();
                    var havePool = playbackData.Subtitles ?? new List<SubtitleInfo>();
                    // A wanted language only counts as present when the pool holds its FULL
                    // dialogue track. A dub version's own same-locale sub is signs/songs only,
                    // so it must not stop us fetching the original version's full track.
                    var stillMissing = wantedSubs.Any(r => !string.IsNullOrWhiteSpace(r) &&
                        !havePool.Any(s => string.Equals(s.Lang, r, StringComparison.OrdinalIgnoreCase) && !s.IsCC && !IsSignsSubtitle(s)));
                    var origVersion = episode.Versions.FirstOrDefault(v => v.Original);
                    if (stillMissing && origVersion != null && !string.IsNullOrEmpty(origVersion.Guid) &&
                        !string.Equals(origVersion.Guid, mediaGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        var origGuid = origVersion.Guid;
                        if (origGuid.Contains(':')) origGuid = origGuid.Split(':')[1];
                        try
                        {
                            var subPlayback = await GetPlaybackDataAsync(origGuid, true, cancellationToken, config);
                            if (subPlayback?.Subtitles != null && subPlayback.Subtitles.Count > 0)
                            {
                                playbackData.Subtitles ??= new List<SubtitleInfo>();
                                var existing = new HashSet<string>(
                                    playbackData.Subtitles.Where(s => s.Lang != null).Select(s => $"{s.Lang}|{s.IsCC}|{IsSignsSubtitle(s)}"),
                                    StringComparer.OrdinalIgnoreCase);
                                var added = 0;
                                foreach (var s in subPlayback.Subtitles)
                                {
                                    if (!string.IsNullOrEmpty(s.Lang) && existing.Add($"{s.Lang}|{s.IsCC}|{IsSignsSubtitle(s)}"))
                                    {
                                        playbackData.Subtitles.Add(s);
                                        added++;
                                    }
                                }
                                _logger?.LogInformation("Unioned {Added} subtitles from original version {Guid} (late, subs-only)", added, origGuid);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to fetch original-version subtitles (late)");
                        }
                    }
                }
            }
            else
            {
                // Check if URLs are HLS playlists
                bool videoIsHls = IsHlsUrl(playbackData.VideoUrl);
                bool audioIsHls = IsHlsUrl(playbackData.AudioUrl);

                if (videoIsHls || audioIsHls)
                {
                    _logger?.LogInformation("Using HLS downloader for segmented streams");
                }

                // Download video (skip if NoVideo is enabled)
                if (playbackData.VideoUrl != null && !config.Download.NoVideo)
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 30, Doing = "Downloading video..." });
                    var videoPath = Path.Combine(tempDir, "video.mp4");

                    if (videoIsHls)
                    {
                        var hlsResult = await DownloadHlsStreamAsync(playbackData.VideoUrl, videoPath, true, false, config, progress, 30, 60, cancellationToken);
                        if (hlsResult.Ok)
                        {
                            downloadedFiles.Add(videoPath);
                            videoLocales[videoPath] = episode.AudioLocale;
                        }
                    }
                    else
                    {
                        await DownloadStreamAsync(playbackData.VideoUrl, videoPath, progress, 30, 60, cancellationToken, playbackData.VideoToken);
                        downloadedFiles.Add(videoPath);
                        videoLocales[videoPath] = episode.AudioLocale;
                    }
                }
                else if (config.Download.NoVideo)
                {
                    _logger?.LogInformation("NoVideo enabled, skipping video download");
                }

                // Download primary audio (skip if NoAudio is enabled)
                if (playbackData.AudioUrl != null && !config.Download.NoAudio)
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 60, Doing = $"Downloading audio ({episode.AudioLocale})..." });
                    var audioPath = Path.Combine(tempDir, $"audio_{(episode.AudioLocale ?? "unknown").Replace("-", "").ToLower()}.m4a");

                    if (audioIsHls)
                    {
                        var hlsResult = await DownloadHlsStreamAsync(playbackData.AudioUrl, audioPath, false, true, config, progress, 60, 80, cancellationToken);
                        if (hlsResult.Ok)
                        {
                            downloadedFiles.Add(audioPath);
                            audioTrackLanguages.Add((audioPath, episode.AudioLocale ?? "unknown"));
                        }
                    }
                    else
                    {
                        await DownloadStreamAsync(playbackData.AudioUrl, audioPath, progress, 60, 80, cancellationToken, playbackData.VideoToken);
                        downloadedFiles.Add(audioPath);
                        audioTrackLanguages.Add((audioPath, episode.AudioLocale ?? "unknown"));
                    }
                }
                else if (config.Download.NoAudio)
                {
                    _logger?.LogInformation("NoAudio enabled, skipping audio download");
                }

                // Audio description (AD) is a separate real stream, not a copy of the
                // primary audio. Upstream fetches it via the play endpoint with
                // ?audioRole=description; we download the real AD version below (see the
                // DownloadDescriptionAudio block). Fabricating an AD track by copying the
                // primary audio produced a duplicate track mislabeled as AD, so it was
                // removed.

                // Download additional dubs if configured (skip if NoAudio is enabled)
                // Note: Video is only downloaded once (DlVideoOnce optimization). Additional dubs reuse the same video stream.
                if (!config.Download.NoAudio && !config.Download.DownloadFirstAvailableDub &&
                    config.Download.DownloadMultipleDubs && episode.Versions != null && episode.Versions.Count > 1)
                {
                    var primaryLocale = episode.AudioLocale;
                    // Use episode's SelectedDubs if set, otherwise fall back to config
                    var selectedDubs = (episode.SelectedDubs?.Count > 0
                        ? episode.SelectedDubs
                        : config.Download.DubLanguages)
                        .Where(dub => !string.Equals(dub, primaryLocale, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _logger?.LogInformation("DlVideoOnce: Reusing video from primary dub for {Count} additional dubs", selectedDubs.Count);

                    foreach (var dub in selectedDubs)
                    {
                        var dubVersion = episode.Versions.FirstOrDefault(v =>
                            string.Equals(v.AudioLocale, dub, StringComparison.OrdinalIgnoreCase));

                        if (dubVersion == null) continue;

                        var dubMediaGuid = dubVersion.Guid;
                        var dubMediaId = dubVersion.MediaGuid ?? dubVersion.Guid;

                        if (string.IsNullOrEmpty(dubMediaId))
                        {
                            _logger?.LogWarning("Dub version missing media ID");
                            continue;
                        }

                        if (dubMediaId.Contains(':')) dubMediaId = dubMediaId.Split(':')[1];
                        if (dubMediaGuid.Contains(':')) dubMediaGuid = dubMediaGuid.Split(':')[1];

                        _logger?.LogInformation("Fetching playback data for additional dub: {Dub} (Guid={Guid})", dub, dubMediaGuid);

                        try
                        {
                            var dubPlayback = await GetPlaybackDataAsync(dubMediaGuid, true, cancellationToken, config);

                            // Download sync video for timing comparison if SyncTiming is enabled
                            if (config.Download.SyncTiming && config.Download.DlVideoOnce && dubPlayback?.VideoUrl != null)
                            {
                                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 62, Doing = $"Downloading sync video ({dub})..." });
                                var syncVideoPath = Path.Combine(tempDir, $"syncvideo_{(dub ?? "unknown").Replace("-", "").ToLower()}.mp4");
                                var dubVideoIsHls = IsHlsUrl(dubPlayback.VideoUrl);

                                if (dubVideoIsHls)
                                {
                                    var hlsResult = await DownloadHlsStreamAsync(dubPlayback.VideoUrl, syncVideoPath, true, false, config, progress, 60, 65, cancellationToken);
                                    if (hlsResult.Ok)
                                    {
                                        syncVideos[dub ?? "unknown"] = syncVideoPath;
                                        _logger?.LogInformation("Downloaded sync video for dub: {Dub} -> {Path}", dub, syncVideoPath);
                                    }
                                }
                                else
                                {
                                    await DownloadStreamAsync(dubPlayback.VideoUrl, syncVideoPath, progress, 60, 65, cancellationToken, dubPlayback.VideoToken);
                                    syncVideos[dub ?? "unknown"] = syncVideoPath;
                                    _logger?.LogInformation("Downloaded sync video for dub: {Dub} -> {Path}", dub, syncVideoPath);
                                }
                            }

                            if (dubPlayback?.AudioUrl != null)
                            {
                                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 65, Doing = $"Downloading audio ({dub})..." });

                                var dubAudioPath = Path.Combine(tempDir, $"audio_{(dub ?? "unknown").Replace("-", "").ToLower()}.m4a");
                                var dubAudioIsHls = IsHlsUrl(dubPlayback.AudioUrl);

                                if (dubAudioIsHls)
                                {
                                    var hlsResult = await DownloadHlsStreamAsync(dubPlayback.AudioUrl, dubAudioPath, false, true, config, progress, 65, 80, cancellationToken);
                                    if (hlsResult.Ok)
                                    {
                                        downloadedFiles.Add(dubAudioPath);
                                        audioTrackLanguages.Add((dubAudioPath, dub ?? "unknown"));
                                        _logger?.LogInformation("Downloaded additional audio track: {Dub} -> {Path}", dub, dubAudioPath);
                                    }
                                }
                                else
                                {
                                    await DownloadStreamAsync(dubPlayback.AudioUrl, dubAudioPath, progress, 65, 80, cancellationToken, dubPlayback.VideoToken);
                                    downloadedFiles.Add(dubAudioPath);
                                    audioTrackLanguages.Add((dubAudioPath, dub ?? "unknown"));
                                    _logger?.LogInformation("Downloaded additional audio track: {Dub} -> {Path}", dub, dubAudioPath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to download additional dub: {Dub}", dub);
                        }

                        // [PT] Ported from upstream: download delay between dubs (only when dub-based delay is enabled;
                        // otherwise the delay applies once per episode in QueueService)
                        if (config.Download.DownloadDelayUseDubBased && config.Download.DownloadDelaySeconds > 0)
                        {
                            _logger?.LogInformation("Waiting {Delay}s before next dub download...", config.Download.DownloadDelaySeconds);
                            await Task.Delay(TimeSpan.FromSeconds(config.Download.DownloadDelaySeconds), cancellationToken);
                        }
                    }
                }

                // Download Audio Description (AD) track if configured (skip if NoAudio is enabled)
                if (!config.Download.NoAudio && config.Download.DownloadDescriptionAudio && episode.Versions != null)
                {
                    var adVersion = episode.Versions.FirstOrDefault(v =>
                        v.Roles?.Any(r => string.Equals(r, "description", StringComparison.OrdinalIgnoreCase)) == true);

                    if (adVersion != null)
                    {
                        var adLocale = adVersion.AudioLocale;
                        // Skip if we already downloaded this locale (AD tracks share locale with main track)
                        var alreadyDownloaded = audioTrackLanguages.Any(a =>
                            string.Equals(a.Lang, adLocale, StringComparison.OrdinalIgnoreCase));

                        if (!alreadyDownloaded)
                        {
                            var adMediaGuid = adVersion.Guid;
                            var adMediaId = adVersion.MediaGuid ?? adVersion.Guid;

                            if (!string.IsNullOrEmpty(adMediaId))
                            {
                                if (adMediaId.Contains(':')) adMediaId = adMediaId.Split(':')[1];
                                if (adMediaGuid.Contains(':')) adMediaGuid = adMediaGuid.Split(':')[1];

                                _logger?.LogInformation("Fetching playback data for Audio Description: {Locale} (Guid={Guid})", adLocale, adMediaGuid);

                                try
                                {
                                    var adPlayback = await GetPlaybackDataAsync(adMediaGuid, true, cancellationToken, config);
                                    if (adPlayback?.AudioUrl != null)
                                    {
                                        progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 67, Doing = $"Downloading audio description ({adLocale})..." });

                                        var adAudioPath = Path.Combine(tempDir, $"audio_{(adLocale ?? "unknown").Replace("-", "").ToLower()}_ad.m4a");
                                        var adAudioIsHls = IsHlsUrl(adPlayback.AudioUrl);

                                        if (adAudioIsHls)
                                        {
                                            var hlsResult = await DownloadHlsStreamAsync(adPlayback.AudioUrl, adAudioPath, false, true, config, progress, 60, 80, cancellationToken);
                                            if (hlsResult.Ok)
                                            {
                                                downloadedFiles.Add(adAudioPath);
                                                audioTrackLanguages.Add((adAudioPath, adLocale ?? "unknown"));
                                                _logger?.LogInformation("Downloaded audio description track: {Locale} -> {Path}", adLocale, adAudioPath);
                                            }
                                        }
                                        else
                                        {
                                            await DownloadStreamAsync(adPlayback.AudioUrl, adAudioPath, progress, 60, 80, cancellationToken, adPlayback.VideoToken);
                                            downloadedFiles.Add(adAudioPath);
                                            audioTrackLanguages.Add((adAudioPath, adLocale ?? "unknown"));
                                            _logger?.LogInformation("Downloaded audio description track: {Locale} -> {Path}", adLocale, adAudioPath);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning(ex, "Failed to download audio description for {Locale}", adLocale);
                                }
                            }
                            else
                            {
                                _logger?.LogWarning("Audio Description version missing media ID");
                            }
                        }
                    }
                }
            }

            // Download subtitles. Carries CC/Signs flags so include filters, dedup, the
            // muxing CC flag and Signs-as-forced all work (ported from upstream
            // CrunchyrollManager.DownloadSubtitles + SubtitleUtils).
            var subtitleFiles = new List<(string Path, string Lang, bool Cc, bool Signs)>();
            if (!config.Download.SkipSubs && playbackData.Subtitles != null && playbackData.Subtitles.Count > 0)
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 80, Doing = "Downloading subtitles..." });
                // Track (lang|cc|signs) we already wrote to honor SubsDownloadDuplicate.
                var downloadedSubKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Same resolution order as the availability check and the late original-version
                // fetch: per-episode SelectedSubs (what the user picked in the UI), then SoftSubs,
                // then SubtitleLanguages. Previously SelectedSubs was ignored here, so the add-paths
                // were not synchronized with the actual subtitle download.
                var subLangs = episode.SelectedSubs?.Count > 0 ? episode.SelectedSubs
                    : (config.Download.SoftSubs?.Count > 0 ? config.Download.SoftSubs : config.Download.SubtitleLanguages);

                foreach (var sub in playbackData.Subtitles)
                {
                    var langCode = (sub.Lang ?? "unknown").Replace("-", "").ToLower();
                    var shouldDownload = subLangs.Contains("all") ||
                                         (sub.Lang != null && subLangs.Contains(sub.Lang)) ||
                                         subLangs.Contains(langCode);

                    if (!shouldDownload || string.IsNullOrEmpty(sub.Url))
                        continue;

                    var isCc = sub.IsCC;
                    // Upstream (CrunchyrollManager.DownloadSubtitles): a subtitle is "signs" when
                    // its locale equals the audio locale of the VERSION it came from — the dub's
                    // own same-language track is signs/songs, while the same language fetched from
                    // the original (ja-JP) version is the full dialogue track.
                    var isSigns = IsSignsSubtitle(sub);

                    // Include filters (upstream: skip signs unless IncludeSignsSubs, CC unless IncludeCcSubs).
                    if ((!config.Download.IncludeSignsSubs && isSigns) || (!config.Download.IncludeCcSubs && isCc))
                        continue;

                    // Duplicate suppression: same lang+cc+signs already written → skip unless allowed.
                    var dupKey = $"{sub.Lang}|{isCc}|{isSigns}";
                    if (downloadedSubKeys.Contains(dupKey) && !config.Download.SubsDownloadDuplicate)
                        continue;

                    var ext = sub.Format?.ToLower() == "ass" ? "ass" : "vtt";
                    var tag = (isCc ? "_cc" : "") + (isSigns ? "_signs" : "");
                    var subPath = Path.Combine(tempDir, $"sub_{sub.Lang}{tag}.{ext}");

                    try
                    {
                        var subRequest = new HttpRequestMessage(HttpMethod.Get, sub.Url);
                        var (subOk, subContent, _) = await _httpClient.SendRequestAsync(subRequest);
                        if (subOk && !string.IsNullOrEmpty(subContent))
                        {
                            var crLocale = Languages.FindLang(sub.Lang ?? "unknown").CrLocale;
                            if (sub.Format?.ToLower() == "ass")
                            {
                                // Clean ASS + apply ScaledBorderAndShadow / FixCccSubtitles.
                                subContent = SubtitleUtils.CleanAssAndEnsureScriptInfo(
                                    subContent, config.Download.FixCccSubtitles, config.Download.SubsAddScaledBorder, crLocale);
                                await File.WriteAllTextAsync(subPath, subContent, cancellationToken);
                            }
                            else if (config.Download.ConvertVttToAss)
                            {
                                // Convert VTT to ASS honoring CcSubsFont + ScaledBorderAndShadow.
                                subPath = Path.ChangeExtension(subPath, ".ass");
                                var assContent = ConvertVttToAss(
                                    subContent, sub.Lang ?? "unknown", config.Download.CcSubsFont, config.Download.SubsAddScaledBorder);
                                await File.WriteAllTextAsync(subPath, assContent, cancellationToken);
                            }
                            else
                            {
                                await File.WriteAllTextAsync(subPath, subContent, cancellationToken);
                            }
                            subtitleFiles.Add((subPath, sub.Lang ?? "unknown", isCc, isSigns));
                            downloadedSubKeys.Add(dupKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to download subtitle {Lang} (cc={Cc}, signs={Signs})", sub.Lang, isCc, isSigns);
                    }
                }
            }

            // Download cover art if available and enabled
            string? coverPath = null;
            if (!string.IsNullOrEmpty(episode.CoverArtUrl) && config.Download.MuxCover && !config.Download.SkipMuxing)
            {
                try
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 83, Doing = "Downloading cover art..." });
                    // [PT] Ported from upstream c123093: unique cover path per episode to avoid collisions
                    coverPath = Path.Combine(tempDir, $"{fileName}.cover.png");
                    if (!File.Exists(coverPath))
                    {
                        using var coverResponse = await _httpClient.Client.GetAsync(episode.CoverArtUrl, cancellationToken);
                        if (coverResponse.IsSuccessStatusCode)
                        {
                            var coverBytes = await coverResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                            if (coverBytes != null && coverBytes.Length > 0)
                            {
                                await File.WriteAllBytesAsync(coverPath, coverBytes, cancellationToken);
                                _logger?.LogDebug("Downloaded cover art to {Path}", coverPath);
                            }
                        }
                    }
                    else
                    {
                        _logger?.LogDebug("Cover art already exists at {Path}, skipping download", coverPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to download cover art for {EpisodeId}", episode.Id);
                }
            }

            // Generate description XML if enabled
            string? descriptionPath = null;
            if (config.Download.IncludeVideoDescription && !config.Download.SkipMuxing)
            {
                // DescriptionLang: re-fetch the episode metadata in the configured language
                // so the muxed description matches it (upstream re-parses by MediaId).
                var descriptionText = episode.Description ?? string.Empty;
                var descLang = config.Download.DescriptionLang;
                if (!string.IsNullOrEmpty(descLang) && !string.IsNullOrEmpty(mediaId))
                {
                    try
                    {
                        var localized = await _api.ParseEpisodeByIdAsync(mediaId, descLang, false, cancellationToken);
                        if (!string.IsNullOrEmpty(localized?.Description))
                            descriptionText = localized!.Description;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to fetch description in {Lang}, using default", descLang);
                    }
                }

                if (!string.IsNullOrEmpty(descriptionText))
                {
                    descriptionPath = Path.Combine(tempDir, "description.xml");
                    try
                    {
                        using var writer = XmlWriter.Create(descriptionPath);
                        writer.WriteStartDocument();
                        writer.WriteStartElement("Tags");
                        writer.WriteStartElement("Tag");
                        writer.WriteStartElement("Targets");
                        writer.WriteElementString("TargetTypeValue", "50");
                        writer.WriteEndElement(); // Targets
                        writer.WriteStartElement("Simple");
                        writer.WriteElementString("Name", "DESCRIPTION");
                        writer.WriteElementString("String", descriptionText);
                        writer.WriteEndElement(); // Simple
                        writer.WriteEndElement(); // Tag
                        writer.WriteEndElement(); // Tags
                        writer.WriteEndDocument();
                        _logger?.LogInformation("Generated description XML: {Path}", descriptionPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to generate description XML");
                        descriptionPath = null;
                    }
                }
            }

            // Extract fonts from subtitles if muxing is enabled
            var fontAttachments = new List<FontAttachment>();
            if (config.Download.MuxFonts && subtitleFiles.Count > 0)
            {
                try
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 81, Doing = "Extracting fonts..." });
                    var allFontNames = new List<string>();
                    foreach (var (subPath, _, _, _) in subtitleFiles.Where(s => s.Path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)))
                    {
                        var assContent = await File.ReadAllTextAsync(subPath, cancellationToken);
                        var fonts = _fontService.ExtractFontsFromAss(assContent, config.Download.MuxTypesettingFonts);
                        allFontNames.AddRange(fonts);
                    }
                    if (allFontNames.Count > 0)
                    {
                        var fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
                        fontAttachments = _fontService.ResolveFonts(allFontNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), fontsDir);
                        _logger?.LogInformation("Resolved {Count} fonts for muxing", fontAttachments.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to extract fonts from subtitles");
                }
            }

            // Fetch chapters if enabled
            string? chapterFile = null;
            if (config.Download.IncludeChapters && !string.IsNullOrEmpty(episode.Id))
            {
                try
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 82, Doing = "Fetching chapters..." });
                    var chapters = await _chapterService.GetChaptersAsync(episode.Id, _auth.Token?.access_token, cancellationToken);
                    if (chapters.Count > 0)
                    {
                        var chapterPath = Path.Combine(tempDir, "chapters.txt");
                        chapterFile = await _chapterService.WriteChapterFileAsync(chapters, chapterPath, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to fetch chapters for {EpisodeId}", episode.Id);
                }
            }

            // Decrypt if keys available
            if (decryptionKeys != null && decryptionKeys.Count > 0)
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 85, Doing = "Decrypting..." });
                downloadedFiles = await DecryptFilesAsync(downloadedFiles, decryptionKeys, cancellationToken);

                // Update audio track paths after decryption (paths change from .enc.m4s to .m4s)
                audioTrackLanguages = audioTrackLanguages.Select(a =>
                {
                    var decryptedPath = Path.Combine(Path.GetDirectoryName(a.Path)!,
                        Path.GetFileNameWithoutExtension(a.Path).Replace(".enc", "") + Path.GetExtension(a.Path));
                    return File.Exists(decryptedPath) ? (decryptedPath, a.Lang) : a;
                }).ToList();
            }

            // Sync Timing: Calculate delays for dubs if enabled
            var audioDelays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (config.Download.SyncTiming && config.Download.DlVideoOnce && syncVideos.Count > 0 && _videoSyncer != null)
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 86, Doing = "Syncing dub timings..." });

                // Find base video path (first video file that's not a sync video)
                var baseVideoPath = downloadedFiles.FirstOrDefault(f =>
                    !syncVideos.Values.Any(sv => string.Equals(sv, f, StringComparison.OrdinalIgnoreCase)) &&
                    (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)));

                if (!string.IsNullOrEmpty(baseVideoPath))
                {
                    var ffmpegPath = FindExecutable("ffmpeg") ?? "ffmpeg";
                    var syncErrors = new List<string>();

                    foreach (var (dubLocale, syncVideoPath) in syncVideos)
                    {
                        try
                        {
                            _logger?.LogInformation("Syncing dub timing for {Dub}: base={Base}, sync={Sync}", dubLocale, baseVideoPath, syncVideoPath);
                            var delay = await _videoSyncer.ProcessVideo(baseVideoPath, syncVideoPath, tempDir, ffmpegPath, config.Download.SyncHwAccel);

                            if (delay.offSet <= -100)
                            {
                                _logger?.LogWarning("Sync failed for dub {Dub}: offset={Offset}", dubLocale, delay.offSet);
                                syncErrors.Add(dubLocale);
                                continue;
                            }

                            var delayMs = (int)(delay.offSet * 1000);
                            audioDelays[dubLocale] = delayMs;
                            _logger?.LogInformation("Sync delay for dub {Dub}: {Delay}ms", dubLocale, delayMs);

                            if (delay.lengthDiff > 0.1)
                            {
                                _logger?.LogWarning("Dub length difference for {Dub}: {LengthDiff}s", dubLocale, delay.lengthDiff);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error syncing dub {Dub}", dubLocale);
                            syncErrors.Add(dubLocale);
                        }
                    }

                    // Clean up sync videos after processing
                    foreach (var syncVideoPath in syncVideos.Values)
                    {
                        try
                        {
                            if (File.Exists(syncVideoPath)) File.Delete(syncVideoPath);
                            var resumeFile = syncVideoPath + ".resume";
                            if (File.Exists(resumeFile)) File.Delete(resumeFile);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to delete sync video: {Path}", syncVideoPath);
                        }
                    }

                    // SyncTimingFullQualityFallback - re-download full quality video for failed dubs
                    if (syncErrors.Count > 0 && config.Download.SyncTimingFullQualityFallback)
                    {
                        _logger?.LogInformation("Sync timing fallback enabled for failed dubs: {Dubs}", string.Join(", ", syncErrors));

                        foreach (var failedLocale in syncErrors.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var fallbackVideoPath = await DownloadFallbackVideoAsync(episode, failedLocale, tempDir, config, progress, cancellationToken);
                                if (!string.IsNullOrEmpty(fallbackVideoPath))
                                {
                                    // Remove old video for this locale if exists
                                    var oldVideo = downloadedFiles.FirstOrDefault(f =>
                                        videoLocales.TryGetValue(f, out var vl) &&
                                        string.Equals(vl, failedLocale, StringComparison.OrdinalIgnoreCase));
                                    if (oldVideo != null)
                                    {
                                        downloadedFiles.Remove(oldVideo);
                                        videoLocales.Remove(oldVideo);
                                        try { if (File.Exists(oldVideo)) File.Delete(oldVideo); } catch { }
                                    }

                                    downloadedFiles.Add(fallbackVideoPath);
                                    videoLocales[fallbackVideoPath] = failedLocale;
                                    _logger?.LogInformation("Added fallback video for {Locale}: {Path}", failedLocale, fallbackVideoPath);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to download fallback video for {Locale}", failedLocale);
                            }
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning("Could not find base video path for sync timing");
                }
            }

            // Probe actual video resolution and fix filename if needed
            var firstVideoFile = downloadedFiles.FirstOrDefault(f => !audioTrackLanguages.Any(a => a.Path == f));
            if (firstVideoFile != null && !config.Download.NoVideo)
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 85, Doing = "Probing video resolution..." });
                var (actualHeight, actualWidth) = await ProbeVideoResolutionAsync(firstVideoFile, cancellationToken);

                if (actualHeight.HasValue)
                {
                    // Check if current filename uses quality preference instead of actual resolution
                    var qualityPref = config.Download.QualityVideo?.ToLowerInvariant();
                    bool needsRename = qualityPref == "best" || qualityPref == "worst" ||
                                       (outputPath.Contains($"[{config.Download.QualityVideo}p]") && config.Download.QualityVideo != actualHeight.Value.ToString());

                    if (needsRename)
                    {
                        _logger?.LogInformation("Renaming output file to use actual resolution {Height}p instead of quality preference '{Quality}'",
                            actualHeight.Value, config.Download.QualityVideo);

                        var newFilenameOptions = new FilenameOptions
                        {
                            NumberPadding = config.Download.LeadingNumbers,
                            WhitespaceReplace = config.Download.FilenameWhitespaceSubstitute,
                            Quality = actualHeight.Value.ToString(),
                            AudioLanguage = config.Download.DefaultAudio,
                            SonarrSeries = sonarrSeries,
                            SonarrEpisode = sonarrEpisode,
                            UseSonarrNumbering = config.Sonarr.UseSonarrNumbering,
                            SelectedDubs = episode.SelectedDubs?.Count > 0
                                ? episode.SelectedDubs
                                : config.Download.DubLanguages
                        };
                        var newFileName = _filenameService.FormatFilename(filenameTemplate, episode, newFilenameOptions);
                        var newOutputPath = Path.Combine(outputDir, newFileName + outputExtension);

                        // Handle collisions or replace existing
                        if (File.Exists(newOutputPath))
                        {
                            if (config.Download.ReplaceExistingFiles)
                            {
                                // [PT] Ported from upstream c123093: respect ReplaceExistingFiles config in quality-probe rename path
                                _logger?.LogInformation("Replacing existing file: {OutputPath}", newOutputPath);
                                File.Delete(newOutputPath);
                            }
                            else
                            {
                                int counter = 1;
                                var baseNewPath = newOutputPath;
                                while (File.Exists(newOutputPath))
                                {
                                    newOutputPath = Path.Combine(outputDir, $"{newFileName}({counter}){outputExtension}");
                                    counter++;
                                }
                                _logger?.LogWarning("Collision detected, using {Path}", newOutputPath);
                            }
                        }

                        outputPath = newOutputPath;
                    }
                }
            }

            // Notify queue service that download phase is complete (allows next download to start early)
            onDownloadComplete?.Invoke();

            // Wait for processing slot (muxing/encoding limit)
            bool processingSlotHeld = false;
            try
            {
                if (_queueService != null)
                {
                    progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 88, Doing = "Waiting for processing slot..." });
                    await _queueService.WaitForProcessingSlotAsync(cancellationToken);
                    processingSlotHeld = true;
                }

                // Mux
                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 90, Doing = "Muxing..." });
                if (!config.Download.SkipMuxing)
                {
                    if (config.Download.KeepDubsSeparate && !config.Download.DlVideoOnce && audioTrackLanguages.Count > 0)
                    {
                        // Group by dub language and create separate output files
                        var groups = audioTrackLanguages.GroupBy(a => a.Lang).ToList();
                        foreach (var group in groups)
                        {
                            var locale = group.Key;
                            var groupAudioTracks = group.Select(a => (a.Path, a.Lang)).ToList();
                            var groupOutputPath = Path.Combine(
                                Path.GetDirectoryName(outputPath) ?? "/downloads",
                                Path.GetFileNameWithoutExtension(outputPath) + $".{locale}" + Path.GetExtension(outputPath)
                            );
                            // Mux/encode in tempDir when enabled, then move the finished file out.
                            var groupWorkPath = transcodeInTemp
                                ? Path.Combine(tempDir, Path.GetFileName(groupOutputPath))
                                : groupOutputPath;

                            progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 90, Doing = $"Muxing {locale}..." });
                            await MuxFilesAsync(downloadedFiles, groupAudioTracks, subtitleFiles, chapterFile, fontAttachments, coverPath, groupWorkPath, config, cancellationToken, audioDelays, videoLocales, descriptionPath, preferredAudioLang: locale);

                            // Post-process encoding for this group if configured
                            if (config.Download.EncodeEnabled && !string.IsNullOrEmpty(config.Download.EncodingPreset) && _encodingService != null)
                            {
                                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 95, Doing = $"Encoding {locale}..." });
                                await EncodeOutputWithLimitAsync(groupWorkPath, config.Download.EncodingPreset, cancellationToken, progress, locale);
                            }

                            if (!string.Equals(groupWorkPath, groupOutputPath, StringComparison.Ordinal))
                            {
                                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 97, Doing = $"Moving {locale}..." });
                                MoveToFinalPath(groupWorkPath, groupOutputPath);
                            }
                        }
                    }
                    else
                    {
                        // Mux/encode in tempDir when enabled, then move the finished file to outputPath.
                        var workPath = transcodeInTemp
                            ? Path.Combine(tempDir, Path.GetFileName(outputPath))
                            : outputPath;

                        // Default audio = the user's FIRST chosen dub for this download (e.g. "en-US"
                        // when they picked English in Add Download), so the player auto-plays it
                        // instead of the global ja-JP default. Bare adds (no selection) fall back.
                        var primaryDub = episode.SelectedDubs?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        await MuxFilesAsync(downloadedFiles, audioTrackLanguages, subtitleFiles, chapterFile, fontAttachments, coverPath, workPath, config, cancellationToken, audioDelays, videoLocales, descriptionPath, preferredAudioLang: primaryDub);

                        // Post-process encoding if configured
                        if (config.Download.EncodeEnabled && !string.IsNullOrEmpty(config.Download.EncodingPreset) && _encodingService != null)
                        {
                            progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 95, Doing = "Encoding..." });
                            await EncodeOutputWithLimitAsync(workPath, config.Download.EncodingPreset, cancellationToken, progress);
                        }

                        if (!string.Equals(workPath, outputPath, StringComparison.Ordinal))
                        {
                            progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 97, Doing = "Moving to output..." });
                            MoveToFinalPath(workPath, outputPath);
                        }
                    }
                }
                else
                {
                    // Skip muxing: move the raw downloaded streams (+ subs) to the output dir so they
                    // aren't lost when tempDir is cleaned up. Without this the files downloaded then
                    // vanished (and with the temp folder enabled nothing appeared at all).
                    var outDir = Path.GetDirectoryName(outputPath) ?? "/downloads";
                    var baseName = Path.GetFileNameWithoutExtension(outputPath);
                    var rawFiles = downloadedFiles.Concat(subtitleFiles.Select(s => s.Path))
                        .Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).Distinct().ToList();
                    foreach (var rawFile in rawFiles)
                    {
                        try
                        {
                            var dest = Path.Combine(outDir, baseName + "." + Path.GetFileName(rawFile));
                            MoveToFinalPath(rawFile, dest);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Skip-mux: failed to move raw file out of temp: {File}", rawFile);
                        }
                    }
                    _logger?.LogInformation("Skip muxing: moved {Count} raw stream file(s) to {Dir}", rawFiles.Count, outDir);
                }
            }
            finally
            {
                if (processingSlotHeld && _queueService != null)
                {
                    _queueService.ReleaseProcessingSlot();
                }
            }

            // Verify the muxed output actually exists before reporting success. Previously a mux
            // that "succeeded" but wrote nothing (e.g. a mangled non-ASCII path) still returned
            // Success=true, so the item showed "Complete" with only an empty series folder. Fail
            // loudly instead so it errors/retries. Skip-mux mode moves raw files, checked separately.
            if (!config.Download.SkipMuxing && !File.Exists(outputPath))
            {
                throw new DownloadException(
                    $"Muxing/encoding produced no output file at '{outputPath}'. The download did not complete.",
                    DownloadErrorType.Unknown);
            }

            progress?.Report(new DownloadProgress { State = DownloadState.Done, Percent = 100, Doing = "Complete" });

            // Record in history
            if (_history != null && config.Download.HistoryEnabled)
            {
                try
                {
                    var fileInfo = new FileInfo(outputPath);
                    var downloadedDubs = audioTrackLanguages.Select(a => a.Lang).Distinct().ToList();
                    var downloadedSubs = subtitleFiles.Select(s => s.Lang).Distinct().ToList();

                    // Series id used to group + mark this download (see ResolveRichSeriesId).
                    var richSeriesId = ResolveRichSeriesId(episode);
                    episode.SeriesId = richSeriesId;
                    if (string.IsNullOrWhiteSpace(episode.SeasonId))
                        episode.SeasonId = $"{richSeriesId}|S{episode.SeasonNumber}";

                    await _history.AddAsync(new DownloadHistory
                    {
                        EpisodeId = episode.Id,
                        SeriesId = richSeriesId ?? string.Empty,
                        SeriesTitle = episode.SeriesTitle,
                        EpisodeTitle = episode.Title,
                        SeasonNumber = episode.SeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        AudioLanguage = episode.Locale,
                        SubtitleLanguages = downloadedSubs,
                        DownloadedAt = DateTime.UtcNow,
                        OutputPath = outputPath,
                        FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
                    });

                    // Populate the rich history (series -> season -> episode) the History UI reads,
                    // THEN mark the episode downloaded. Without the populate step the mark always
                    // failed ("Couldn't update download history") and nothing appeared in History.
                    if (!string.IsNullOrWhiteSpace(richSeriesId))
                    {
                        await _history.UpdateWithSeasonDataAsync(new List<EpisodeInfo> { episode });
                        await _history.SetAsDownloadedAsync(richSeriesId, episode.SeasonId, episode.Id, downloadedDubs, downloadedSubs);

                        // [PT parity with upstream History.UpdateWithSeasonData] Mirror the desktop
                        // app: after recording the download, populate the FULL series (all seasons +
                        // episodes, downloaded and missing) and auto-match Sonarr (series + episodes)
                        // so History shows the whole show with Sonarr status, not just the one
                        // downloaded episode. CrUpdateSeriesAsync also recovers the real CR series id
                        // when the download could only key by title. Best-effort - a completed
                        // download must never fail over history enrichment. Deduped per series within
                        // a short window so a batch download enriches the series once, not per episode.
                        var enrichNow = DateTime.UtcNow;
                        if (!_lastSeriesEnrich.TryGetValue(richSeriesId, out var lastEnrich) || (enrichNow - lastEnrich).TotalSeconds > 30)
                        {
                            _lastSeriesEnrich[richSeriesId] = enrichNow;
                            try
                            {
                                await _history.CrUpdateSeriesAsync(richSeriesId, null);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Post-download history/Sonarr enrichment failed for {SeriesId}", richSeriesId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to record download history");
                }
            }

            // [PT] Mark as watched on Crunchyroll if configured
            if (config.Crunchyroll.MarkAsWatched)
            {
                try
                {
                    await _api.MarkAsWatchedAsync(episode.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to mark episode as watched");
                }
            }

            return new DownloadResult
            {
                Success = true,
                OutputPath = outputPath,
                Episode = episode
            };
        }
        finally
        {
            // Cleanup the per-download temp working directory (unless NoCleanup is set)
            if (!config.Download.NoCleanup)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    public async Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting series download: {SeriesId}", seriesId);

        var episodes = await _api.GetEpisodesAsync(seriesId, useBetaApi: true, cancellationToken);
        if (episodes.Count == 0)
        {
            return new DownloadResult { Success = false, ErrorMessage = "No episodes found" };
        }

        _logger?.LogInformation("Found {Count} episodes", episodes.Count);

        int successCount = 0;
        foreach (var episode in episodes)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var result = await DownloadEpisodeAsync(episode, config, progress, cancellationToken);
            if (result.Success) successCount++;
        }

        return new DownloadResult
        {
            Success = successCount > 0,
            ErrorMessage = successCount < episodes.Count ? $"Downloaded {successCount}/{episodes.Count} episodes" : null
        };
    }

    private async Task<PlaybackData?> GetPlaybackDataAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken, CruncharrConfig? config = null, int retryAttempt = 0)
    {
        var token = _auth.Token;
        if (token?.access_token == null)
        {
            throw new DownloadException("You are not logged in. Please log in to your Crunchyroll account.", DownloadErrorType.NotAuthenticated);
        }

        // Refresh token before playback API call (matches source behavior)
        await _auth.RefreshTokenAsync(useBetaApi, cancellationToken);

        // Re-read token after refresh
        token = _auth.Token;
        if (token?.access_token == null)
        {
            throw new DownloadException("You are not logged in. Please log in to your Crunchyroll account.", DownloadErrorType.NotAuthenticated);
        }

        const int maxRetries = 3;

        // Use stream endpoint settings from auth service (ported from source)
        var streamEndpoint = _auth.StreamEndpoint;
        var streamEndpointSecondary = _auth.StreamEndpointSecondary;

        var endpoints = new List<(string Endpoint, string UserAgent, CrAuthSettings Settings)>();

        var primaryUrl = $"{ApiUrls.Playback}/{episodeId}/{streamEndpoint.Endpoint}/play";
        if (streamEndpoint.Video || streamEndpoint.Audio)
        {
            endpoints.Add((primaryUrl, streamEndpoint.UserAgent, streamEndpoint));
        }

        var secondaryUrl = !string.IsNullOrEmpty(streamEndpointSecondary.Endpoint)
            ? $"{ApiUrls.Playback}/{episodeId}/{streamEndpointSecondary.Endpoint}/play"
            : null;

        // Only add secondary endpoint if it's different from primary
        if (!string.IsNullOrEmpty(secondaryUrl) && secondaryUrl != primaryUrl && (streamEndpointSecondary.Video || streamEndpointSecondary.Audio))
        {
            endpoints.Add((secondaryUrl, streamEndpointSecondary.UserAgent, streamEndpointSecondary));
        }

        // Fallback endpoint - only add if different from primary and secondary
        var fallbackUrl = $"{ApiUrls.Playback}/{episodeId}/web/firefox/play";
        if (fallbackUrl != primaryUrl && fallbackUrl != secondaryUrl)
        {
            endpoints.Add((fallbackUrl, ApiUrls.FirefoxUserAgent, streamEndpoint));
        }

        PlaybackData? mergedData = null;
        bool rateLimited = false;
        int retryDelaySeconds = GetRetryDelaySeconds(retryAttempt, config);

        foreach (var (endpoint, userAgent, settings) in endpoints)
        {
            var request = HttpClientWrapper.CreateRequest(endpoint, HttpMethod.Get, true, token.access_token);
            request.Headers.Add("User-Agent", userAgent);

            // Do NOT log any portion of the access token: diagnostics logs are exposed
            // unauthenticated via /api/v1/diagnostics/logs.
            _logger?.LogInformation("[PLAYBACK REQUEST] Endpoint={Endpoint}, UserAgent={UserAgent}",
                endpoint,
                userAgent);

            var (isOk, content, error, headers) = await _httpClient.SendRequestWithHeadersAsync(request);

            _logger?.LogInformation("[PLAYBACK RESPONSE] IsOk={IsOk}, ContentLength={ContentLength}, Error={Error}",
                isOk,
                content?.Length ?? 0,
                error);

            if (!string.IsNullOrEmpty(content) && !isOk)
            {
                _logger?.LogWarning("[PLAYBACK ERROR BODY] {Content}", content);
            }

            if (isOk && content != null)
            {
                var data = await ParsePlaybackDataAsync(content, cancellationToken);
                if (data != null)
                {
                    if (mergedData == null)
                    {
                        mergedData = data;
                    }
                    else
                    {
                        // Merge hardsubs from multiple endpoints
                        if (data.HardSubs != null)
                        {
                            mergedData.HardSubs ??= new Dictionary<string, HardSub>();
                            foreach (var kvp in data.HardSubs)
                            {
                                if (!mergedData.HardSubs.ContainsKey(kvp.Key))
                                {
                                    mergedData.HardSubs[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        // Merge subtitles from multiple endpoints
                        if (data.Subtitles != null)
                        {
                            mergedData.Subtitles ??= new List<SubtitleInfo>();
                            var existing = new HashSet<string>(mergedData.Subtitles.Select(s => s.Lang));
                            foreach (var sub in data.Subtitles.Where(s => !existing.Contains(s.Lang)))
                            {
                                mergedData.Subtitles.Add(sub);
                            }
                        }
                        // Fill missing primary URLs from secondary endpoint
                        if (string.IsNullOrEmpty(mergedData.VideoUrl)) mergedData.VideoUrl = data.VideoUrl;
                        if (string.IsNullOrEmpty(mergedData.AudioUrl)) mergedData.AudioUrl = data.AudioUrl;
                        if (string.IsNullOrEmpty(mergedData.Pssh)) mergedData.Pssh = data.Pssh;
                    }

                    // [PT] Upstream parity (ProcessPlaybackResponseAsync -> DeAuthVideo):
                    // release the active-stream session as soon as the play response is
                    // read. The play token is only a concurrency lock - the manifest, DRM
                    // license and CDN segments don't need it. Each /play endpoint we hit
                    // (primary, secondary, fallback) opens its own session, so deauth every
                    // one. Without this each download leaks an active stream and CR quickly
                    // returns TOO_MANY_ACTIVE_STREAMS / rate-limits playback - even though
                    // the website streams fine because it manages its own session.
                    if (!string.IsNullOrEmpty(data.VideoToken))
                    {
                        await DeAuthVideoAsync(episodeId, data.VideoToken);
                    }
                }
                continue;
            }

            // Check for stream errors
            if (!string.IsNullOrEmpty(content))
            {
                var streamError = StreamError.FromJson(content);

                if (streamError?.IsTooManyActiveStreamsError() == true)
                {
                    _logger?.LogWarning("Too many active streams detected. De-authing existing streams...");
                    foreach (var activeStream in streamError.ActiveStreams)
                    {
                        await DeAuthVideoAsync(activeStream.ContentId, activeStream.Token);
                    }
                    // Retry after de-auth
                    if (retryAttempt < maxRetries)
                    {
                        _logger?.LogInformation("Retrying playback request after de-auth (attempt {Attempt}/{Max})", retryAttempt + 1, maxRetries);
                        await Task.Delay(2000, cancellationToken);
                        return await GetPlaybackDataAsync(episodeId, useBetaApi, cancellationToken, config, retryAttempt + 1);
                    }
                    throw new DownloadException("Too many active streams. Close open Crunchyroll tabs in your browser and try again.", DownloadErrorType.TooManyActiveStreams);
                }

                if (streamError?.IsMaturityRatingError() == true)
                {
                    throw new DownloadException("Account maturity rating is lower than video rating. Change it in Crunchyroll account settings.", DownloadErrorType.MaturityRating);
                }

                if (streamError?.IsPlaybackRateLimitError() == true)
                {
                    rateLimited = true;
                    retryDelaySeconds = GetRetryDelaySeconds(retryAttempt, config);
                    if (headers.TryGetValue("retry-after", out var retryAfter) && int.TryParse(retryAfter, out var parsedRetryAfter))
                    {
                        retryDelaySeconds = parsedRetryAfter;
                    }
                    continue;
                }

                // Check for subscription/auth errors
                if (streamError?.Error?.Contains("subscription", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("access", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.RawJson?.Contains("40016") == true)
                {
                    // If we already have data from a previous endpoint, just log and continue
                    if (mergedData != null)
                    {
                        _logger?.LogWarning("Token invalidated on secondary endpoint, using data from primary endpoint");
                        continue;
                    }
                    if (streamError?.Error?.Contains("does not have access", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        throw new DownloadException("Premium subscription required. This content is only available to premium subscribers.", DownloadErrorType.PremiumContent);
                    }
                    if (streamError?.Error?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true ||
                        streamError?.Error?.Contains("ended", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        throw new DownloadException("Your Crunchyroll subscription has expired. Please renew your subscription.", DownloadErrorType.SubscriptionExpired);
                    }
                    throw new DownloadException("Subscription error: " + streamError?.Error, DownloadErrorType.SubscriptionExpired);
                }

                // Check for auth errors
                if (streamError?.RawJson?.Contains("401") == true ||
                    streamError?.Error?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("invalid token", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // If we already have data from a previous endpoint, just log and continue
                    if (mergedData != null)
                    {
                        _logger?.LogWarning("Auth error on secondary endpoint, using data from primary endpoint");
                        continue;
                    }
                    throw new DownloadException("Authentication failed. Please log in again.", DownloadErrorType.NotAuthenticated);
                }

                if (!string.IsNullOrEmpty(streamError?.Error))
                {
                    // [PT] Upstream: include the reason field in playback error output
                    _logger?.LogError("Playback API error: {Error} {Reason}", streamError.Error, streamError.Reason ?? "");
                }
            }
        }

        if (mergedData != null)
        {
            return mergedData;
        }

        if (rateLimited && retryAttempt < maxRetries)
        {
            _logger?.LogWarning("Playback API rate limited on all endpoints. Retrying in {Delay}s...", retryDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
            return await GetPlaybackDataAsync(episodeId, useBetaApi, cancellationToken, config, retryAttempt + 1);
        }

        throw new DownloadException("Failed to get playback data from all endpoints. The content may not be available in your region.", DownloadErrorType.NetworkError);
    }

    // Exponential backoff for playback rate-limit retries (upstream Helpers.GetRetryDelaySeconds):
    // base * 2^attempt, capped at the configured max.
    private static int GetRetryDelaySeconds(int retryAttempt, CruncharrConfig? config)
    {
        int baseDelay = Math.Max(1, config?.Download.PlaybackRateLimitRetryDelaySeconds ?? 30);
        int maxDelay = Math.Max(baseDelay, config?.Download.RetryMaxDelaySeconds ?? 3600);
        int attempt = Math.Max(0, retryAttempt);
        double delay = baseDelay * Math.Pow(2, attempt);
        return (int)Math.Min(maxDelay, delay);
    }

    private (bool Success, string? ErrorMessage) SelectStreamWithHardsub(PlaybackData playback, CruncharrConfig config)
    {
        var hsLang = config.Download.HardSubLang;
        var rawFallback = config.Download.HardSubRawFallback;

        _logger?.LogInformation("Stream selection: HardSubLang={HardSubLang}, RawFallback={RawFallback}", hsLang, rawFallback);

        if (string.IsNullOrEmpty(hsLang) || hsLang == "none")
        {
            // Use raw stream (no hardsubs)
            if (playback.HardSubs != null)
            {
                _logger?.LogInformation("Using raw stream (no hardsubs). Available hardsubs: {Available}",
                    string.Join(", ", playback.HardSubs.Keys));
            }
            playback.IsHardsubbed = false;
            playback.HardsubLang = null;
            return (true, null);
        }

        // Looking for hardsub stream
        if (playback.HardSubs == null || playback.HardSubs.Count == 0)
        {
            if (rawFallback)
            {
                _logger?.LogWarning("No hardsubs available for {Lang}, falling back to raw stream", hsLang);
                playback.IsHardsubbed = false;
                playback.HardsubLang = null;
                return (true, null);
            }
            _logger?.LogError("No hardsubs available for {Lang} and raw fallback is disabled", hsLang);
            return (false, $"No hardsubs available for {hsLang}. Available: none. Enable 'Hard Sub Raw Fallback' to use raw stream.");
        }

        // Try exact match
        var exactMatch = playback.HardSubs.FirstOrDefault(kvp =>
            kvp.Value.Hlang?.Equals(hsLang, StringComparison.OrdinalIgnoreCase) == true);

        if (exactMatch.Value != null)
        {
            _logger?.LogInformation("Found exact hardsub match: {Lang} -> {Url}", hsLang, exactMatch.Value.Url);
            playback.VideoUrl = exactMatch.Value.Url;
            playback.IsHardsubbed = true;
            playback.HardsubLang = hsLang;
            return (true, null);
        }

        // Try language code match (e.g., "en" for "en-US")
        var langPrefix = hsLang?.Split('-')[0].ToLowerInvariant() ?? "";
        var prefixMatch = playback.HardSubs.FirstOrDefault(kvp =>
            kvp.Value.Hlang?.Split('-')[0].ToLowerInvariant() == langPrefix);

        if (prefixMatch.Value != null)
        {
            _logger?.LogInformation("Found prefix hardsub match: {Lang} -> {ActualLang} -> {Url}",
                hsLang, prefixMatch.Value.Hlang, prefixMatch.Value.Url);
            playback.VideoUrl = prefixMatch.Value.Url;
            playback.IsHardsubbed = true;
            playback.HardsubLang = prefixMatch.Value.Hlang;
            return (true, null);
        }

        // No match found
        if (rawFallback)
        {
            _logger?.LogWarning("Hardsub {Lang} not available. Available: {Available}. Falling back to raw stream.",
                hsLang, string.Join(", ", playback.HardSubs.Values.Select(h => h.Hlang).Where(h => !string.IsNullOrEmpty(h))));
            playback.IsHardsubbed = false;
            playback.HardsubLang = null;
            return (true, null);
        }

        _logger?.LogError("Hardsub {Lang} not available. Available: {Available}",
            hsLang, string.Join(", ", playback.HardSubs.Values.Select(h => h.Hlang).Where(h => !string.IsNullOrEmpty(h))));
        return (false, $"Hardsub {hsLang} not available. Available: {string.Join(", ", playback.HardSubs.Values.Select(h => h.Hlang).Where(h => !string.IsNullOrEmpty(h)))}. Enable 'Hard Sub Raw Fallback' to use raw stream.");
    }

    private async Task DeAuthVideoAsync(string contentId, string videoToken)
    {
        try
        {
            var request = HttpClientWrapper.CreateRequest(
                $"https://cr-play-service.prd.crunchyrollsvc.com/v1/token/{contentId}/{videoToken}/inactive",
                HttpMethod.Patch,
                true,
                _auth.Token?.access_token);
            await _httpClient.SendRequestAsync(request, suppressError: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to de-auth video {ContentId}", contentId);
        }
    }

    // Upstream parity (CrunchyrollManager.DownloadSubtitles): "signs" = a non-CC subtitle whose
    // locale equals the audio locale of the playback version it was fetched from. Unknown origin
    // is treated as NOT signs — dropping a full dialogue track is far worse than keeping one
    // extra signs track. (internal for the guard test)
    internal static bool IsSignsSubtitle(SubtitleInfo s) =>
        !s.IsCC && !string.IsNullOrEmpty(s.SourceAudioLocale) &&
        string.Equals(s.Lang, s.SourceAudioLocale, StringComparison.OrdinalIgnoreCase);

    private async Task<PlaybackData?> ParsePlaybackDataAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            var playStream = JsonConvert.DeserializeObject<CrunchyStreamData>(content);
            if (playStream == null) return null;

            var playback = new PlaybackData();
            playback.VideoToken = playStream.Token;

            // Extract URL
            if (!string.IsNullOrEmpty(playStream.Url))
            {
                if (playStream.Url.Contains(".mpd") || playStream.Url.Contains("/dash/"))
                {
                    playback.VideoUrl = playStream.Url;
                }
                else
                {
                    var (video, audio, pssh) = await ParseHlsPlaylistAsync(playStream.Url, cancellationToken);
                    playback.VideoUrl = video;
                    playback.AudioUrl = audio;
                    playback.Pssh = pssh;
                }
            }

            // Extract subtitles
            if (playStream.Subtitles != null)
            {
                playback.Subtitles = new List<SubtitleInfo>();
                foreach (var sub in playStream.Subtitles)
                {
                    playback.Subtitles.Add(new SubtitleInfo
                    {
                        Lang = sub.Key,
                        Url = sub.Value.Url ?? "",
                        Format = sub.Value.Format ?? "vtt",
                        IsCC = false,
                        SourceAudioLocale = playStream.AudioLocale
                    });
                }
            }

            // [PT] Extract Closed Captions (upstream maps pbData.Meta.Captions as isCC=true).
            // Previously dropped entirely, so IncludeCcSubs could never surface a CC track.
            if (playStream.Captions != null)
            {
                playback.Subtitles ??= new List<SubtitleInfo>();
                foreach (var cap in playStream.Captions)
                {
                    var capLang = cap.Value.Language ?? cap.Key;
                    playback.Subtitles.Add(new SubtitleInfo
                    {
                        Lang = capLang,
                        Url = cap.Value.Url ?? "",
                        Format = cap.Value.Format ?? "vtt",
                        IsCC = true,
                        SourceAudioLocale = playStream.AudioLocale
                    });
                }
            }

            // Extract hardsubs
            if (playStream.HardSubs != null)
            {
                playback.HardSubs = playStream.HardSubs;
            }

            return playback;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse playback data");
            return null;
        }
    }

    private async Task<(string? Video, string? Audio, string? Pssh)> ParseHlsPlaylistAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);

            if (!isOk)
            {
                return (null, null, null);
            }

            // Simple HLS parsing - look for video and audio variant playlists
            var lines = content.Split('\n');
            string? videoUrl = null;
            string? audioUrl = null;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("VIDEO"))
                {
                    // Video stream
                    if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                    {
                        videoUrl = lines[i + 1].Trim();
                        if (!videoUrl.StartsWith("http"))
                        {
                            var baseUri = new Uri(playlistUrl);
                            videoUrl = new Uri(baseUri, videoUrl).ToString();
                        }
                    }
                }
                if (lines[i].Contains("AUDIO"))
                {
                    // Audio stream
                    if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                    {
                        audioUrl = lines[i + 1].Trim();
                        if (!audioUrl.StartsWith("http"))
                        {
                            var baseUri = new Uri(playlistUrl);
                            audioUrl = new Uri(baseUri, audioUrl).ToString();
                        }
                    }
                }
            }

            // If no video/audio found, the playlist might be a media playlist itself
            if (videoUrl == null && audioUrl == null)
            {
                // Check if this is a media playlist with segments
                if (lines.Any(l => l.StartsWith("#EXTINF")))
                {
                    videoUrl = playlistUrl;
                }
            }

            // Check for DRM PSSH in HLS key tags
            string? pssh = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-KEY") && line.Contains("URI=\"data:text/plain;base64,"))
                {
                    var match = Regex.Match(line, "URI=\"data:text/plain;base64,([^\"]+)\"");
                    if (match.Success)
                    {
                        pssh = match.Groups[1].Value;
                        break;
                    }
                }
            }

            return (videoUrl, audioUrl, pssh);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse HLS playlist");
            return (null, null, null);
        }
    }

    private async Task DownloadStreamAsync(string url, string outputPath, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken, string? videoToken = null)
    {
        // Direct file download - stream to disk without buffering entire file in memory
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(outputPath);

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[8192];
        long downloaded = 0;
        long lastReportedBytes = 0;
        var lastReportTime = DateTime.UtcNow;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;

            if (totalBytes > 0 && progress != null)
            {
                var now = DateTime.UtcNow;
                var elapsedMs = (now - lastReportTime).TotalMilliseconds;
                // Report progress every ~500ms to avoid flooding
                if (elapsedMs >= 500)
                {
                    var percent = startPercent + (downloaded / (double)totalBytes) * (endPercent - startPercent);
                    var incrementalBytes = downloaded - lastReportedBytes;
                    var speedBytesPerSec = elapsedMs > 0 ? incrementalBytes / (elapsedMs / 1000.0) : 0;
                    if (speedBytesPerSec < 1) speedBytesPerSec = 1;

                    var remainingBytes = totalBytes - downloaded;
                    var etaSec = speedBytesPerSec > 0 ? remainingBytes / speedBytesPerSec : 0;
                    if (etaSec > TimeSpan.MaxValue.TotalSeconds) etaSec = TimeSpan.MaxValue.TotalSeconds;

                    progress.Report(new DownloadProgress
                    {
                        State = DownloadState.Downloading,
                        Percent = percent,
                        DownloadSpeedBytes = (long)speedBytesPerSec,
                        Time = etaSec
                    });

                    lastReportedBytes = downloaded;
                    lastReportTime = now;
                }
            }
        }
    }

    private async Task<(string? VideoPath, List<(string Path, string Lang)> AudioPaths)> DownloadDashTracksAsync(string manifestUrl, string tempDir, CruncharrConfig config, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken, string? videoToken = null, string? mediaGuid = null, List<string>? selectedDubs = null, bool audioOnly = false)
    {
        // Download manifest with auth headers
        var manifestRequest = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        manifestRequest.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");
        // [PT] Use the active stream-endpoint UA so the manifest request matches the play client
        // instead of a hardcoded (and quickly stale) TV UA. Falls back to the current TV default.
        var manifestUserAgent = !string.IsNullOrEmpty(_auth.StreamEndpoint?.UserAgent)
            ? _auth.StreamEndpoint.UserAgent
            : "ANDROIDTV/3.66.0_22348 Android/16";
        manifestRequest.Headers.Add("User-Agent", manifestUserAgent);
        if (!string.IsNullOrEmpty(videoToken))
        {
            manifestRequest.Headers.Add("x-cr-video-token", videoToken);
        }

        var (isOk, manifestContent, error) = await _httpClient.SendRequestAsync(manifestRequest);
        if (!isOk || string.IsNullOrEmpty(manifestContent))
        {
            throw new Exception($"Failed to download DASH manifest: {error}");
        }

        // Parse manifest using qma source parser directly
        var streamPlaylists = await Cruncharr.Core.Utils.Parser.MpdParser.Parse(manifestContent, null, manifestUrl, _httpClient.Client);

        // Merge all server data
        var videoItems = new List<Cruncharr.Core.Utils.Parser.VideoItem>();
        var audioItems = new List<Cruncharr.Core.Utils.Parser.AudioItem>();

        foreach (var serverData in streamPlaylists.Data.Values)
        {
            if (serverData.video != null)
            {
                foreach (var vp in serverData.video)
                {
                    videoItems.Add(new Cruncharr.Core.Utils.Parser.VideoItem
                    {
                        bandwidth = vp.bandwidth,
                        codecs = vp.codecs,
                        quality = vp.quality ?? new Cruncharr.Core.Utils.Parser.Quality(),
                        resolutionText = $"{vp.quality?.width ?? 0}x{vp.quality?.height ?? 0}",
                        segments = vp.segments,
                        pssh = vp.pssh,
                        encryptionKeys = vp.encryptionKeys
                    });
                }
            }
            if (serverData.audio != null)
            {
                foreach (var ap in serverData.audio)
                {
                    audioItems.Add(new Cruncharr.Core.Utils.Parser.AudioItem
                    {
                        bandwidth = ap.bandwidth,
                        language = ap.language,
                        audioSamplingRate = ap.audioSamplingRate,
                        @default = ap.@default,
                        segments = ap.segments,
                        pssh = ap.pssh,
                        encryptionKeys = ap.encryptionKeys,
                        resolutionText = $"{Math.Round(ap.bandwidth / 1000.0)}kB/s",
                        resolutionTextSnap = $"{SnapToAudioBucket(ToKbps(ap.bandwidth))}kB/s"
                    });
                }
            }
        }

        if (videoItems.Count == 0 && audioItems.Count == 0)
        {
            throw new Exception("No video or audio tracks found in DASH manifest");
        }

        _logger?.LogInformation("Manifest has {VideoCount} video tracks and {AudioCount} audio tracks", videoItems.Count, audioItems.Count);

        // Select video/audio tracks using ported upstream logic
        var chosenVideo = SelectVideoTrackQma(videoItems, config.Download.QualityVideo, config);
        var chosenAudios = SelectAudioTracksUpstream(audioItems, selectedDubs?.Count > 0
            ? selectedDubs
            : config.Download.DubLanguages);

        // Apply QualityAudio filter (ported from upstream DownloadMediaList lines 1874-1895)
        chosenAudios = FilterAudioByQuality(chosenAudios, config.Download.QualityAudio);

        _logger?.LogInformation("Selected {AudioCount} audio tracks for download", chosenAudios.Count);

        string? videoPath = null;
        var audioPaths = new List<(string Path, string Lang)>();

        // Download video using HlsDownloader (qma approach). Skipped for additional
        // dubs (audioOnly): the video is downloaded once from the primary version.
        if (chosenVideo != null && !audioOnly)
        {
            var videoOutput = chosenVideo.pssh != null
                ? Path.Combine(tempDir, "video.enc.m4s")
                : Path.Combine(tempDir, "video.m4s");
            videoPath = Path.Combine(tempDir, "video.m4s");

            var videoJson = new Cruncharr.Core.Utils.HLS.M3U8Json
            {
                Segments = chosenVideo.segments?.Cast<dynamic>().ToList() ?? new List<dynamic>()
            };

            var videoDownloader = new Cruncharr.Core.Utils.HLS.HlsDownloader(
                new Cruncharr.Core.Utils.HLS.HlsOptions
                {
                    Output = videoOutput,
                    M3U8Json = videoJson,
                    Threads = config.Download.PartSize > 0 ? config.Download.PartSize : 5,
                    Retries = config.Download.RetryAttempts,
                    Timeout = config.Download.Timeout > 0 ? config.Download.Timeout : 15000,
                    FsRetryTime = config.Download.RetryDelay * 1000
                },
                true, false, config.Download.DownloadMethodeNew,
                _httpClient.Client, config, progress, cancellationToken);

            _logger?.LogInformation("Downloading video stream to {Path}", videoOutput);
            var videoResult = await videoDownloader.Download();

            if (!videoResult.Ok)
            {
                throw new Exception("Video track download failed");
            }

            // Decrypt if needed
            if (chosenVideo.pssh != null && _widevine.CanDecrypt)
            {
                var authData = new Dictionary<string, string>{
                    { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                    { "x-cr-content-id", mediaGuid ?? "" },
                    { "x-cr-video-token", videoToken ?? "" }
                };

                var keys = await _widevine.GetKeysAsync(chosenVideo.pssh, ApiUrls.WidevineLicenceUrl, authData, _httpClient.Client);
                if (keys.Count > 0)
                {
                    await DecryptWithMp4Decrypt(videoOutput, videoPath, keys);
                }
                else
                {
                    _logger?.LogWarning("No decryption keys obtained, video may be unplayable");
                    videoPath = videoOutput; // Return encrypted file path since decryption failed
                }
            }
            else if (chosenVideo.pssh != null)
            {
                videoPath = videoOutput; // Return encrypted file path since we can't decrypt
            }
        }

        // Download audio tracks using HlsDownloader (qma approach)
        if (chosenAudios.Count > 0)
        {
            double audioStartPercent = startPercent + (endPercent - startPercent) * 0.6;
            double audioRange = (endPercent - startPercent) * 0.4;
            double perAudioPercent = audioRange / chosenAudios.Count;

            for (int i = 0; i < chosenAudios.Count; i++)
            {
                var (audioItem, lang) = chosenAudios[i];
                // Crunchyroll's dub manifest labels its audio track with the ORIGINAL locale (ja-JP),
                // not the dub's. So a single-dub download (e.g. user picks English-only) gets its
                // audio downloaded + MUXED as ja-JP even though the content is the English dub. When
                // exactly ONE dub is requested and ONE audio track is chosen (primary OR additional-
                // dub fetch), trust the requested dub locale so the track is labelled correctly.
                // Multi-dub downloads keep their per-track labels (condition requires Count==1).
                if (selectedDubs is { Count: 1 } && !string.IsNullOrEmpty(selectedDubs[0]) && chosenAudios.Count == 1)
                {
                    lang = selectedDubs[0];
                }
                var langCode = (lang ?? "unknown").Replace("-", "").ToLower();
                var audioFileName = chosenAudios.Count(a => a.Item2 == lang) > 1
                    ? $"audio_{langCode}_{i}.m4s"
                    : $"audio_{langCode}.m4s";
                var audioEncFileName = chosenAudios.Count(a => a.Item2 == lang) > 1
                    ? $"audio_{langCode}_{i}.enc.m4s"
                    : $"audio_{langCode}.enc.m4s";
                var audioOutput = audioItem.pssh != null
                    ? Path.Combine(tempDir, audioEncFileName)
                    : Path.Combine(tempDir, audioFileName);
                var audioFinalPath = Path.Combine(tempDir, audioFileName);

                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = audioStartPercent + (i * perAudioPercent), Doing = $"Downloading audio ({lang})..." });

                var audioJson = new Cruncharr.Core.Utils.HLS.M3U8Json
                {
                    Segments = audioItem.segments?.Cast<dynamic>().ToList() ?? new List<dynamic>()
                };

                var audioDownloader = new Cruncharr.Core.Utils.HLS.HlsDownloader(
                    new Cruncharr.Core.Utils.HLS.HlsOptions
                    {
                        Output = audioOutput,
                        M3U8Json = audioJson,
                        Threads = config.Download.PartSize > 0 ? config.Download.PartSize : 5,
                        Retries = config.Download.RetryAttempts,
                        Timeout = config.Download.Timeout > 0 ? config.Download.Timeout : 15000,
                        FsRetryTime = config.Download.RetryDelay * 1000
                    },
                    false, true, config.Download.DownloadMethodeNew,
                    _httpClient.Client, config, progress, cancellationToken);

                _logger?.LogInformation("Downloading audio stream ({Lang}) to {Path}", lang, audioOutput);
                var audioResult = await audioDownloader.Download();

                if (audioResult.Ok)
                {
                    var audioReturnPath = audioFinalPath;

                    // Decrypt if needed
                    if (audioItem.pssh != null && _widevine.CanDecrypt)
                    {
                        var authData = new Dictionary<string, string>{
                            { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                            { "x-cr-content-id", mediaGuid ?? "" },
                            { "x-cr-video-token", videoToken ?? "" }
                        };

                        var keys = await _widevine.GetKeysAsync(audioItem.pssh, ApiUrls.WidevineLicenceUrl, authData, _httpClient.Client);
                        if (keys.Count > 0)
                        {
                            await DecryptWithMp4Decrypt(audioOutput, audioFinalPath, keys);
                        }
                        else
                        {
                            _logger?.LogWarning("No decryption keys obtained for audio, may be unplayable");
                            audioReturnPath = audioOutput; // Return encrypted file path since decryption failed
                        }
                    }
                    else if (audioItem.pssh != null)
                    {
                        audioReturnPath = audioOutput; // Return encrypted file path since we can't decrypt
                    }

                    audioPaths.Add((audioReturnPath, lang ?? "unknown"));
                }
                else
                {
                    _logger?.LogWarning("Audio track download failed for language {Lang}", lang);
                }
            }
        }

        // NOTE: Audio Description (AD) tracks for DASH are handled at the episode preparation level
        // (similar to upstream DownloadMediaList lines 1306-1332). The AD version should be added
        // to episode.Versions before calling DownloadEpisodeAsync. When present in the manifest,
        // they will be downloaded as part of the normal audio track selection above.

        return (videoPath, audioPaths);
    }

    private List<Cruncharr.Core.Utils.Parser.VideoItem> DeduplicateVideoTracks(List<Cruncharr.Core.Utils.Parser.VideoItem> videos)
    {
        return videos
            .GroupBy(v => new { v.quality?.height, WB = WidthBucket(v.quality?.width ?? 0, v.quality?.height ?? 0) })
            .Select(g => g.OrderByDescending(v => v.bandwidth).First())
            .OrderBy(v => v.quality?.height)
            .ThenBy(v => v.bandwidth)
            .ToList();
    }

    // Ported from upstream Helpers.WidthBucket
    // Normalizes widths that are approximately 16:9 to the expected 16:9 width,
    // while keeping non-standard widths as-is. Used for video deduplication.
    private static int WidthBucket(int width, int height)
    {
        if (height == 0) return width;
        int expected = (int)Math.Round(height * 16 / 9.0);
        int tol = Math.Max(8, (int)(expected * 0.02)); // ~2% or >=8 px
        return Math.Abs(width - expected) <= tol ? expected : width;
    }

    private Cruncharr.Core.Utils.Parser.VideoItem? SelectVideoTrackQma(List<Cruncharr.Core.Utils.Parser.VideoItem> videos, string qualityPreference, CruncharrConfig config)
    {
        if (videos.Count == 0) return null;

        var deduped = DeduplicateVideoTracks(videos);

        // [PT] Ported from upstream: Kstream selects specific stream by 1-based index
        if (config.Download.Kstream > 0 && config.Download.Kstream <= deduped.Count)
        {
            var selected = deduped[config.Download.Kstream - 1];
            _logger?.LogInformation("Using Kstream selection: index {Index}, height {Height}, resolution {Resolution}",
                config.Download.Kstream, selected.quality?.height, selected.resolutionText);
            return selected;
        }

        if (string.IsNullOrWhiteSpace(qualityPreference))
        {
            qualityPreference = "best";
        }

        int dedupedCount = deduped.Count;
        int chosenIndex;
        if (qualityPreference == "best")
        {
            chosenIndex = dedupedCount;
        }
        else if (qualityPreference == "worst")
        {
            chosenIndex = 1;
        }
        else
        {
            var heightStr = qualityPreference.Replace("p", "").Trim();
            if (int.TryParse(heightStr, out var targetHeight))
            {
                var matchIndex = deduped.FindIndex(v => v.quality?.height == targetHeight);
                if (matchIndex >= 0)
                {
                    chosenIndex = matchIndex + 1;
                }
                else
                {
                    chosenIndex = dedupedCount;
                }
            }
            else
            {
                chosenIndex = dedupedCount;
            }
        }

        if (chosenIndex > dedupedCount)
        {
            chosenIndex = dedupedCount;
        }

        return deduped[chosenIndex - 1];
    }

    // Ported from upstream CrunchyrollManager.cs DownloadMediaList
    // Selects audio tracks matching configured DubLanguages, deduplicated by language+bandwidth bucket
    private List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> SelectAudioTracksUpstream(
        List<Cruncharr.Core.Utils.Parser.AudioItem> audioTracks, List<string> languages)
    {
        if (audioTracks.Count == 0 || languages.Count == 0) return [];

        // Upstream deduplication: group by language + bandwidth bucket, pick best in each group
        var deduped = audioTracks
            .Select(a => new
            {
                Item = a,
                Lang = string.IsNullOrWhiteSpace(a.language?.CrLocale) ? "und" : a.language.CrLocale,
                Bucket = SnapToAudioBucket(ToKbps(a.bandwidth))
            })
            .GroupBy(x => new { x.Lang, x.Bucket })
            .Select(g => g.OrderByDescending(x => x.Item.@default)
                .ThenByDescending(x => x.Item.audioSamplingRate)
                .ThenByDescending(x => x.Item.bandwidth)
                .First().Item)
            .ToList();

        // Sort by configured DubLanguages order
        var rank = languages
            .Select((val, i) => new { val, i })
            .ToDictionary(x => x.val.ToLowerInvariant(), x => x.i, StringComparer.OrdinalIgnoreCase);

        var sorted = deduped
            .OrderBy(a =>
            {
                var key = a.language?.CrLocale ?? string.Empty;
                return rank.TryGetValue(key, out var r) ? r : int.MaxValue;
            })
            .ToList();

        // Filter to the REQUESTED languages. Crunchyroll's per-dub DASH manifest frequently
        // bundles the original (ja-JP) audio alongside the dub's audio (e.g. the en-US version
        // manifest carries both en-US@136.119 and ja-JP@136.031). Upstream takes a SINGLE audio
        // track per version's manifest; the previous port returned EVERY track and kept one per
        // language group, so requesting only "en-US" still muxed the Japanese track — and if CR
        // flags it default the player plays Japanese. Keep only tracks whose locale was asked for.
        var requested = new HashSet<string>(
            languages.Where(l => !string.IsNullOrWhiteSpace(l)), StringComparer.OrdinalIgnoreCase);
        var filtered = sorted
            .Where(a => requested.Contains(a.language?.CrLocale ?? "und"))
            .ToList();

        // Fallback: if NOTHING matched, CR mislabelled this dub's audio with the original locale
        // (the known dub-manifest mislabel) so no track carries the requested locale. Return all
        // tracks unchanged so the single-track relabel path (audio loop, selectedDubs Count==1)
        // can still tag it with the requested dub. Only this all-mislabelled case falls through.
        var chosen = filtered.Count > 0 ? filtered : sorted;

        return chosen.Select(a => (a, a.language?.CrLocale ?? "und")).ToList();
    }

    // Ported from upstream Helpers.SnapToAudioBucket
    private static int SnapToAudioBucket(double kbps)
    {
        var buckets = new[] { 32, 64, 96, 128, 160, 192, 256, 320, 500 };
        foreach (var bucket in buckets.OrderBy(b => b))
        {
            if (kbps <= bucket) return bucket;
        }
        return buckets.Last();
    }

    // Ported from upstream Helpers.ToKbps
    private static double ToKbps(long bandwidth) => bandwidth / 1000.0;

    // Ported from upstream DownloadMediaList lines 1874-1895
    // Filters audio tracks by QualityAudio setting (best, worst, or specific bandwidth)
    private List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> FilterAudioByQuality(
        List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> audioTracks, string qualityPreference)
    {
        if (audioTracks.Count == 0) return audioTracks;

        // Group by language
        var grouped = audioTracks.GroupBy(a => a.Language).ToList();
        var result = new List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)>();

        foreach (var group in grouped)
        {
            var tracks = group.OrderBy(a => a.Track.bandwidth).ToList();

            int chosenIndex;
            if (qualityPreference == "best")
            {
                chosenIndex = tracks.Count - 1; // Last = highest bandwidth
            }
            else if (qualityPreference == "worst")
            {
                chosenIndex = 0; // First = lowest bandwidth
            }
            else
            {
                // Try to match specific quality (e.g., "128kB/s" or bucket string)
                var matchIndex = tracks.FindIndex(a =>
                    a.Track.resolutionTextSnap?.Equals(qualityPreference, StringComparison.OrdinalIgnoreCase) == true ||
                    a.Track.resolutionText?.Equals(qualityPreference, StringComparison.OrdinalIgnoreCase) == true);
                if (matchIndex >= 0)
                {
                    chosenIndex = matchIndex;
                }
                else
                {
                    chosenIndex = tracks.Count - 1; // Fallback to best
                }
            }

            if (chosenIndex >= 0 && chosenIndex < tracks.Count)
            {
                result.Add(tracks[chosenIndex]);
                _logger?.LogInformation("QualityAudio [{Quality}]: Selected {Bandwidth}kbps for {Language}",
                    qualityPreference, ToKbps(tracks[chosenIndex].Track.bandwidth), group.Key);
            }
        }

        return result;
    }

    private async Task DecryptWithMp4Decrypt(string inputPath, string outputPath, List<ContentKey> keys)
    {
        if (keys.Count == 0) return;

        // Find decryptor tool (prefer shaka-packager, fallback to mp4decrypt)
        string? decryptToolPath = null;
        bool useShaka = false;

        var mp4decryptPath = FindExecutable("mp4decrypt");
        var shakaPath = FindExecutable("shaka-packager");

        if (shakaPath != null)
        {
            decryptToolPath = shakaPath;
            useShaka = true;
        }
        else if (mp4decryptPath != null)
        {
            decryptToolPath = mp4decryptPath;
        }
        else
        {
            _logger?.LogError("No decryptor found (mp4decrypt or shaka-packager). Cannot decrypt {Input}", inputPath);
            return;
        }

        _logger?.LogInformation("Decrypting {Input} -> {Output} using {Tool}", inputPath, outputPath, useShaka ? "shaka-packager" : "mp4decrypt");

        if (useShaka)
        {
            var shakaKeys = BuildShakaKeysParam(keys);
            var streamType = inputPath.Contains("audio") ? "audio" : "video";
            var args = new List<string>{
                $"input=\"{inputPath}\",stream={streamType},output=\"{outputPath}\"",
                shakaKeys
            };
            await RunProcessAsync(decryptToolPath!, args, CancellationToken.None);
        }
        else
        {
            var args = new List<string> { "--show-progress" };
            foreach (var key in keys)
            {
                args.Add("--key");
                args.Add($"{FormatKey(key.KeyID)}:{FormatKey(key.Bytes)}");
            }
            args.Add(inputPath);
            args.Add(outputPath);
            await RunProcessAsync(decryptToolPath!, args, CancellationToken.None);
        }

        if (File.Exists(outputPath))
        {
            _logger?.LogInformation("Decryption complete: {Output}", outputPath);
            // Clean up encrypted file
            try
            {
                File.Delete(inputPath);
                File.Delete(inputPath + ".resume");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        else
        {
            _logger?.LogError("Decryption failed for {Input} - output not found", inputPath);
        }
    }

    private async Task<List<string>> DecryptFilesAsync(List<string> encryptedFiles, List<ContentKey> keys, CancellationToken cancellationToken)
    {
        var decryptedFiles = new List<string>();

        // Find decryptor tool
        string? decryptToolPath = null;
        bool useShaka = false;

        // Check for mp4decrypt first, then shaka-packager
        var mp4decryptPath = FindExecutable("mp4decrypt");
        var shakaPath = FindExecutable("shaka-packager");

        if (shakaPath != null)
        {
            decryptToolPath = shakaPath;
            useShaka = true;
        }
        else if (mp4decryptPath != null)
        {
            decryptToolPath = mp4decryptPath;
        }
        else
        {
            _logger?.LogWarning("No decryptor found (mp4decrypt or shaka-packager). Files remain encrypted.");
            return encryptedFiles;
        }

        foreach (var file in encryptedFiles)
        {
            // Skip files that are already decrypted (no .enc extension)
            if (!file.Contains(".enc"))
            {
                _logger?.LogDebug("Skipping already-decrypted file: {File}", file);
                decryptedFiles.Add(file);
                continue;
            }

            var decryptedPath = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file).Replace(".enc", "") + Path.GetExtension(file));
            _logger?.LogInformation("Decrypting {File} -> {DecryptedPath}", file, decryptedPath);

            if (useShaka)
            {
                // Shaka-packager command
                var shakaKeys = BuildShakaKeysParam(keys);
                var streamType = file.Contains("audio") ? "audio" : "video";
                var args = new List<string>{
                    $"input=\"{file}\",stream={streamType},output=\"{decryptedPath}\"",
                    shakaKeys
                };

                _logger?.LogInformation("Running shaka-packager: {Args}", string.Join(" ", args));
                await RunProcessAsync(decryptToolPath!, args, cancellationToken);
            }
            else
            {
                // mp4decrypt command
                var args = new List<string> { "--show-progress" };
                foreach (var key in keys)
                {
                    args.Add("--key");
                    args.Add($"{FormatKey(key.KeyID)}:{FormatKey(key.Bytes)}");
                }
                args.Add(file);
                args.Add(decryptedPath);

                _logger?.LogInformation("Running mp4decrypt with {KeyCount} keys", keys.Count);
                await RunProcessAsync(decryptToolPath!, args, cancellationToken);
            }

            if (File.Exists(decryptedPath))
            {
                _logger?.LogInformation("Decryption successful: {DecryptedPath}", decryptedPath);
                decryptedFiles.Add(decryptedPath);
                // Clean up encrypted file
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            else
            {
                _logger?.LogError("Decryption failed for {File} - output not found", file);
                decryptedFiles.Add(file);
            }
        }

        return decryptedFiles;
    }

    private static string BuildShakaKeysParam(List<ContentKey> keys) =>
        "--enable_raw_key_decryption " + string.Join(" ",
            keys.Select(k => $"--keys key_id={FormatKey(k.KeyID)}:key={FormatKey(k.Bytes)}"));

    private static string FormatKey(byte[] keyBytes) =>
        BitConverter.ToString(keyBytes).Replace("-", "").ToLower();

    private async Task MuxFilesAsync(List<string> mediaFiles, List<(string Path, string Lang)> audioTracks, List<(string Path, string Lang, bool Cc, bool Signs)> subtitles, string? chapterFile, List<FontAttachment> fonts, string? coverPath, string outputPath, CruncharrConfig config, CancellationToken cancellationToken, Dictionary<string, int>? audioDelays = null, Dictionary<string, string>? videoLocales = null, string? descriptionPath = null, string? preferredAudioLang = null)
    {
        // The DEFAULT audio track (what a player auto-plays) is the user's chosen dub for THIS
        // download, not the global DefaultAudio. Without this, picking English in Add Download but
        // leaving the config default at ja-JP muxes en+ja and flags ja-JP default -> plays Japanese
        // even though English is present ("I selected English, it plays Japanese"). Fall back to the
        // config default when no per-download dub was chosen (e.g. a bare series/season add).
        var defaultAudioLocale = !string.IsNullOrWhiteSpace(preferredAudioLang)
            ? preferredAudioLang
            : config.Download.DefaultAudio;
        var mergerOptions = new MergerOptions
        {
            Output = outputPath,
            VideoTitle = config.Download.VideoTitle ?? "",
            DubLangList = config.Download.DubLanguages,
            SubLangList = config.Download.SoftSubs,
            SkipSubMux = config.Download.SkipSubMux,
            CcSubsMuxingFlag = config.Download.CcSubsMuxingFlag,
            SignsSubsAsForced = config.Download.SignsSubsAsForced,
            DefaultSubSigns = config.Download.MuxDefaultSubSigns,
            DefaultSubForcedDisplay = config.Download.MuxDefaultSubForcedDisplay,
            CcTag = config.Download.CcTag,
            KeepAllVideos = videoLocales != null && videoLocales.Count > 1,
            Options = new MuxOptions
            {
                Ffmpeg = config.Download.FfmpegOptions,
                Mkvmerge = config.Download.MkvmergeOptions
            },
            Defaults = new Defaults
            {
                Video = Languages.FindLang(config.Download.DefaultVideo),
                Audio = Languages.FindLang(defaultAudioLocale),
                Sub = Languages.FindLang(config.Download.DefaultSub)
            }
        };

        // Map video and audio files
        _logger?.LogDebug("MUX: mediaFiles count={Count}, audioTracks count={AudioCount}", mediaFiles.Count, audioTracks.Count);
        foreach (var f in mediaFiles) _logger?.LogDebug("MUX: mediaFile: {File}", f);
        foreach (var a in audioTracks) _logger?.LogDebug("MUX: audioTrack: {Path} / {Lang}", a.Path, a.Lang);

        foreach (var file in mediaFiles)
        {
            var audioTrack = audioTracks.FirstOrDefault(a => a.Path == file);
            if (audioTrack != default)
            {
                // Audio file
                _logger?.LogDebug("MUX: Adding to OnlyAudio: {File} ({Lang})", file, audioTrack.Lang);
                var mergerInput = new MergerInput
                {
                    Path = file,
                    Language = Languages.FindLang(audioTrack.Lang)
                };
                // Apply sync delay if available
                if (audioDelays != null && audioDelays.TryGetValue(audioTrack.Lang, out var delay))
                {
                    mergerInput.Delay = delay;
                    _logger?.LogDebug("MUX: Applying sync delay {Delay}ms to audio: {Lang}", delay, audioTrack.Lang);
                }
                mergerOptions.OnlyAudio.Add(mergerInput);
            }
            else
            {
                // Video-only file
                var vidLang = videoLocales?.TryGetValue(file, out var vl) == true ? Languages.FindLang(vl) : Languages.DEFAULT_lang;
                _logger?.LogDebug("MUX: Adding to OnlyVid: {File} ({Lang})", file, vidLang?.CrLocale ?? "default");
                mergerOptions.OnlyVid.Add(new MergerInput
                {
                    Path = file,
                    Language = vidLang ?? Languages.DEFAULT_lang
                });
            }
        }

        _logger?.LogDebug("MUX: OnlyVid.Count={OnlyVid}, OnlyAudio.Count={OnlyAudio}, Subtitles.Count={Subs}",
            mergerOptions.OnlyVid.Count, mergerOptions.OnlyAudio.Count, mergerOptions.Subtitles.Count);

        // Map subtitles. CC and Signs flags are determined at download time and carried
        // here so SignsSubsAsForced / DefaultSubSigns / the CC muxing flag all work
        // (every sub was previously muxed with Cc=false, Signs=false).
        foreach (var (path, lang, cc, signs) in subtitles)
        {
            mergerOptions.Subtitles.Add(new SubtitleInput
            {
                File = path,
                Language = Languages.FindLang(lang),
                ClosedCaption = cc,
                Signs = signs
            });
        }

        // Map chapter file
        if (!string.IsNullOrEmpty(chapterFile))
        {
            mergerOptions.Chapters.Add(new MergerInput
            {
                Path = chapterFile
            });
        }

        // Map cover
        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            mergerOptions.Cover.Add(new MergerInput
            {
                Path = coverPath
            });
        }

        // Map description
        if (!string.IsNullOrEmpty(descriptionPath) && File.Exists(descriptionPath))
        {
            mergerOptions.Description.Add(new MergerInput
            {
                Path = descriptionPath
            });
        }

        // Map fonts
        foreach (var font in fonts)
        {
            mergerOptions.Fonts.Add(new ParsedFont
            {
                Name = font.Name,
                Path = font.Path,
                Mime = font.Mime
            });
        }

        var merger = new Merger(mergerOptions);

        var mkvmergePath = FindExecutable("mkvmerge");
        var ffmpegPath = FindExecutable("ffmpeg");

        // MP4 output MUST go through ffmpeg: mkvmerge only writes Matroska, so using it
        // for a .mp4 target produced an MKV stream in a .mp4 file (unplayable as MP4, and
        // ASS subtitles can't live in MP4). MKV output prefers mkvmerge, ffmpeg fallback.
        bool success = false;
        if (config.Download.MuxMp4)
        {
            if (ffmpegPath != null)
            {
                success = await merger.Merge("ffmpeg", ffmpegPath, cancellationToken);
            }
            else
            {
                _logger?.LogWarning("MuxMp4 is enabled but ffmpeg was not found; cannot mux to MP4.");
            }
        }
        else
        {
            if (mkvmergePath != null)
            {
                success = await merger.Merge("mkvmerge", mkvmergePath, cancellationToken);
            }
            if (!success && ffmpegPath != null)
            {
                success = await merger.Merge("ffmpeg", ffmpegPath, cancellationToken);
            }
        }

        if (!success)
        {
            _logger?.LogWarning("Muxing failed. Files left in temp directory.");
        }
        else if (!config.Download.NoCleanup)
        {
            merger.CleanUp();
        }
    }

    private static string EscapeProcessArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        if (!arg.Contains(' ') && !arg.Contains('"') && !arg.Contains('\\')) return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private async Task RunProcessAsync(string executable, List<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", args.Select(EscapeProcessArgument)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger?.LogDebug("Running: {Executable} {Args}", executable, startInfo.Arguments);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            _logger?.LogError("Failed to start process: {Executable}", executable);
            return;
        }

        // Read stdout/stderr concurrently to avoid deadlocks on full buffers
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // Kill the child on cancellation — WaitForExitAsync alone only abandons the wait,
        // leaving ffmpeg/decrypt running orphaned after a queue-item cancel.
        await using var killRegistration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignored */ }
        });

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger?.LogError("Process failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            if (!string.IsNullOrEmpty(output))
            {
                _logger?.LogError("Process output: {Output}", output);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(output))
            {
                _logger?.LogDebug("Process output: {Output}", output);
            }
            if (!string.IsNullOrEmpty(error))
            {
                _logger?.LogDebug("Process stderr: {Error}", error);
            }
        }
    }

    private async Task<string> RunProcessWithOutputAsync(string executable, List<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", args.Select(EscapeProcessArgument)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger?.LogDebug("Running: {Executable} {Args}", executable, startInfo.Arguments);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            _logger?.LogError("Failed to start process: {Executable}", executable);
            return "";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await using var killRegistration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignored */ }
        });

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger?.LogError("Process failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            return "";
        }

        return output.Trim();
    }

    private async Task<(int? Height, int? Width)> ProbeVideoResolutionAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(videoPath)) return (null, null);

            var ffprobePath = FindExecutable("ffprobe") ?? "ffprobe";
            var args = new List<string>{
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0",
                videoPath
            };

            var output = await RunProcessWithOutputAsync(ffprobePath, args, cancellationToken);
            if (string.IsNullOrEmpty(output)) return (null, null);

            var parts = output.Split(',');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0].Trim(), out var width) &&
                int.TryParse(parts[1].Trim(), out var height))
            {
                _logger?.LogInformation("Probed video resolution: {Width}x{Height}", width, height);
                return (height, width);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to probe video resolution for {Path}", videoPath);
        }
        return (null, null);
    }

    // Move a finished file from the temp working dir to its final location. tempDir may live on
    // a different filesystem than the output dir (e.g. tmpfs vs the SSD mount), where File.Move's
    // rename() fails with a cross-device error; fall back to copy+delete in that case.
    private void MoveToFinalPath(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
        {
            _logger?.LogWarning("Expected output {Source} not found; cannot move to {Dest}", sourcePath, destPath);
            return;
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        if (File.Exists(destPath)) File.Delete(destPath);

        try
        {
            File.Move(sourcePath, destPath);
        }
        catch (IOException)
        {
            // Cross-device (EXDEV) or similar: copy then remove the source.
            File.Copy(sourcePath, destPath, overwrite: true);
            File.Delete(sourcePath);
        }
        _logger?.LogInformation("Moved output to {Dest}", destPath);
    }

    // Acquire a transcode slot before encoding so the CPU-heavy encode step honors the
    // separate "max simultaneous transcodes" limit (downloads/muxes stay parallel; encodes
    // serialize to the configured limit). Always released, even on cancel/failure.
    private async Task EncodeOutputWithLimitAsync(string inputPath, string presetName, CancellationToken cancellationToken, IProgress<DownloadProgress>? progress = null, string? label = null)
    {
        bool held = false;
        try
        {
            if (_queueService != null)
            {
                progress?.Report(new DownloadProgress { State = DownloadState.Processing, Percent = 94,
                    Doing = label == null ? "Waiting for transcode slot..." : $"Waiting for transcode slot ({label})..." });
                await _queueService.WaitForTranscodeSlotAsync(cancellationToken);
                held = true;
            }
            await EncodeOutputAsync(inputPath, presetName, cancellationToken, progress, label);
        }
        finally
        {
            if (held) _queueService!.ReleaseTranscodeSlot();
        }
    }

    private async Task EncodeOutputAsync(string inputPath, string presetName, CancellationToken cancellationToken, IProgress<DownloadProgress>? progress = null, string? label = null)
    {
        var preset = _encodingService?.GetPreset(presetName);
        if (preset == null)
        {
            _logger?.LogWarning("Encoding preset {PresetName} not found", presetName);
            return;
        }

        var ffmpegPath = FindExecutable("ffmpeg");
        if (ffmpegPath == null)
        {
            _logger?.LogError("ffmpeg not found for encoding");
            return;
        }

        var tempOutput = inputPath + ".encoding.mkv";
        var args = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-y",
            "-i", inputPath,
        };

        if (!string.IsNullOrWhiteSpace(preset.Codec))
        {
            args.Add("-c:v");
            args.Add(preset.Codec!);
            // Quality flag depends on the codec (CRF for software, -cq/-global_quality/-rc
            // for the various hardware encoders); mirrors upstream Helpers.GetQualityOption.
            args.AddRange(GetEncodeQualityOption(preset));
            // Only build a -vf filter from the parts the preset actually sets. A preset with
            // empty Resolution AND FrameRate keeps the SOURCE resolution/fps (no filter) —
            // previously this emitted "-vf scale=,fps=" which ffmpeg rejects.
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(preset.Resolution)) filters.Add($"scale={preset.Resolution}");
            if (!string.IsNullOrWhiteSpace(preset.FrameRate)) filters.Add($"fps={preset.FrameRate}");
            if (filters.Count > 0)
            {
                args.Add("-vf");
                args.Add(string.Join(",", filters));
            }
        }

        // AdditionalParameters (e.g. "-map 0", which maps EVERY stream so all audio/sub
        // tracks survive the re-encode) are stored as single strings that may hold several
        // whitespace-separated tokens. ffmpeg needs each token as its own argv element, so
        // split first — passing "-map 0" as one element makes ffmpeg read the option name as
        // "map 0" and bail with "Unrecognized option" (mirrors upstream SplitArguments).
        foreach (var param in preset.AdditionalParameters)
            args.AddRange(SplitArguments(param));

        // Machine-readable progress on stdout (key=value blocks) so the queue can show
        // encode percentage + ETA instead of a frozen "Encoding...".
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");

        args.Add(tempOutput);

        var durationSeconds = await ProbeVideoDurationAsync(inputPath, cancellationToken);
        await RunFfmpegWithEncodeProgressAsync(ffmpegPath, args, durationSeconds, progress, label, cancellationToken);

        if (File.Exists(tempOutput))
        {
            File.Delete(inputPath);
            File.Move(tempOutput, inputPath);
            _logger?.LogInformation("Encoded output to {Path} with preset {Preset}", inputPath, presetName);
        }
        else
        {
            _logger?.LogWarning("Encoding produced no output for {Path} with preset {Preset}; keeping muxed file", inputPath, presetName);
        }
    }

    /// <summary>Total duration in seconds of the container's longest stream (ffprobe), or null.</summary>
    private async Task<double?> ProbeVideoDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(videoPath)) return null;
            var ffprobePath = FindExecutable("ffprobe") ?? "ffprobe";
            var args = new List<string>{
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                videoPath
            };
            var output = await RunProcessWithOutputAsync(ffprobePath, args, cancellationToken);
            if (double.TryParse(output, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to probe duration of {Path}", videoPath);
        }
        return null;
    }

    /// <summary>
    /// Runs ffmpeg reading its "-progress pipe:1" key=value output, reporting encode
    /// percentage + ETA through the queue progress (Percent stays in the 95-99 processing
    /// band; Doing carries the human-readable "Encoding... N%"). Kills ffmpeg on cancel.
    /// </summary>
    private async Task RunFfmpegWithEncodeProgressAsync(string ffmpegPath, List<string> args, double? durationSeconds, IProgress<DownloadProgress>? progress, string? label, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = string.Join(" ", args.Select(EscapeProcessArgument)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger?.LogDebug("Running: {Executable} {Args}", ffmpegPath, startInfo.Arguments);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            _logger?.LogError("Failed to start process: {Executable}", ffmpegPath);
            return;
        }

        await using var killRegistration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignored */ }
        });

        // Drain stderr concurrently so a full pipe buffer can't deadlock ffmpeg.
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var labelSuffix = string.IsNullOrEmpty(label) ? "" : $" {label}";
        double outTimeSeconds = 0;
        double speed = 0;
        var lastReport = DateTime.MinValue;

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "out_time_us":
                case "out_time_ms": // both are microseconds in ffmpeg's -progress output
                    if (long.TryParse(value, out var us) && us > 0) outTimeSeconds = us / 1_000_000.0;
                    break;
                case "speed":
                    var sVal = value.TrimEnd('x');
                    if (double.TryParse(sVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sp)) speed = sp;
                    break;
                case "progress": // end of a stats block — report once per block, throttled
                    if (progress == null) break;
                    var now = DateTime.UtcNow;
                    if (value != "end" && (now - lastReport).TotalSeconds < 1) break;
                    lastReport = now;
                    if (durationSeconds is > 0)
                    {
                        var pct = Math.Clamp(outTimeSeconds / durationSeconds.Value * 100.0, 0, 100);
                        var eta = speed > 0 ? Math.Max(0, (durationSeconds.Value - outTimeSeconds) / speed) : 0;
                        progress.Report(new DownloadProgress
                        {
                            State = DownloadState.Processing,
                            Percent = 95 + pct * 0.04, // keep inside the processing band
                            Doing = $"Encoding{labelSuffix}... {pct:0}%",
                            Time = eta
                        });
                    }
                    else
                    {
                        // No duration — still show elapsed encode position so it visibly moves.
                        progress.Report(new DownloadProgress
                        {
                            State = DownloadState.Processing,
                            Percent = 95,
                            Doing = $"Encoding{labelSuffix}... {TimeSpan.FromSeconds(outTimeSeconds):hh\\:mm\\:ss}"
                        });
                    }
                    break;
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger?.LogError("Process failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
        }
        else if (!string.IsNullOrEmpty(error))
        {
            _logger?.LogDebug("Process stderr: {Error}", error);
        }
    }

    // Codec-aware quality option (mirrors upstream Helpers.GetQualityOption).
    private static IEnumerable<string> GetEncodeQualityOption(VideoPreset preset)
    {
        if (preset.Crf == -1) return Array.Empty<string>();
        var q = preset.Crf.ToString();
        return preset.Codec switch
        {
            "h264_nvenc" or "hevc_nvenc" => preset.Crf is >= 0 and <= 51 ? new[] { "-cq", q } : Array.Empty<string>(),
            "h264_qsv" or "hevc_qsv" => preset.Crf is >= 1 and <= 51 ? new[] { "-global_quality", q } : Array.Empty<string>(),
            "h264_amf" => preset.Crf is >= 0 and <= 51 ? new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q } : Array.Empty<string>(),
            "hevc_amf" => preset.Crf is >= 0 and <= 51 ? new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q } : Array.Empty<string>(),
            _ => preset.Crf >= 0 ? new[] { "-crf", q } : Array.Empty<string>()
        };
    }

    // Split a single parameter string into ffmpeg argv tokens, honoring double quotes
    // (mirrors upstream Helpers.SplitArguments).
    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }

    private string? FindExecutable(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, name);
            if (File.Exists(fullPath)) return fullPath;

            // Windows
            if (File.Exists(fullPath + ".exe")) return fullPath + ".exe";
        }
        return null;
    }

    private static bool IsHlsUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains(".m3u8") || url.Contains("/hls/");
    }

    private async Task<(bool Ok, PartsData Parts)> DownloadHlsStreamAsync(string playlistUrl, string outputPath, bool isVideo, bool isAudio, CruncharrConfig config, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken)
    {
        try
        {
            // Download playlist
            var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            var (isOk, content, _) = await _httpClient.SendRequestAsync(request);
            if (!isOk || string.IsNullOrEmpty(content))
            {
                _logger?.LogError("Failed to download HLS playlist from {Url}", playlistUrl);
                return (false, new PartsData());
            }

            // Parse playlist
            var m3u8 = M3u8MediaPlaylistParser.Parse(content, playlistUrl);

            var segments = m3u8.Segments as List<dynamic>;
            if (segments == null || segments.Count == 0)
            {
                _logger?.LogWarning("No segments found in HLS playlist");
                return (false, new PartsData());
            }

            int segmentCount = segments.Count;
            _logger?.LogInformation("HLS playlist has {Count} segments", segmentCount);

            // Download with HlsDownloader
            var options = new HlsOptions
            {
                M3U8Json = m3u8,
                Output = outputPath,
                Threads = config.Download.PartSize,
                Retries = config.Download.RetryAttempts,
                BaseUrl = playlistUrl,
                Timeout = config.Download.RetryDelay * 1000,
                FsRetryTime = config.Download.RetryDelay * 1000,
                Override = config.Download.ForceOverride ? "Y" : "N"
            };

            var downloader = new HlsDownloader(options, isVideo, isAudio, config.Download.DownloadMethodeNew, _httpClient.Client, config,
                new Progress<DownloadProgress>(p =>
                {
                    if (progress != null && p.Percent > 0)
                    {
                        var overallPercent = startPercent + (p.Percent / 100.0) * (endPercent - startPercent);
                        progress.Report(new DownloadProgress
                        {
                            State = p.State,
                            Percent = overallPercent,
                            Doing = p.Doing,
                            DownloadSpeedBytes = p.DownloadSpeedBytes
                        });
                    }
                }), cancellationToken);

            return await downloader.Download();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HLS download failed for {Url}", playlistUrl);
            return (false, new PartsData());
        }
    }

    private async Task<string?> DownloadFallbackVideoAsync(EpisodeInfo episode, string locale, string tempDir, CruncharrConfig config, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Downloading fallback video for locale: {Locale}", locale);

        try
        {
            // Find version for this locale
            var version = episode.Versions?.FirstOrDefault(v =>
                string.Equals(v.AudioLocale, locale, StringComparison.OrdinalIgnoreCase));

            if (version == null)
            {
                _logger?.LogWarning("No version found for locale {Locale}", locale);
                return null;
            }

            var mediaGuid = version.Guid;
            if (mediaGuid.Contains(':')) mediaGuid = mediaGuid.Split(':')[1];

            // Get playback data
            progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 0, Doing = $"Fetching playback data for fallback ({locale})..." });
            var playbackData = await GetPlaybackDataAsync(mediaGuid, true, cancellationToken, config);
            if (playbackData?.VideoUrl == null)
            {
                _logger?.LogWarning("No video URL in playback data for fallback ({Locale})", locale);
                return null;
            }

            // Download video at best quality (no audio, no subs)
            var fallbackPath = Path.Combine(tempDir, $"video_fallback_{(locale ?? "unknown").Replace("-", "").ToLower()}.mp4");
            var videoIsHls = IsHlsUrl(playbackData.VideoUrl);

            progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 30, Doing = $"Downloading fallback video ({locale})..." });

            if (videoIsHls)
            {
                var hlsResult = await DownloadHlsStreamAsync(playbackData.VideoUrl, fallbackPath, true, false, config, progress, 30, 90, cancellationToken);
                if (!hlsResult.Ok)
                {
                    _logger?.LogWarning("HLS fallback download failed for {Locale}", locale);
                    return null;
                }
            }
            else
            {
                await DownloadStreamAsync(playbackData.VideoUrl, fallbackPath, progress, 30, 90, cancellationToken, playbackData.VideoToken);
            }

            if (File.Exists(fallbackPath))
            {
                _logger?.LogInformation("Fallback video downloaded for {Locale}: {Path}", locale, fallbackPath);
                return fallbackPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading fallback video for {Locale}", locale);
            return null;
        }
    }

    private static string ConvertVttToAss(string vttContent, string language, string? ccFont = null, string? scaledBorder = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine($"Title: {language} Subtitle");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("PlayResX: 640");
        sb.AppendLine("PlayResY: 360");
        sb.AppendLine("Timer: 0.0");
        // Only emit ScaledBorderAndShadow when configured (DontAdd => omit), matching upstream.
        var scaledLine = SubtitleUtils.NormalizeScaledBorder(scaledBorder);
        if (scaledLine != null)
            sb.AppendLine(scaledLine);
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name,Fontname,Fontsize,PrimaryColour,SecondaryColour,OutlineColour,BackColour,Bold,Italic,Underline,Strikeout,ScaleX,ScaleY,Spacing,Angle,BorderStyle,Outline,Shadow,Alignment,MarginL,MarginR,MarginV,Encoding");
        sb.AppendLine($"Style: Default,{(string.IsNullOrWhiteSpace(ccFont) ? "Trebuchet MS" : ccFont)},24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,2,0010,0010,0018,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var lines = vttContent.Split('\n');
        var timePattern = new Regex(@"^(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})");

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line == "WEBVTT" || line.StartsWith("NOTE")) continue;

            var match = timePattern.Match(line);
            if (match.Success)
            {
                var start = match.Groups[1].Value.Replace(".", ",");
                var end = match.Groups[2].Value.Replace(".", ",");

                var textLines = new List<string>();
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !timePattern.IsMatch(lines[i]))
                {
                    textLines.Add(lines[i].Trim());
                    i++;
                }
                i--;

                if (textLines.Count > 0)
                {
                    var text = string.Join("\\N", textLines)
                        .Replace("<b>", "{\\b1}").Replace("</b>", "{\\b0}")
                        .Replace("<i>", "{\\i1}").Replace("</i>", "{\\i0}")
                        .Replace("<u>", "{\\u1}").Replace("</u>", "{\\u0}");
                    sb.AppendLine($"Dialogue: 0,{start},{end},Default,,0,0,0,,{text}");
                }
            }
        }

        return sb.ToString();
    }
}

public class PlaybackData
{
    public string? VideoUrl { get; set; }
    public string? AudioUrl { get; set; }
    public string? Pssh { get; set; }
    public string? VideoToken { get; set; }
    public List<SubtitleInfo>? Subtitles { get; set; }
    public Dictionary<string, HardSub>? HardSubs { get; set; }
    public bool IsHardsubbed { get; set; }
    public string? HardsubLang { get; set; }
}

public class SubtitleInfo
{
    public string Lang { get; set; } = "";
    public string Url { get; set; } = "";
    public string Format { get; set; } = "vtt";
    // Closed Caption track (from playStream.Captions, not Subtitles).
    public bool IsCC { get; set; } = false;
    // Audio locale of the playback VERSION this subtitle came from. Upstream classifies a
    // subtitle as "signs" per version (sub locale == that version's audio locale), NOT
    // against the set of downloaded dubs: the full en-US dialogue track lives on the ja-JP
    // original version, while the en-US dub version only carries an en-US signs/songs track.
    // Without the origin, a merged sub pool misclassifies the full dialogue track as signs
    // and IncludeSignsSubs=false drops every subtitle (seen live).
    public string? SourceAudioLocale { get; set; }
}
