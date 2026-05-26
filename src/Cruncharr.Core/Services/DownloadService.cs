using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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

public interface IDownloadService{
    Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

public class DownloadService : IDownloadService{
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
    
    public DownloadService(ICrunchyrollAuthService auth, ICrunchyrollApiService api, ILogger<DownloadService>? logger = null, IHistoryService? history = null, IVideoSyncer? videoSyncer = null, IEncodingService? encodingService = null, IQueueService? queueService = null){
        _auth = auth;
        _api = api;
        _logger = logger;
        _history = history;
        _videoSyncer = videoSyncer;
        _encodingService = encodingService;
        _queueService = queueService;
        _httpClient = auth.HttpClient;
        // Use /widevine for Docker, fallback to default path
        var widevineDir = "/widevine";
        if (!Directory.Exists(widevineDir)){
            widevineDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cruncharr", "widevine");
        }
        _widevine = new WidevineCdm(widevineDir);
        _chapterService = new ChapterService(_httpClient, null);
        _fontService = new FontService(null);
        _filenameService = new FilenameService();
    }
    
    public async Task<DownloadResult> DownloadEpisodeAsync(EpisodeInfo episode, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Starting download: {EpisodeId} - {Title}", episode.Id, episode.Title);
        progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 0, Doing = "Authenticating..." });
        
        // Authenticate (use beta API)
        try{
            if (!await _auth.AuthenticateAsync(true, cancellationToken)){
                return new DownloadResult{ Success = false, ErrorMessage = "Authentication failed. Please log in to your Crunchyroll account.", ErrorType = DownloadErrorType.NotAuthenticated };
            }
        } catch (Exception ex){
            return new DownloadResult{ Success = false, ErrorMessage = $"Authentication error: {ex.Message}", ErrorType = DownloadErrorType.NotAuthenticated };
        }
        
        // Fetch full episode details with versions if not already loaded
        if (episode.Versions == null || episode.Versions.Count == 0){
            progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 10, Doing = "Fetching episode info..." });
            var fullEpisode = await _api.GetEpisodeAsync(episode.Id, true, cancellationToken);
            if (fullEpisode != null){
                episode.Versions = fullEpisode.Versions;
                episode.AudioLocale = fullEpisode.AudioLocale ?? episode.AudioLocale;
                episode.Guid = fullEpisode.Guid ?? episode.Guid;
                _logger?.LogInformation("Fetched episode details: {EpisodeId}, Versions={VersionCount}, AudioLocale={AudioLocale}, Guid={Guid}", 
                    fullEpisode.Id, fullEpisode.Versions?.Count ?? 0, fullEpisode.AudioLocale, fullEpisode.Guid);
            } else{
                _logger?.LogWarning("Failed to fetch full episode details for {EpisodeId}", episode.Id);
            }
        }
        
        // Select correct episode version based on DubLanguages (ported from upstream CrunchyrollManager.DownloadMediaList)
        // Upstream sorts data.Data by DubLang priority, then processes each version
        // Default to episode.Id (the actual version ID for this language)
        string mediaGuid = episode.Id;
        string mediaId = episode.Id;
        
        _logger?.LogInformation("Episode {EpisodeId} has {VersionCount} versions", episode.Id, episode.Versions?.Count ?? 0);
        if (episode.Versions != null){
            foreach (var v in episode.Versions){
                _logger?.LogDebug("Version: Guid={Guid}, MediaGuid={MediaGuid}, AudioLocale={AudioLocale}, Original={Original}", v.Guid, v.MediaGuid, v.AudioLocale, v.Original);
            }
        }
        
        if (episode.Versions != null && episode.Versions.Count > 0){
            EpisodeVersion? currentVersion = null;
            EpisodeVersion? primaryVersion = null;
            
            // Ported from upstream: find version matching episode's language
            if (!string.IsNullOrEmpty(episode.AudioLocale)){
                currentVersion = episode.Versions.FirstOrDefault(v => 
                    v.AudioLocale.Equals(episode.AudioLocale, StringComparison.OrdinalIgnoreCase));
            }
            
            // If episode's locale not found or not in DubLanguages, try DubLanguages priority
            var dubLangs = config.Download.DubLanguages;
            if (currentVersion == null || 
                (dubLangs.Count > 0 && !dubLangs.Any(d => d.Equals(episode.AudioLocale, StringComparison.OrdinalIgnoreCase)))){
                
                // Try each DubLanguage in order
                foreach (var dubLang in dubLangs){
                    var matchingVersion = episode.Versions.FirstOrDefault(v => 
                        v.AudioLocale.Equals(dubLang, StringComparison.OrdinalIgnoreCase));
                    if (matchingVersion != null){
                        currentVersion = matchingVersion;
                        _logger?.LogInformation("DubLanguages override: selected {DubLang} version instead of {OriginalLocale}", 
                            dubLang, episode.AudioLocale);
                        break;
                    }
                }
            }
            
            // Fallback: try config's default audio
            if (currentVersion == null && !string.IsNullOrEmpty(config.Download.DefaultAudio)){
                currentVersion = episode.Versions.FirstOrDefault(v => 
                    v.AudioLocale.Equals(config.Download.DefaultAudio, StringComparison.OrdinalIgnoreCase));
            }
            
            // Fallback: if only one version, use it
            if (currentVersion == null && episode.Versions.Count == 1){
                currentVersion = episode.Versions[0];
            }
            
            // Fallback: use original version
            if (currentVersion == null){
                currentVersion = episode.Versions.FirstOrDefault(v => v.Original) ?? episode.Versions[0];
            }
            
            if (currentVersion != null){
                mediaGuid = currentVersion.Guid;
                if (!string.IsNullOrEmpty(currentVersion.MediaGuid)){
                    mediaId = currentVersion.MediaGuid;
                }
                
                // Track if this is the primary (original) version
                bool isPrimary = currentVersion.Original;
                if (!isPrimary){
                    primaryVersion = episode.Versions.FirstOrDefault(v => v.Original) ?? currentVersion;
                } else{
                    primaryVersion = currentVersion;
                }
                
                _logger?.LogInformation("Selected version: Guid={Guid}, MediaGuid={MediaGuid}, audio_locale={AudioLocale}, original={Original}, isPrimary={IsPrimary}", 
                    currentVersion.Guid, currentVersion.MediaGuid, currentVersion.AudioLocale, currentVersion.Original, isPrimary);
            } else{
                _logger?.LogWarning("Could not find matching version for audio locale {AudioLocale}, using default episode.Id", episode.AudioLocale);
            }
        }
        
        // Strip any prefix from mediaId/mediaGuid
        if (mediaId.Contains(':')){
            mediaId = mediaId.Split(':')[1];
        }
        if (mediaGuid.Contains(':')){
            mediaGuid = mediaGuid.Split(':')[1];
        }
        
        _logger?.LogInformation("Using mediaGuid={MediaGuid}, mediaId={MediaId} for playback API", mediaGuid, mediaId);
        
        // Get playback data (use beta API)
        progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 20, Doing = "Fetching playback data..." });
        var playbackData = await GetPlaybackDataAsync(mediaGuid, true, cancellationToken);
        if (playbackData == null){
            return new DownloadResult{ Success = false, ErrorMessage = "Failed to fetch playback data" };
        }
        
        // Fetch DRM keys if needed
        List<ContentKey>? decryptionKeys = null;
        
        // For DASH, PSSH might be in the manifest instead of the JSON response
        string? pssh = playbackData.Pssh;
        if (string.IsNullOrEmpty(pssh) && playbackData.VideoUrl?.Contains(".mpd") == true){
            _logger?.LogInformation("No PSSH in playback data, trying to extract from DASH manifest...");
            var manifestRequest = new HttpRequestMessage(HttpMethod.Get, playbackData.VideoUrl);
            manifestRequest.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");
            var (manifestOk, manifestContent, _) = await _httpClient.SendRequestAsync(manifestRequest);
            if (manifestOk && !string.IsNullOrEmpty(manifestContent)){
                var manifest = DashDownloader.ParseManifest(manifestContent, playbackData.VideoUrl);
                pssh = manifest.VideoTracks.FirstOrDefault()?.Pssh ?? manifest.AudioTracks.FirstOrDefault()?.Pssh;
                _logger?.LogInformation("PSSH from manifest: {Pssh}", pssh ?? "(null)");
            }
        }
        
        // Select stream based on HardSubLang setting (ported from upstream DownloadMediaList)
        var streamSelection = SelectStreamWithHardsub(playbackData, config);
        if (!streamSelection.Success){
            return new DownloadResult{ Success = false, ErrorMessage = streamSelection.ErrorMessage };
        }
        
        _logger?.LogInformation("PSSH: {Pssh}", pssh ?? "(null)");
        if (!string.IsNullOrEmpty(pssh) && _widevine.CanDecrypt){
            progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 25, Doing = "Fetching decryption keys..." });
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
            if (decryptionKeys.Count == 0){
                _logger?.LogWarning("Failed to get decryption keys, stream may be undecryptable");
            }
        } else{
            _logger?.LogWarning("Skipping decryption - PSSH: {HasPssh}, CanDecrypt: {CanDecrypt}", !string.IsNullOrEmpty(playbackData.Pssh), _widevine.CanDecrypt);
        }
        
        // Prepare output path
        var outputDir = config.Download.OutputDirectory;
        Directory.CreateDirectory(outputDir);
        
        var filenameOptions = new FilenameOptions{
            NumberPadding = config.Download.LeadingNumbers,
            Quality = config.Download.QualityVideo,
            AudioLanguage = config.Download.DefaultAudio
        };
        var fileName = _filenameService.FormatFilename(config.Download.Filename, episode, filenameOptions);
        string outputExtension;
        if (config.Download.MuxAudioOnlyToMp3 && config.Download.NoVideo){
            outputExtension = ".mp3";
        } else if (config.Download.MuxMp4){
            outputExtension = ".mp4";
        } else{
            outputExtension = ".mkv";
        }
        var outputPath = Path.Combine(outputDir, fileName + outputExtension);
        
        // Replace existing file if configured
        if (config.Download.ReplaceExistingFiles && File.Exists(outputPath)){
            _logger?.LogInformation("Replacing existing file: {OutputPath}", outputPath);
            File.Delete(outputPath);
        }
        
        // Download streams
        var tempDir = config.Download.UseTempFolder 
            ? Path.Combine(config.Download.TempDirectory, Guid.NewGuid().ToString())
            : outputDir;
        Directory.CreateDirectory(tempDir);
        
        try{
            var downloadedFiles = new List<string>();
            var audioTrackLanguages = new List<(string Path, string Lang)>();
            var syncVideos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            // Handle DASH manifest (contains both video and audio)
            if (playbackData.VideoUrl != null && (playbackData.VideoUrl.Contains(".mpd") || playbackData.VideoUrl.Contains("/dash/"))){
                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 30, Doing = "Downloading DASH streams..." });
                var (videoPath, audioPaths) = await DownloadDashTracksAsync(playbackData.VideoUrl, tempDir, config, progress, 30, 80, cancellationToken, playbackData.VideoToken, mediaId);
                if (videoPath != null && !config.Download.NoVideo) downloadedFiles.Add(videoPath);
                foreach (var (path, _) in audioPaths){
                    downloadedFiles.Add(path);
                }
                audioTrackLanguages = audioPaths;
            } else{
                // Check if URLs are HLS playlists
                bool videoIsHls = IsHlsUrl(playbackData.VideoUrl);
                bool audioIsHls = IsHlsUrl(playbackData.AudioUrl);
                
                if (videoIsHls || audioIsHls){
                    _logger?.LogInformation("Using HLS downloader for segmented streams");
                }
                
                // Download video (skip if NoVideo is enabled)
                if (playbackData.VideoUrl != null && !config.Download.NoVideo){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 30, Doing = "Downloading video..." });
                    var videoPath = Path.Combine(tempDir, "video.mp4");
                    
                    if (videoIsHls){
                        var hlsResult = await DownloadHlsStreamAsync(playbackData.VideoUrl, videoPath, true, false, config, progress, 30, 60, cancellationToken);
                        if (hlsResult.Ok) downloadedFiles.Add(videoPath);
                    } else{
                        await DownloadStreamAsync(playbackData.VideoUrl, videoPath, progress, 30, 60, cancellationToken, playbackData.VideoToken);
                        downloadedFiles.Add(videoPath);
                    }
                } else if (config.Download.NoVideo){
                    _logger?.LogInformation("NoVideo enabled, skipping video download");
                }
                
                // Download primary audio (skip if NoAudio is enabled)
                if (playbackData.AudioUrl != null && !config.Download.NoAudio){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 60, Doing = $"Downloading audio ({episode.AudioLocale ?? config.Download.DefaultAudio})..." });
                    var audioPath = Path.Combine(tempDir, $"audio_{(episode.AudioLocale ?? config.Download.DefaultAudio).Replace("-", "").ToLower()}.m4a");
                    
                    if (audioIsHls){
                        var hlsResult = await DownloadHlsStreamAsync(playbackData.AudioUrl, audioPath, false, true, config, progress, 60, 80, cancellationToken);
                        if (hlsResult.Ok){
                            downloadedFiles.Add(audioPath);
                            audioTrackLanguages.Add((audioPath, episode.AudioLocale ?? config.Download.DefaultAudio));
                        }
                    } else{
                        await DownloadStreamAsync(playbackData.AudioUrl, audioPath, progress, 60, 80, cancellationToken, playbackData.VideoToken);
                        downloadedFiles.Add(audioPath);
                        audioTrackLanguages.Add((audioPath, episode.AudioLocale ?? config.Download.DefaultAudio));
                    }
                } else if (config.Download.NoAudio){
                    _logger?.LogInformation("NoAudio enabled, skipping audio download");
                }
                
                // Download additional dubs if configured (skip if NoAudio is enabled)
                // Note: Video is only downloaded once (DlVideoOnce optimization). Additional dubs reuse the same video stream.
                if (!config.Download.NoAudio && config.Download.DownloadMultipleDubs && episode.Versions != null && episode.Versions.Count > 1){
                    var primaryLocale = episode.AudioLocale ?? config.Download.DefaultAudio;
                    var selectedDubs = config.Download.DubLanguages
                        .Where(dub => !string.Equals(dub, primaryLocale, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    _logger?.LogInformation("DlVideoOnce: Reusing video from primary dub for {Count} additional dubs", selectedDubs.Count);
                    
                    foreach (var dub in selectedDubs){
                        var dubVersion = episode.Versions.FirstOrDefault(v => 
                            v.AudioLocale.Equals(dub, StringComparison.OrdinalIgnoreCase));
                        
                        if (dubVersion == null) continue;
                        
                        var dubMediaGuid = dubVersion.Guid;
                        var dubMediaId = dubVersion.MediaGuid ?? dubVersion.Guid;
                        
                        if (dubMediaId.Contains(':')) dubMediaId = dubMediaId.Split(':')[1];
                        if (dubMediaGuid.Contains(':')) dubMediaGuid = dubMediaGuid.Split(':')[1];
                        
                        _logger?.LogInformation("Fetching playback data for additional dub: {Dub} (Guid={Guid})", dub, dubMediaGuid);
                        
                        try{
                            var dubPlayback = await GetPlaybackDataAsync(dubMediaGuid, true, cancellationToken);
                            
                            // Download sync video for timing comparison if SyncTiming is enabled
                            if (config.Download.SyncTiming && config.Download.DlVideoOnce && dubPlayback?.VideoUrl != null){
                                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 62, Doing = $"Downloading sync video ({dub})..." });
                                var syncVideoPath = Path.Combine(tempDir, $"syncvideo_{dub.Replace("-", "").ToLower()}.mp4");
                                var dubVideoIsHls = IsHlsUrl(dubPlayback.VideoUrl);
                                
                                if (dubVideoIsHls){
                                    var hlsResult = await DownloadHlsStreamAsync(dubPlayback.VideoUrl, syncVideoPath, true, false, config, progress, 60, 65, cancellationToken);
                                    if (hlsResult.Ok){
                                        syncVideos[dub] = syncVideoPath;
                                        _logger?.LogInformation("Downloaded sync video for dub: {Dub} -> {Path}", dub, syncVideoPath);
                                    }
                                } else{
                                    await DownloadStreamAsync(dubPlayback.VideoUrl, syncVideoPath, progress, 60, 65, cancellationToken, dubPlayback.VideoToken);
                                    syncVideos[dub] = syncVideoPath;
                                    _logger?.LogInformation("Downloaded sync video for dub: {Dub} -> {Path}", dub, syncVideoPath);
                                }
                            }
                            
                            if (dubPlayback?.AudioUrl != null){
                                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 65, Doing = $"Downloading audio ({dub})..." });
                                
                                var dubAudioPath = Path.Combine(tempDir, $"audio_{dub.Replace("-", "").ToLower()}.m4a");
                                var dubAudioIsHls = IsHlsUrl(dubPlayback.AudioUrl);
                                
                                if (dubAudioIsHls){
                                    var hlsResult = await DownloadHlsStreamAsync(dubPlayback.AudioUrl, dubAudioPath, false, true, config, progress, 65, 80, cancellationToken);
                                    if (hlsResult.Ok){
                                        downloadedFiles.Add(dubAudioPath);
                                        audioTrackLanguages.Add((dubAudioPath, dub));
                                        _logger?.LogInformation("Downloaded additional audio track: {Dub} -> {Path}", dub, dubAudioPath);
                                    }
                                } else{
                                    await DownloadStreamAsync(dubPlayback.AudioUrl, dubAudioPath, progress, 65, 80, cancellationToken, dubPlayback.VideoToken);
                                    downloadedFiles.Add(dubAudioPath);
                                    audioTrackLanguages.Add((dubAudioPath, dub));
                                    _logger?.LogInformation("Downloaded additional audio track: {Dub} -> {Path}", dub, dubAudioPath);
                                }
                            }
                        } catch (Exception ex){
                            _logger?.LogWarning(ex, "Failed to download additional dub: {Dub}", dub);
                        }
                    }
                }
                
                // Download Audio Description (AD) track if configured (skip if NoAudio is enabled)
                if (!config.Download.NoAudio && config.Download.DownloadDescriptionAudio && episode.Versions != null){
                    var adVersion = episode.Versions.FirstOrDefault(v => 
                        v.Roles?.Any(r => string.Equals(r, "description", StringComparison.OrdinalIgnoreCase)) == true);
                    
                    if (adVersion != null){
                        var adLocale = adVersion.AudioLocale;
                        // Skip if we already downloaded this locale (AD tracks share locale with main track)
                        var alreadyDownloaded = audioTrackLanguages.Any(a => 
                            a.Lang.Equals(adLocale, StringComparison.OrdinalIgnoreCase));
                        
                        if (!alreadyDownloaded){
                            var adMediaGuid = adVersion.Guid;
                            var adMediaId = adVersion.MediaGuid ?? adVersion.Guid;
                            
                            if (adMediaId.Contains(':')) adMediaId = adMediaId.Split(':')[1];
                            if (adMediaGuid.Contains(':')) adMediaGuid = adMediaGuid.Split(':')[1];
                            
                            _logger?.LogInformation("Fetching playback data for Audio Description: {Locale} (Guid={Guid})", adLocale, adMediaGuid);
                            
                            try{
                                var adPlayback = await GetPlaybackDataAsync(adMediaGuid, true, cancellationToken);
                                if (adPlayback?.AudioUrl != null){
                                    progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 67, Doing = $"Downloading audio description ({adLocale})..." });
                                    
                                    var adAudioPath = Path.Combine(tempDir, $"audio_{adLocale.Replace("-", "").ToLower()}_ad.m4a");
                                    var adAudioIsHls = IsHlsUrl(adPlayback.AudioUrl);
                                    
                                    if (adAudioIsHls){
                                        var hlsResult = await DownloadHlsStreamAsync(adPlayback.AudioUrl, adAudioPath, false, true, config, progress, 60, 80, cancellationToken);
                                        if (hlsResult.Ok){
                                            downloadedFiles.Add(adAudioPath);
                                            audioTrackLanguages.Add((adAudioPath, adLocale));
                                            _logger?.LogInformation("Downloaded audio description track: {Locale} -> {Path}", adLocale, adAudioPath);
                                        }
                                    } else{
                                        await DownloadStreamAsync(adPlayback.AudioUrl, adAudioPath, progress, 60, 80, cancellationToken, adPlayback.VideoToken);
                                        downloadedFiles.Add(adAudioPath);
                                        audioTrackLanguages.Add((adAudioPath, adLocale));
                                        _logger?.LogInformation("Downloaded audio description track: {Locale} -> {Path}", adLocale, adAudioPath);
                                    }
                                }
                            } catch (Exception ex){
                                _logger?.LogWarning(ex, "Failed to download audio description for {Locale}", adLocale);
                            }
                        }
                    }
                }
            }
            
            // Download subtitles
            var subtitleFiles = new List<(string Path, string Lang)>();
            if (playbackData.Subtitles != null && playbackData.Subtitles.Count > 0){
                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 80, Doing = "Downloading subtitles..." });
                foreach (var sub in playbackData.Subtitles){
                    var langCode = sub.Lang.Replace("-", "").ToLower();
                    var subLangs = config.Download.SoftSubs?.Count > 0 ? config.Download.SoftSubs : config.Download.SubtitleLanguages;
                    var shouldDownload = subLangs.Contains("all") || 
                                         subLangs.Contains(sub.Lang) ||
                                         subLangs.Contains(langCode);
                    
                    if (shouldDownload && !string.IsNullOrEmpty(sub.Url)){
                        var ext = sub.Format?.ToLower() == "ass" ? "ass" : "vtt";
                        var subPath = Path.Combine(tempDir, $"sub_{sub.Lang}.{ext}");
                        
                        try{
                            var subRequest = new HttpRequestMessage(HttpMethod.Get, sub.Url);
                            var (subOk, subContent, _) = await _httpClient.SendRequestAsync(subRequest);
                            if (subOk && !string.IsNullOrEmpty(subContent)){
                                if (sub.Format?.ToLower() == "vtt" && config.Download.ConvertVttToAss){
                                    // Convert VTT to ASS
                                    subPath = Path.ChangeExtension(subPath, ".ass");
                                    var assContent = ConvertVttToAss(subContent, sub.Lang);
                                    await File.WriteAllTextAsync(subPath, assContent, cancellationToken);
                                } else{
                                    await File.WriteAllTextAsync(subPath, subContent, cancellationToken);
                                }
                                subtitleFiles.Add((subPath, sub.Lang));
                            }
                        } catch (Exception ex){
                            _logger?.LogWarning(ex, "Failed to download subtitle {Lang}", sub.Lang);
                        }
                    }
                }
            }
            
            // Download cover art if available and enabled
            string? coverPath = null;
            if (!string.IsNullOrEmpty(episode.CoverArtUrl) && config.Download.MuxCover && !config.Download.SkipMuxing){
                try{
                    progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 83, Doing = "Downloading cover art..." });
                    using var coverResponse = await _httpClient.Client.GetAsync(episode.CoverArtUrl, cancellationToken);
                    if (coverResponse.IsSuccessStatusCode){
                        var coverBytes = await coverResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                        if (coverBytes != null && coverBytes.Length > 0){
                            coverPath = Path.Combine(tempDir, "cover.png");
                            await File.WriteAllBytesAsync(coverPath, coverBytes, cancellationToken);
                            _logger?.LogDebug("Downloaded cover art to {Path}", coverPath);
                        }
                    }
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Failed to download cover art for {EpisodeId}", episode.Id);
                }
            }
            
            // Extract fonts from subtitles if muxing is enabled
            var fontAttachments = new List<FontAttachment>();
            if (config.Download.MuxFonts && subtitleFiles.Count > 0){
                try{
                    progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 81, Doing = "Extracting fonts..." });
                    var allFontNames = new List<string>();
                    foreach (var (subPath, _) in subtitleFiles.Where(s => s.Path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))){
                        var assContent = await File.ReadAllTextAsync(subPath, cancellationToken);
                        var fonts = _fontService.ExtractFontsFromAss(assContent, true);
                        allFontNames.AddRange(fonts);
                    }
                    if (allFontNames.Count > 0){
                        var fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
                        fontAttachments = _fontService.ResolveFonts(allFontNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), fontsDir);
                        _logger?.LogInformation("Resolved {Count} fonts for muxing", fontAttachments.Count);
                    }
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Failed to extract fonts from subtitles");
                }
            }
            
            // Fetch chapters if enabled
            string? chapterFile = null;
            if (config.Download.IncludeChapters && !string.IsNullOrEmpty(episode.Id)){
                try{
                    progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 82, Doing = "Fetching chapters..." });
                    var chapters = await _chapterService.GetChaptersAsync(episode.Id, _auth.Token?.access_token, cancellationToken);
                    if (chapters.Count > 0){
                        var chapterPath = Path.Combine(tempDir, "chapters.txt");
                        chapterFile = await _chapterService.WriteChapterFileAsync(chapters, chapterPath, cancellationToken);
                    }
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Failed to fetch chapters for {EpisodeId}", episode.Id);
                }
            }
            
            // Decrypt if keys available
            if (decryptionKeys != null && decryptionKeys.Count > 0){
                progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 85, Doing = "Decrypting..." });
                downloadedFiles = await DecryptFilesAsync(downloadedFiles, decryptionKeys, cancellationToken);
                
                // Update audio track paths after decryption (paths change from .enc.m4s to .m4s)
                audioTrackLanguages = audioTrackLanguages.Select(a => {
                    var decryptedPath = Path.Combine(Path.GetDirectoryName(a.Path)!, 
                        Path.GetFileNameWithoutExtension(a.Path).Replace(".enc", "") + Path.GetExtension(a.Path));
                    return File.Exists(decryptedPath) ? (decryptedPath, a.Lang) : a;
                }).ToList();
            }
            
            // Sync Timing: Calculate delays for dubs if enabled
            var audioDelays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (config.Download.SyncTiming && config.Download.DlVideoOnce && syncVideos.Count > 0 && _videoSyncer != null){
                progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 86, Doing = "Syncing dub timings..." });
                
                // Find base video path (first video file that's not a sync video)
                var baseVideoPath = downloadedFiles.FirstOrDefault(f => 
                    !syncVideos.Values.Any(sv => sv.Equals(f, StringComparison.OrdinalIgnoreCase)) &&
                    (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)));
                
                if (!string.IsNullOrEmpty(baseVideoPath)){
                    var ffmpegPath = FindExecutable("ffmpeg") ?? "ffmpeg";
                    var syncErrors = new List<string>();
                    
                    foreach (var (dubLocale, syncVideoPath) in syncVideos){
                        try{
                            _logger?.LogInformation("Syncing dub timing for {Dub}: base={Base}, sync={Sync}", dubLocale, baseVideoPath, syncVideoPath);
                            var delay = await _videoSyncer.ProcessVideo(baseVideoPath, syncVideoPath, tempDir, ffmpegPath);
                            
                            if (delay.offSet <= -100){
                                _logger?.LogWarning("Sync failed for dub {Dub}: offset={Offset}", dubLocale, delay.offSet);
                                syncErrors.Add(dubLocale);
                                continue;
                            }
                            
                            var delayMs = (int)(delay.offSet * 1000);
                            audioDelays[dubLocale] = delayMs;
                            _logger?.LogInformation("Sync delay for dub {Dub}: {Delay}ms", dubLocale, delayMs);
                            
                            if (delay.lengthDiff > 0.1){
                                _logger?.LogWarning("Dub length difference for {Dub}: {LengthDiff}s", dubLocale, delay.lengthDiff);
                            }
                        } catch (Exception ex){
                            _logger?.LogError(ex, "Error syncing dub {Dub}", dubLocale);
                            syncErrors.Add(dubLocale);
                        }
                    }
                    
                    // Clean up sync videos after processing
                    foreach (var syncVideoPath in syncVideos.Values){
                        try{
                            if (File.Exists(syncVideoPath)) File.Delete(syncVideoPath);
                            var resumeFile = syncVideoPath + ".resume";
                            if (File.Exists(resumeFile)) File.Delete(resumeFile);
                        } catch (Exception ex){
                            _logger?.LogWarning(ex, "Failed to delete sync video: {Path}", syncVideoPath);
                        }
                    }
                    
                    // TODO: SyncTimingFullQualityFallback - re-download full quality video for failed dubs
                    if (syncErrors.Count > 0 && config.Download.SyncTimingFullQualityFallback){
                        _logger?.LogWarning("Sync timing fallback not yet implemented for dubs: {Dubs}", string.Join(", ", syncErrors));
                    }
                } else{
                    _logger?.LogWarning("Could not find base video path for sync timing");
                }
            }
            
            // Wait for processing slot (muxing/encoding limit)
            bool processingSlotHeld = false;
            try{
                if (_queueService != null){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 88, Doing = "Waiting for processing slot..." });
                    await _queueService.WaitForProcessingSlotAsync(cancellationToken);
                    processingSlotHeld = true;
                }
                
                // Mux
                progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 90, Doing = "Muxing..." });
                if (!config.Download.SkipMuxing){
                    await MuxFilesAsync(downloadedFiles, audioTrackLanguages, subtitleFiles, chapterFile, fontAttachments, coverPath, outputPath, config, cancellationToken, audioDelays);
                }
                
                // Post-process encoding if configured
                if (!string.IsNullOrEmpty(config.Download.EncodingPreset) && _encodingService != null){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 95, Doing = "Encoding..." });
                    await EncodeOutputAsync(outputPath, config.Download.EncodingPreset, cancellationToken);
                }
            } finally{
                if (processingSlotHeld && _queueService != null){
                    _queueService.ReleaseProcessingSlot();
                }
            }
            
            progress?.Report(new DownloadProgress{ State = DownloadState.Done, Percent = 100, Doing = "Complete" });
            
            // Record in history
            if (_history != null && config.Download.HistoryEnabled){
                try{
                    var fileInfo = new FileInfo(outputPath);
                    var downloadedDubs = audioTrackLanguages.Select(a => a.Lang).Distinct().ToList();
                    var downloadedSubs = subtitleFiles.Select(s => s.Lang).Distinct().ToList();
                    
                    await _history.AddAsync(new DownloadHistory{
                        EpisodeId = episode.Id,
                        SeriesId = episode.Guid,
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
                    
                    // Also update rich history with downloaded dubs/subs for partial download tracking
                    await _history.SetAsDownloadedAsync(episode.Guid, episode.SeasonId, episode.Id, downloadedDubs, downloadedSubs);
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Failed to record download history");
                }
            }
            
            return new DownloadResult{
                Success = true,
                OutputPath = outputPath,
                Episode = episode
            };
        } finally{
            // Cleanup temp files
            if (config.Download.UseTempFolder){
                try{
                    if (Directory.Exists(tempDir)){
                        Directory.Delete(tempDir, true);
                    }
                } catch{
                    // Ignore cleanup errors
                }
            } else{
                // Clean up individual temp files in output dir
                try{
                    foreach (var file in Directory.GetFiles(tempDir, "*.m4s")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.mp4")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.m4a")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.ass")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.vtt")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.resume")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "*.new.resume")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "cover.*")) File.Delete(file);
                    foreach (var file in Directory.GetFiles(tempDir, "chapters.*")) File.Delete(file);
                } catch{
                    // Ignore cleanup errors
                }
            }
        }
    }
    
    public async Task<DownloadResult> DownloadSeriesAsync(string seriesId, CruncharrConfig config, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default){
        _logger?.LogInformation("Starting series download: {SeriesId}", seriesId);
        
        var episodes = await _api.GetEpisodesAsync(seriesId, false, cancellationToken);
        if (episodes.Count == 0){
            return new DownloadResult{ Success = false, ErrorMessage = "No episodes found" };
        }
        
        _logger?.LogInformation("Found {Count} episodes", episodes.Count);
        
        int successCount = 0;
        foreach (var episode in episodes){
            if (cancellationToken.IsCancellationRequested) break;
            
            var result = await DownloadEpisodeAsync(episode, config, progress, cancellationToken);
            if (result.Success) successCount++;
        }
        
        return new DownloadResult{
            Success = successCount > 0,
            ErrorMessage = successCount < episodes.Count ? $"Downloaded {successCount}/{episodes.Count} episodes" : null
        };
    }
    
    private async Task<PlaybackData?> GetPlaybackDataAsync(string episodeId, bool useBetaApi, CancellationToken cancellationToken, int retryAttempt = 0){
        if (_auth.Token?.access_token == null){
            throw new DownloadException("You are not logged in. Please log in to your Crunchyroll account.", DownloadErrorType.NotAuthenticated);
        }
        
        // Refresh token before playback API call (matches source behavior)
        await _auth.RefreshTokenAsync(useBetaApi, cancellationToken);
        
        const int maxRetries = 3;
        
        // Use stream endpoint settings from auth service (ported from source)
        var streamEndpoint = _auth.StreamEndpoint;
        var streamEndpointSecondary = _auth.StreamEndpointSecondary;
        
        var endpoints = new List<(string Endpoint, string UserAgent, CrAuthSettings Settings)>();
        
        if (streamEndpoint.Video || streamEndpoint.Audio){
            endpoints.Add(($"{ApiUrls.Playback}/{episodeId}/{streamEndpoint.Endpoint}/play", streamEndpoint.UserAgent, streamEndpoint));
        }
        
        if (!string.IsNullOrEmpty(streamEndpointSecondary.Endpoint) && (streamEndpointSecondary.Video || streamEndpointSecondary.Audio)){
            endpoints.Add(($"{ApiUrls.Playback}/{episodeId}/{streamEndpointSecondary.Endpoint}/play", streamEndpointSecondary.UserAgent, streamEndpointSecondary));
        }
        
        // Fallback endpoint
        endpoints.Add(($"{ApiUrls.Playback}/{episodeId}/web/firefox/play", ApiUrls.FirefoxUserAgent, streamEndpoint));
        
        foreach (var (endpoint, userAgent, settings) in endpoints){
            var request = HttpClientWrapper.CreateRequest(endpoint, HttpMethod.Get, true, _auth.Token.access_token);
            request.Headers.Add("User-Agent", userAgent);
            
            _logger?.LogInformation("[PLAYBACK REQUEST] Endpoint={Endpoint}, TokenPrefix={TokenPrefix}, UserAgent={UserAgent}", 
                endpoint, 
                _auth.Token.access_token?[..Math.Min(20, _auth.Token.access_token.Length)] + "...",
                userAgent);
            
            var (isOk, content, error, headers) = await _httpClient.SendRequestWithHeadersAsync(request);
            
            _logger?.LogInformation("[PLAYBACK RESPONSE] IsOk={IsOk}, ContentLength={ContentLength}, Error={Error}", 
                isOk, 
                content?.Length ?? 0,
                error);
            
            if (!string.IsNullOrEmpty(content) && !isOk){
                _logger?.LogWarning("[PLAYBACK ERROR BODY] {Content}", content);
            }
            
            if (isOk){
                return await ParsePlaybackDataAsync(content, cancellationToken);
            }
            
            // Check for stream errors
            if (!string.IsNullOrEmpty(content)){
                var streamError = StreamError.FromJson(content);
                
                if (streamError?.IsTooManyActiveStreamsError() == true){
                    _logger?.LogWarning("Too many active streams detected. De-authing existing streams...");
                    foreach (var activeStream in streamError.ActiveStreams){
                        await DeAuthVideoAsync(activeStream.ContentId, activeStream.Token);
                    }
                    // Retry after de-auth
                    if (retryAttempt < maxRetries){
                        _logger?.LogInformation("Retrying playback request after de-auth (attempt {Attempt}/{Max})", retryAttempt + 1, maxRetries);
                        await Task.Delay(2000, cancellationToken);
                        return await GetPlaybackDataAsync(episodeId, useBetaApi, cancellationToken, retryAttempt + 1);
                    }
                    throw new DownloadException("Too many active streams. Close open Crunchyroll tabs in your browser and try again.", DownloadErrorType.TooManyActiveStreams);
                }
                
                if (streamError?.IsMaturityRatingError() == true){
                    throw new DownloadException("Account maturity rating is lower than video rating. Change it in Crunchyroll account settings.", DownloadErrorType.MaturityRating);
                }
                
                if (streamError?.IsPlaybackRateLimitError() == true){
                    int retryDelaySeconds = GetRetryDelaySeconds(retryAttempt);
                    if (headers.TryGetValue("retry-after", out var retryAfter) && int.TryParse(retryAfter, out var parsedRetryAfter)){
                        retryDelaySeconds = parsedRetryAfter;
                    }
                    
                    _logger?.LogWarning("Playback API rate limited (4294). Retrying in {Delay}s...", retryDelaySeconds);
                    
                    if (retryAttempt < maxRetries){
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                        return await GetPlaybackDataAsync(episodeId, useBetaApi, cancellationToken, retryAttempt + 1);
                    }
                    throw new DownloadException("Rate limit exceeded. Please wait a few minutes and try again.", DownloadErrorType.RateLimited);
                }
                
                // Check for subscription/auth errors
                if (streamError?.Error?.Contains("subscription", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("access", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.RawJson?.Contains("40016") == true){
                    if (streamError?.Error?.Contains("does not have access", StringComparison.OrdinalIgnoreCase) == true){
                        throw new DownloadException("Premium subscription required. This content is only available to premium subscribers.", DownloadErrorType.PremiumContent);
                    }
                    if (streamError?.Error?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true ||
                        streamError?.Error?.Contains("ended", StringComparison.OrdinalIgnoreCase) == true){
                        throw new DownloadException("Your Crunchyroll subscription has expired. Please renew your subscription.", DownloadErrorType.SubscriptionExpired);
                    }
                    throw new DownloadException("Subscription error: " + streamError?.Error, DownloadErrorType.SubscriptionExpired);
                }
                
                // Check for auth errors
                if (streamError?.RawJson?.Contains("401") == true || 
                    streamError?.Error?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("invalid token", StringComparison.OrdinalIgnoreCase) == true ||
                    streamError?.Error?.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) == true){
                    throw new DownloadException("Authentication failed. Please log in again.", DownloadErrorType.NotAuthenticated);
                }
                
                if (!string.IsNullOrEmpty(streamError?.Error)){
                    _logger?.LogError("Playback API error: {Error}", streamError.Error);
                }
            }
        }
        
        throw new DownloadException("Failed to get playback data from all endpoints. The content may not be available in your region.", DownloadErrorType.NetworkError);
    }
    
    private static int GetRetryDelaySeconds(int retryAttempt){
        // Exponential backoff: 5s, 15s, 45s
        return (int)(5 * Math.Pow(3, retryAttempt));
    }
    
    private (bool Success, string? ErrorMessage) SelectStreamWithHardsub(PlaybackData playback, CruncharrConfig config){
        var hsLang = config.Download.HardSubLang;
        var rawFallback = config.Download.HardSubRawFallback;
        
        _logger?.LogInformation("Stream selection: HardSubLang={HardSubLang}, RawFallback={RawFallback}", hsLang, rawFallback);
        
        if (string.IsNullOrEmpty(hsLang) || hsLang == "none"){
            // Use raw stream (no hardsubs)
            if (playback.HardSubs != null){
                _logger?.LogInformation("Using raw stream (no hardsubs). Available hardsubs: {Available}", 
                    string.Join(", ", playback.HardSubs.Keys));
            }
            playback.IsHardsubbed = false;
            playback.HardsubLang = null;
            return (true, null);
        }
        
        // Looking for hardsub stream
        if (playback.HardSubs == null || playback.HardSubs.Count == 0){
            if (rawFallback){
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
        
        if (exactMatch.Value != null){
            _logger?.LogInformation("Found exact hardsub match: {Lang} -> {Url}", hsLang, exactMatch.Value.Url);
            playback.VideoUrl = exactMatch.Value.Url;
            playback.IsHardsubbed = true;
            playback.HardsubLang = hsLang;
            return (true, null);
        }
        
        // Try language code match (e.g., "en" for "en-US")
        var langPrefix = hsLang.Split('-')[0].ToLowerInvariant();
        var prefixMatch = playback.HardSubs.FirstOrDefault(kvp =>
            kvp.Value.Hlang?.Split('-')[0].ToLowerInvariant() == langPrefix);
        
        if (prefixMatch.Value != null){
            _logger?.LogInformation("Found prefix hardsub match: {Lang} -> {ActualLang} -> {Url}", 
                hsLang, prefixMatch.Value.Hlang, prefixMatch.Value.Url);
            playback.VideoUrl = prefixMatch.Value.Url;
            playback.IsHardsubbed = true;
            playback.HardsubLang = prefixMatch.Value.Hlang;
            return (true, null);
        }
        
        // No match found
        if (rawFallback){
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
    
    private async Task DeAuthVideoAsync(string contentId, string videoToken){
        try{
            var request = HttpClientWrapper.CreateRequest(
                $"https://cr-play-service.prd.crunchyrollsvc.com/v1/token/{contentId}/{videoToken}/inactive",
                HttpMethod.Patch,
                true,
                _auth.Token?.access_token);
            await _httpClient.SendRequestAsync(request, suppressError: true);
        } catch (Exception ex){
            _logger?.LogWarning(ex, "Failed to de-auth video {ContentId}", contentId);
        }
    }
    
    private async Task<PlaybackData?> ParsePlaybackDataAsync(string content, CancellationToken cancellationToken){
        try{
            var playStream = JsonConvert.DeserializeObject<CrunchyStreamData>(content);
            if (playStream == null) return null;
            
            var playback = new PlaybackData();
            playback.VideoToken = playStream.Token;
            
            // Extract URL
            if (!string.IsNullOrEmpty(playStream.Url)){
                if (playStream.Url.Contains(".mpd") || playStream.Url.Contains("/dash/")){
                    playback.VideoUrl = playStream.Url;
                } else{
                    var (video, audio, pssh) = await ParseHlsPlaylistAsync(playStream.Url, cancellationToken);
                    playback.VideoUrl = video;
                    playback.AudioUrl = audio;
                    playback.Pssh = pssh;
                }
            }
            
            // Extract subtitles
            if (playStream.Subtitles != null){
                playback.Subtitles = new List<SubtitleInfo>();
                foreach (var sub in playStream.Subtitles){
                    playback.Subtitles.Add(new SubtitleInfo{
                        Lang = sub.Key,
                        Url = sub.Value.Url ?? "",
                        Format = sub.Value.Format ?? "vtt"
                    });
                }
            }
            
            // Extract hardsubs
            if (playStream.HardSubs != null){
                playback.HardSubs = playStream.HardSubs;
            }
            
            return playback;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse playback data");
            return null;
        }
    }
    
    private async Task<(string? Video, string? Audio, string? Pssh)> ParseHlsPlaylistAsync(string playlistUrl, CancellationToken cancellationToken){
        try{
            var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
            
            if (!isOk){
                return (null, null, null);
            }
            
            // Simple HLS parsing - look for video and audio variant playlists
            var lines = content.Split('\n');
            string? videoUrl = null;
            string? audioUrl = null;
            
            for (int i = 0; i < lines.Length; i++){
                if (lines[i].Contains("VIDEO")){
                    // Video stream
                    if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#")){
                        videoUrl = lines[i + 1].Trim();
                        if (!videoUrl.StartsWith("http")){
                            var baseUri = new Uri(playlistUrl);
                            videoUrl = new Uri(baseUri, videoUrl).ToString();
                        }
                    }
                }
                if (lines[i].Contains("AUDIO")){
                    // Audio stream
                    if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#")){
                        audioUrl = lines[i + 1].Trim();
                        if (!audioUrl.StartsWith("http")){
                            var baseUri = new Uri(playlistUrl);
                            audioUrl = new Uri(baseUri, audioUrl).ToString();
                        }
                    }
                }
            }
            
            // If no video/audio found, the playlist might be a media playlist itself
            if (videoUrl == null && audioUrl == null){
                // Check if this is a media playlist with segments
                if (lines.Any(l => l.StartsWith("#EXTINF"))){
                    videoUrl = playlistUrl;
                }
            }
            
            // Check for DRM PSSH in HLS key tags
            string? pssh = null;
            foreach (var line in lines){
                if (line.StartsWith("#EXT-X-KEY") && line.Contains("URI=\"data:text/plain;base64,")){
                    var match = Regex.Match(line, "URI=\"data:text/plain;base64,([^\"]+)\"");
                    if (match.Success){
                        pssh = match.Groups[1].Value;
                        break;
                    }
                }
            }
            
            return (videoUrl, audioUrl, pssh);
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to parse HLS playlist");
            return (null, null, null);
        }
    }
    
    private async Task DownloadStreamAsync(string url, string outputPath, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken, string? videoToken = null){
        // Direct file download - stream to disk without buffering entire file in memory
        var request = new HttpRequestMessage(HttpMethod.Get, url);
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
        
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0){
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            
            if (totalBytes > 0 && progress != null){
                var now = DateTime.UtcNow;
                var elapsedMs = (now - lastReportTime).TotalMilliseconds;
                // Report progress every ~500ms to avoid flooding
                if (elapsedMs >= 500){
                    var percent = startPercent + (downloaded / (double)totalBytes) * (endPercent - startPercent);
                    var incrementalBytes = downloaded - lastReportedBytes;
                    var speedBytesPerSec = elapsedMs > 0 ? incrementalBytes / (elapsedMs / 1000.0) : 0;
                    if (speedBytesPerSec < 1) speedBytesPerSec = 1;
                    
                    var remainingBytes = totalBytes - downloaded;
                    var etaSec = speedBytesPerSec > 0 ? remainingBytes / speedBytesPerSec : 0;
                    if (etaSec > TimeSpan.MaxValue.TotalSeconds) etaSec = TimeSpan.MaxValue.TotalSeconds;
                    
                    progress.Report(new DownloadProgress{ 
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
    
    private async Task<(string? VideoPath, List<(string Path, string Lang)> AudioPaths)> DownloadDashTracksAsync(string manifestUrl, string tempDir, CruncharrConfig config, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken, string? videoToken = null, string? mediaGuid = null){
        // Download manifest with auth headers
        var manifestRequest = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        manifestRequest.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");
        manifestRequest.Headers.Add("User-Agent", "ANDROIDTV/3.59.0 Android/16");
        if (!string.IsNullOrEmpty(videoToken)){
            manifestRequest.Headers.Add("x-cr-video-token", videoToken);
        }
        
        var (isOk, manifestContent, error) = await _httpClient.SendRequestAsync(manifestRequest);
        if (!isOk || string.IsNullOrEmpty(manifestContent)){
            throw new Exception($"Failed to download DASH manifest: {error}");
        }
        
        // Parse manifest using qma source parser directly
        var streamPlaylists = await Cruncharr.Core.Utils.Parser.MpdParser.Parse(manifestContent, null, manifestUrl, _httpClient.Client);
        
        // Merge all server data
        var videoItems = new List<Cruncharr.Core.Utils.Parser.VideoItem>();
        var audioItems = new List<Cruncharr.Core.Utils.Parser.AudioItem>();
        
        foreach (var serverData in streamPlaylists.Data.Values){
            if (serverData.video != null){
                foreach (var vp in serverData.video){
                    videoItems.Add(new Cruncharr.Core.Utils.Parser.VideoItem{
                        bandwidth = vp.bandwidth,
                        codecs = vp.codecs,
                        quality = vp.quality,
                        resolutionText = $"{vp.quality?.width}x{vp.quality?.height}",
                        segments = vp.segments,
                        pssh = vp.pssh,
                        encryptionKeys = vp.encryptionKeys
                    });
                }
            }
            if (serverData.audio != null){
                foreach (var ap in serverData.audio){
                    audioItems.Add(new Cruncharr.Core.Utils.Parser.AudioItem{
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
        
        if (videoItems.Count == 0 && audioItems.Count == 0){
            throw new Exception("No video or audio tracks found in DASH manifest");
        }
        
        _logger?.LogInformation("Manifest has {VideoCount} video tracks and {AudioCount} audio tracks", videoItems.Count, audioItems.Count);
        
        // Select video/audio tracks using ported upstream logic
        var chosenVideo = SelectVideoTrackQma(videoItems, config.Download.QualityVideo);
        var chosenAudios = SelectAudioTracksUpstream(audioItems, config.Download.DubLanguages);
        
        // Apply QualityAudio filter (ported from upstream DownloadMediaList lines 1874-1895)
        chosenAudios = FilterAudioByQuality(chosenAudios, config.Download.QualityAudio);
        
        _logger?.LogInformation("Selected {AudioCount} audio tracks for download", chosenAudios.Count);
        
        string? videoPath = null;
        var audioPaths = new List<(string Path, string Lang)>();
        
        // Download video using HlsDownloader (qma approach)
        if (chosenVideo != null){
            var videoOutput = chosenVideo.pssh != null 
                ? Path.Combine(tempDir, "video.enc.m4s") 
                : Path.Combine(tempDir, "video.m4s");
            videoPath = Path.Combine(tempDir, "video.m4s");
            
            var videoJson = new Cruncharr.Core.Utils.HLS.M3U8Json{
                Segments = chosenVideo.segments?.Cast<dynamic>().ToList() ?? new List<dynamic>()
            };
            
            var videoDownloader = new Cruncharr.Core.Utils.HLS.HlsDownloader(
                new Cruncharr.Core.Utils.HLS.HlsOptions{
                    Output = videoOutput,
                    M3U8Json = videoJson,
                    Threads = config.Download.PartSize > 0 ? config.Download.PartSize : 5,
                    Retries = config.Download.RetryAttempts,
                    Timeout = 15 * 1000,
                    FsRetryTime = config.Download.RetryDelay * 1000
                }, 
                true, false, config.Download.DownloadMethodeNew, 
                _httpClient.Client, config, progress, cancellationToken);
            
            _logger?.LogInformation("Downloading video stream to {Path}", videoOutput);
            var videoResult = await videoDownloader.Download();
            
            if (!videoResult.Ok){
                throw new Exception("Video track download failed");
            }
            
            // Decrypt if needed
            if (chosenVideo.pssh != null && _widevine.CanDecrypt){
                var authData = new Dictionary<string, string>{
                    { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                    { "x-cr-content-id", mediaGuid ?? "" },
                    { "x-cr-video-token", videoToken ?? "" }
                };
                
                var keys = await _widevine.GetKeysAsync(chosenVideo.pssh, ApiUrls.WidevineLicenceUrl, authData, _httpClient.Client);
                if (keys.Count > 0){
                    await DecryptWithMp4Decrypt(videoOutput, videoPath, keys);
                } else{
                    _logger?.LogWarning("No decryption keys obtained, video may be unplayable");
                    videoPath = videoOutput; // Return encrypted file path since decryption failed
                }
            } else if (chosenVideo.pssh != null){
                videoPath = videoOutput; // Return encrypted file path since we can't decrypt
            }
        }
        
        // Download audio tracks using HlsDownloader (qma approach)
        if (chosenAudios.Count > 0){
            double audioStartPercent = startPercent + (endPercent - startPercent) * 0.6;
            double audioRange = (endPercent - startPercent) * 0.4;
            double perAudioPercent = audioRange / chosenAudios.Count;
            
            for (int i = 0; i < chosenAudios.Count; i++){
                var (audioItem, lang) = chosenAudios[i];
                var langCode = lang.Replace("-", "").ToLower();
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
                
                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = audioStartPercent + (i * perAudioPercent), Doing = $"Downloading audio ({lang})..." });
                
                var audioJson = new Cruncharr.Core.Utils.HLS.M3U8Json{
                    Segments = audioItem.segments?.Cast<dynamic>().ToList() ?? new List<dynamic>()
                };
                
                var audioDownloader = new Cruncharr.Core.Utils.HLS.HlsDownloader(
                    new Cruncharr.Core.Utils.HLS.HlsOptions{
                        Output = audioOutput,
                        M3U8Json = audioJson,
                        Threads = config.Download.PartSize > 0 ? config.Download.PartSize : 5,
                        Retries = config.Download.RetryAttempts,
                        Timeout = 15 * 1000,
                        FsRetryTime = config.Download.RetryDelay * 1000
                    }, 
                    false, true, config.Download.DownloadMethodeNew, 
                    _httpClient.Client, config, progress, cancellationToken);
                
                _logger?.LogInformation("Downloading audio stream ({Lang}) to {Path}", lang, audioOutput);
                var audioResult = await audioDownloader.Download();
                
                if (audioResult.Ok){
                    var audioReturnPath = audioFinalPath;
                    
                    // Decrypt if needed
                    if (audioItem.pssh != null && _widevine.CanDecrypt){
                        var authData = new Dictionary<string, string>{
                            { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                            { "x-cr-content-id", mediaGuid ?? "" },
                            { "x-cr-video-token", videoToken ?? "" }
                        };
                        
                        var keys = await _widevine.GetKeysAsync(audioItem.pssh, ApiUrls.WidevineLicenceUrl, authData, _httpClient.Client);
                        if (keys.Count > 0){
                            await DecryptWithMp4Decrypt(audioOutput, audioFinalPath, keys);
                        } else{
                            _logger?.LogWarning("No decryption keys obtained for audio, may be unplayable");
                            audioReturnPath = audioOutput; // Return encrypted file path since decryption failed
                        }
                    } else if (audioItem.pssh != null){
                        audioReturnPath = audioOutput; // Return encrypted file path since we can't decrypt
                    }
                    
                    audioPaths.Add((audioReturnPath, lang));
                } else{
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
    
    private List<Cruncharr.Core.Utils.Parser.VideoItem> DeduplicateVideoTracks(List<Cruncharr.Core.Utils.Parser.VideoItem> videos){
        return videos
            .GroupBy(v => new{ v.quality?.height, WB = WidthBucket(v.quality?.width ?? 0, v.quality?.height ?? 0) })
            .Select(g => g.OrderByDescending(v => v.bandwidth).First())
            .OrderBy(v => v.quality?.height)
            .ThenBy(v => v.bandwidth)
            .ToList();
    }
    
    // Ported from upstream Helpers.WidthBucket
    // Normalizes widths that are approximately 16:9 to the expected 16:9 width,
    // while keeping non-standard widths as-is. Used for video deduplication.
    private static int WidthBucket(int width, int height){
        if (height == 0) return width;
        int expected = (int)Math.Round(height * 16 / 9.0);
        int tol = Math.Max(8, (int)(expected * 0.02)); // ~2% or >=8 px
        return Math.Abs(width - expected) <= tol ? expected : width;
    }
    
    private Cruncharr.Core.Utils.Parser.VideoItem? SelectVideoTrackQma(List<Cruncharr.Core.Utils.Parser.VideoItem> videos, string qualityPreference){
        if (videos.Count == 0) return null;
        
        var deduped = DeduplicateVideoTracks(videos);
        
        if (string.IsNullOrWhiteSpace(qualityPreference)){
            qualityPreference = "best";
        }
        
        int dedupedCount = deduped.Count;
        int chosenIndex;
        if (qualityPreference == "best"){
            chosenIndex = dedupedCount;
        } else if (qualityPreference == "worst"){
            chosenIndex = 1;
        } else{
            var heightStr = qualityPreference.Replace("p", "").Trim();
            if (int.TryParse(heightStr, out var targetHeight)){
                var matchIndex = deduped.FindIndex(v => v.quality?.height == targetHeight);
                if (matchIndex >= 0){
                    chosenIndex = matchIndex + 1;
                } else{
                    chosenIndex = dedupedCount;
                }
            } else{
                chosenIndex = dedupedCount;
            }
        }
        
        if (chosenIndex > dedupedCount){
            chosenIndex = dedupedCount;
        }
        
        return deduped[chosenIndex - 1];
    }
    
    // Ported from upstream CrunchyrollManager.cs DownloadMediaList
    // Selects audio tracks matching configured DubLanguages, deduplicated by language+bandwidth bucket
    private List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> SelectAudioTracksUpstream(
        List<Cruncharr.Core.Utils.Parser.AudioItem> audioTracks, List<string> languages){
        if (audioTracks.Count == 0 || languages.Count == 0) return [];
        
        // Upstream deduplication: group by language + bandwidth bucket, pick best in each group
        var deduped = audioTracks
            .Select(a => new{
                Item = a,
                Lang = string.IsNullOrWhiteSpace(a.language?.CrLocale) ? "und" : a.language.CrLocale,
                Bucket = SnapToAudioBucket(ToKbps(a.bandwidth))
            })
            .GroupBy(x => new{ x.Lang, x.Bucket })
            .Select(g => g.OrderByDescending(x => x.Item.@default)
                .ThenByDescending(x => x.Item.audioSamplingRate)
                .ThenByDescending(x => x.Item.bandwidth)
                .First().Item)
            .ToList();
        
        // Sort by configured DubLanguages order
        var rank = languages
            .Select((val, i) => new{ val, i })
            .ToDictionary(x => x.val.ToLowerInvariant(), x => x.i, StringComparer.OrdinalIgnoreCase);
        
        var sorted = deduped
            .OrderBy(a => {
                var key = a.language?.CrLocale ?? string.Empty;
                return rank.TryGetValue(key, out var r) ? r : int.MaxValue;
            })
            .ToList();
        
        return sorted.Select(a => (a, a.language?.CrLocale ?? "und")).ToList();
    }
    
    // Ported from upstream Helpers.SnapToAudioBucket
    private static int SnapToAudioBucket(double kbps){
        var buckets = new[]{ 32, 64, 96, 128, 160, 192, 256, 320, 500 };
        foreach (var bucket in buckets.OrderBy(b => b)){
            if (kbps <= bucket) return bucket;
        }
        return buckets.Last();
    }
    
    // Ported from upstream Helpers.ToKbps
    private static double ToKbps(long bandwidth) => bandwidth / 1000.0;
    
    // Ported from upstream DownloadMediaList lines 1874-1895
    // Filters audio tracks by QualityAudio setting (best, worst, or specific bandwidth)
    private List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> FilterAudioByQuality(
        List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)> audioTracks, string qualityPreference){
        if (audioTracks.Count == 0) return audioTracks;
        
        // Group by language
        var grouped = audioTracks.GroupBy(a => a.Language).ToList();
        var result = new List<(Cruncharr.Core.Utils.Parser.AudioItem Track, string Language)>();
        
        foreach (var group in grouped){
            var tracks = group.OrderBy(a => a.Track.bandwidth).ToList();
            
            int chosenIndex;
            if (qualityPreference == "best"){
                chosenIndex = tracks.Count - 1; // Last = highest bandwidth
            } else if (qualityPreference == "worst"){
                chosenIndex = 0; // First = lowest bandwidth
            } else{
                // Try to match specific quality (e.g., "128kB/s" or bucket string)
                var matchIndex = tracks.FindIndex(a => 
                    a.Track.resolutionTextSnap?.Equals(qualityPreference, StringComparison.OrdinalIgnoreCase) == true ||
                    a.Track.resolutionText?.Equals(qualityPreference, StringComparison.OrdinalIgnoreCase) == true);
                if (matchIndex >= 0){
                    chosenIndex = matchIndex;
                } else{
                    chosenIndex = tracks.Count - 1; // Fallback to best
                }
            }
            
            if (chosenIndex >= 0 && chosenIndex < tracks.Count){
                result.Add(tracks[chosenIndex]);
                _logger?.LogInformation("QualityAudio [{Quality}]: Selected {Bandwidth}kbps for {Language}", 
                    qualityPreference, ToKbps(tracks[chosenIndex].Track.bandwidth), group.Key);
            }
        }
        
        return result;
    }
    
    private async Task DecryptWithMp4Decrypt(string inputPath, string outputPath, List<ContentKey> keys){
        if (keys.Count == 0) return;
        
        // Find decryptor tool (prefer shaka-packager, fallback to mp4decrypt)
        string? decryptToolPath = null;
        bool useShaka = false;
        
        var mp4decryptPath = FindExecutable("mp4decrypt");
        var shakaPath = FindExecutable("shaka-packager");
        
        if (shakaPath != null){
            decryptToolPath = shakaPath;
            useShaka = true;
        } else if (mp4decryptPath != null){
            decryptToolPath = mp4decryptPath;
        } else{
            _logger?.LogError("No decryptor found (mp4decrypt or shaka-packager). Cannot decrypt {Input}", inputPath);
            return;
        }
        
        _logger?.LogInformation("Decrypting {Input} -> {Output} using {Tool}", inputPath, outputPath, useShaka ? "shaka-packager" : "mp4decrypt");
        
        if (useShaka){
            var shakaKeys = BuildShakaKeysParam(keys);
            var streamType = inputPath.Contains("audio") ? "audio" : "video";
            var args = new List<string>{
                $"input=\"{inputPath}\",stream={streamType},output=\"{outputPath}\"",
                shakaKeys
            };
            await RunProcessAsync(decryptToolPath!, args, CancellationToken.None);
        } else{
            var args = new List<string>{ "--show-progress" };
            foreach (var key in keys){
                args.Add("--key");
                args.Add($"{FormatKey(key.KeyID)}:{FormatKey(key.Bytes)}");
            }
            args.Add(inputPath);
            args.Add(outputPath);
            await RunProcessAsync(decryptToolPath!, args, CancellationToken.None);
        }
        
        if (File.Exists(outputPath)){
            _logger?.LogInformation("Decryption complete: {Output}", outputPath);
            // Clean up encrypted file
            try{
                File.Delete(inputPath);
                File.Delete(inputPath + ".resume");
            } catch{
                // Ignore cleanup errors
            }
        } else{
            _logger?.LogError("Decryption failed for {Input} - output not found", inputPath);
        }
    }
    
    private async Task<List<string>> DecryptFilesAsync(List<string> encryptedFiles, List<ContentKey> keys, CancellationToken cancellationToken){
        var decryptedFiles = new List<string>();
        
        // Find decryptor tool
        string? decryptToolPath = null;
        bool useShaka = false;
        
        // Check for mp4decrypt first, then shaka-packager
        var mp4decryptPath = FindExecutable("mp4decrypt");
        var shakaPath = FindExecutable("shaka-packager");
        
        if (shakaPath != null){
            decryptToolPath = shakaPath;
            useShaka = true;
        } else if (mp4decryptPath != null){
            decryptToolPath = mp4decryptPath;
        } else{
            _logger?.LogWarning("No decryptor found (mp4decrypt or shaka-packager). Files remain encrypted.");
            return encryptedFiles;
        }
        
        foreach (var file in encryptedFiles){
            // Skip files that are already decrypted (no .enc extension)
            if (!file.Contains(".enc")){
                _logger?.LogDebug("Skipping already-decrypted file: {File}", file);
                decryptedFiles.Add(file);
                continue;
            }
            
            var decryptedPath = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file).Replace(".enc", "") + Path.GetExtension(file));
            _logger?.LogInformation("Decrypting {File} -> {DecryptedPath}", file, decryptedPath);
            
            if (useShaka){
                // Shaka-packager command
                var shakaKeys = BuildShakaKeysParam(keys);
                var streamType = file.Contains("audio") ? "audio" : "video";
                var args = new List<string>{
                    $"input=\"{file}\",stream={streamType},output=\"{decryptedPath}\"",
                    shakaKeys
                };
                
                _logger?.LogInformation("Running shaka-packager: {Args}", string.Join(" ", args));
                await RunProcessAsync(decryptToolPath!, args, cancellationToken);
            } else{
                // mp4decrypt command
                var args = new List<string>{ "--show-progress" };
                foreach (var key in keys){
                    args.Add("--key");
                    args.Add($"{FormatKey(key.KeyID)}:{FormatKey(key.Bytes)}");
                }
                args.Add(file);
                args.Add(decryptedPath);
                
                _logger?.LogInformation("Running mp4decrypt with {KeyCount} keys", keys.Count);
                await RunProcessAsync(decryptToolPath!, args, cancellationToken);
            }
            
            if (File.Exists(decryptedPath)){
                _logger?.LogInformation("Decryption successful: {DecryptedPath}", decryptedPath);
                decryptedFiles.Add(decryptedPath);
                // Clean up encrypted file
                try{
                    File.Delete(file);
                } catch{
                    // Ignore cleanup errors
                }
            } else{
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
    
    private async Task MuxFilesAsync(List<string> mediaFiles, List<(string Path, string Lang)> audioTracks, List<(string Path, string Lang)> subtitles, string? chapterFile, List<FontAttachment> fonts, string? coverPath, string outputPath, CruncharrConfig config, CancellationToken cancellationToken, Dictionary<string, int>? audioDelays = null){
        var mergerOptions = new MergerOptions{
            Output = outputPath,
            VideoTitle = config.Download.VideoTitle,
            DubLangList = config.Download.DubLanguages,
            SubLangList = config.Download.SoftSubs,
            SkipSubMux = config.Download.SkipSubMux,
            CcSubsMuxingFlag = config.Download.CcSubsMuxingFlag,
            SignsSubsAsForced = config.Download.SignsSubsAsForced,
            DefaultSubSigns = config.Download.MuxDefaultSubSigns,
            DefaultSubForcedDisplay = config.Download.MuxDefaultSubForcedDisplay,
            Options = new MuxOptions{
                Ffmpeg = config.Download.FfmpegOptions,
                Mkvmerge = config.Download.MkvmergeOptions
            },
            Defaults = new Defaults{
                Video = Languages.FindLang(config.Download.DefaultVideo),
                Audio = Languages.FindLang(config.Download.DefaultAudio),
                Sub = Languages.FindLang(config.Download.DefaultSub)
            }
        };
        
        // Map video and audio files
        _logger?.LogInformation("MUX DEBUG: mediaFiles count={Count}, audioTracks count={AudioCount}", mediaFiles.Count, audioTracks.Count);
        foreach (var f in mediaFiles) _logger?.LogInformation("MUX DEBUG: mediaFile: {File}", f);
        foreach (var a in audioTracks) _logger?.LogInformation("MUX DEBUG: audioTrack: {Path} / {Lang}", a.Path, a.Lang);
        
        foreach (var file in mediaFiles){
            var audioTrack = audioTracks.FirstOrDefault(a => a.Path == file);
            if (audioTrack != default){
                // Audio file
                _logger?.LogInformation("MUX DEBUG: Adding to OnlyAudio: {File} ({Lang})", file, audioTrack.Lang);
                var mergerInput = new MergerInput{
                    Path = file,
                    Language = Languages.FindLang(audioTrack.Lang)
                };
                // Apply sync delay if available
                if (audioDelays != null && audioDelays.TryGetValue(audioTrack.Lang, out var delay)){
                    mergerInput.Delay = delay;
                    _logger?.LogInformation("MUX DEBUG: Applying sync delay {Delay}ms to audio: {Lang}", delay, audioTrack.Lang);
                }
                mergerOptions.OnlyAudio.Add(mergerInput);
            } else{
                // Video-only file
                _logger?.LogInformation("MUX DEBUG: Adding to OnlyVid: {File}", file);
                mergerOptions.OnlyVid.Add(new MergerInput{
                    Path = file,
                    Language = Languages.DEFAULT_lang
                });
            }
        }
        
        _logger?.LogInformation("MUX DEBUG: OnlyVid.Count={OnlyVid}, OnlyAudio.Count={OnlyAudio}, Subtitles.Count={Subs}",
            mergerOptions.OnlyVid.Count, mergerOptions.OnlyAudio.Count, mergerOptions.Subtitles.Count);
        
        // Map subtitles
        foreach (var (path, lang) in subtitles){
            mergerOptions.Subtitles.Add(new SubtitleInput{
                File = path,
                Language = Languages.FindLang(lang),
                ClosedCaption = false,
                Signs = false
            });
        }
        
        // Map chapter file
        if (!string.IsNullOrEmpty(chapterFile)){
            mergerOptions.Chapters.Add(new MergerInput{
                Path = chapterFile
            });
        }
        
        // Map cover
        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath)){
            mergerOptions.Cover.Add(new MergerInput{
                Path = coverPath
            });
        }
        
        // Map fonts
        foreach (var font in fonts){
            mergerOptions.Fonts.Add(new ParsedFont{
                Name = font.Name,
                Path = font.Path,
                Mime = font.Mime
            });
        }
        
        var merger = new Merger(mergerOptions);
        
        // Try mkvmerge first, fallback to ffmpeg
        var mkvmergePath = FindExecutable("mkvmerge");
        var ffmpegPath = FindExecutable("ffmpeg");
        
        bool success = false;
        if (mkvmergePath != null){
            success = await merger.Merge("mkvmerge", mkvmergePath, cancellationToken);
        }
        
        if (!success && ffmpegPath != null){
            success = await merger.Merge("ffmpeg", ffmpegPath, cancellationToken);
        }
        
        if (!success){
            _logger?.LogWarning("Muxing failed. Files left in temp directory.");
        } else if (!config.Download.NoCleanup){
            merger.CleanUp();
        }
    }
    
    private async Task RunProcessAsync(string executable, List<string> args, CancellationToken cancellationToken){
        var startInfo = new ProcessStartInfo{
            FileName = executable,
            Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        _logger?.LogDebug("Running: {Executable} {Args}", executable, startInfo.Arguments);
        
        using var process = Process.Start(startInfo);
        if (process == null){
            _logger?.LogError("Failed to start process: {Executable}", executable);
            return;
        }
        
        await process.WaitForExitAsync(cancellationToken);
        
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        
        if (process.ExitCode != 0){
            _logger?.LogError("Process failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            if (!string.IsNullOrEmpty(output)){
                _logger?.LogError("Process output: {Output}", output);
            }
        } else{
            if (!string.IsNullOrEmpty(output)){
                _logger?.LogDebug("Process output: {Output}", output);
            }
            if (!string.IsNullOrEmpty(error)){
                _logger?.LogDebug("Process stderr: {Error}", error);
            }
        }
    }
    
    private async Task EncodeOutputAsync(string inputPath, string presetName, CancellationToken cancellationToken){
        var preset = _encodingService?.GetPreset(presetName);
        if (preset == null){
            _logger?.LogWarning("Encoding preset {PresetName} not found", presetName);
            return;
        }
        
        var ffmpegPath = FindExecutable("ffmpeg");
        if (ffmpegPath == null){
            _logger?.LogError("ffmpeg not found for encoding");
            return;
        }
        
        var tempOutput = inputPath + ".encoding.mkv";
        var args = new List<string>{
            "-y",
            "-i", $"\"{inputPath}\"",
            "-c:v", preset.Codec ?? "libx264",
            "-crf", preset.Crf.ToString(),
            "-vf", $"\"scale={preset.Resolution}\"",
            "-r", preset.FrameRate ?? "24000/1001"
        };
        
        args.AddRange(preset.AdditionalParameters);
        args.Add($"\"{tempOutput}\"");
        
        await RunProcessAsync(ffmpegPath, args, cancellationToken);
        
        if (File.Exists(tempOutput)){
            File.Delete(inputPath);
            File.Move(tempOutput, inputPath);
            _logger?.LogInformation("Encoded output to {Path} with preset {Preset}", inputPath, presetName);
        }
    }
    
    private string? FindExecutable(string name){
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var path in paths){
            var fullPath = Path.Combine(path, name);
            if (File.Exists(fullPath)) return fullPath;
            
            // Windows
            if (File.Exists(fullPath + ".exe")) return fullPath + ".exe";
        }
        return null;
    }
    
    private static bool IsHlsUrl(string? url){
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains(".m3u8") || url.Contains("/hls/");
    }
    
    private async Task<(bool Ok, PartsData Parts)> DownloadHlsStreamAsync(string playlistUrl, string outputPath, bool isVideo, bool isAudio, CruncharrConfig config, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken){
        try{
            // Download playlist
            var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            var (isOk, content, _) = await _httpClient.SendRequestAsync(request);
            if (!isOk || string.IsNullOrEmpty(content)){
                _logger?.LogError("Failed to download HLS playlist from {Url}", playlistUrl);
                return (false, new PartsData());
            }
            
            // Parse playlist
            var m3u8 = M3u8MediaPlaylistParser.Parse(content, playlistUrl);
            
            if (m3u8.Segments == null || m3u8.Segments.Count == 0){
                _logger?.LogWarning("No segments found in HLS playlist");
                return (false, new PartsData());
            }
            
            int segmentCount = ((List<dynamic>)m3u8.Segments).Count;
            _logger?.LogInformation("HLS playlist has {Count} segments", segmentCount);
            
            // Download with HlsDownloader
            var options = new HlsOptions{
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
                new Progress<DownloadProgress>(p =>{
                    if (progress != null && p.Percent > 0){
                        var overallPercent = startPercent + (p.Percent / 100.0) * (endPercent - startPercent);
                        progress.Report(new DownloadProgress{
                            State = p.State,
                            Percent = overallPercent,
                            Doing = p.Doing,
                            DownloadSpeedBytes = p.DownloadSpeedBytes
                        });
                    }
                }), cancellationToken);
            
            return await downloader.Download();
        } catch (Exception ex){
            _logger?.LogError(ex, "HLS download failed for {Url}", playlistUrl);
            return (false, new PartsData());
        }
    }
    
    private static string ConvertVttToAss(string vttContent, string language){
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine($"Title: {language} Subtitle");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("PlayResX: 640");
        sb.AppendLine("PlayResY: 360");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine("Style: Default, Arial, 20, \u0026H00FFFFFF, \u0026H000000FF, \u0026H00000000, \u0026H00000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 2, 10, 10, 10, 1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        
        var lines = vttContent.Split('\n');
        var timePattern = new Regex(@"^(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})");
        
        for (int i = 0; i < lines.Length; i++){
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line == "WEBVTT" || line.StartsWith("NOTE")) continue;
            
            var match = timePattern.Match(line);
            if (match.Success){
                var start = match.Groups[1].Value.Replace(".", ",");
                var end = match.Groups[2].Value.Replace(".", ",");
                
                var textLines = new List<string>();
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !timePattern.IsMatch(lines[i])){
                    textLines.Add(lines[i].Trim());
                    i++;
                }
                i--;
                
                if (textLines.Count > 0){
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

public class PlaybackData{
    public string? VideoUrl { get; set; }
    public string? AudioUrl { get; set; }
    public string? Pssh { get; set; }
    public string? VideoToken { get; set; }
    public List<SubtitleInfo>? Subtitles { get; set; }
    public Dictionary<string, HardSub>? HardSubs { get; set; }
    public bool IsHardsubbed { get; set; }
    public string? HardsubLang { get; set; }
}

public class SubtitleInfo{
    public string Lang { get; set; } = "";
    public string Url { get; set; } = "";
    public string Format { get; set; } = "vtt";
}
