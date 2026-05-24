using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.DRM;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    
    private readonly IHistoryService? _history;
    
    public DownloadService(ICrunchyrollAuthService auth, ICrunchyrollApiService api, ILogger<DownloadService>? logger = null, IHistoryService? history = null){
        _auth = auth;
        _api = api;
        _logger = logger;
        _history = history;
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
            }
        }
        
        // Select correct episode version based on audio locale (ported from qma)
        // Default to episode.Id (the actual version ID for this language)
        string mediaGuid = episode.Id;
        string mediaId = episode.Id;
        if (episode.Versions != null && episode.Versions.Count > 0){
            EpisodeVersion? currentVersion = null;
            
            // Try to find version matching the episode's audio locale
            if (!string.IsNullOrEmpty(episode.AudioLocale)){
                currentVersion = episode.Versions.FirstOrDefault(v => v.AudioLocale.Equals(episode.AudioLocale, StringComparison.OrdinalIgnoreCase));
            }
            
            // Fallback: try config's default audio
            if (currentVersion == null && !string.IsNullOrEmpty(config.Download.DefaultAudio)){
                currentVersion = episode.Versions.FirstOrDefault(v => v.AudioLocale.Equals(config.Download.DefaultAudio, StringComparison.OrdinalIgnoreCase));
            }
            
            // Fallback: if only one version, use it
            if (currentVersion == null && episode.Versions.Count == 1){
                currentVersion = episode.Versions[0];
            }
            
            if (currentVersion != null){
                mediaGuid = currentVersion.Guid;
                if (!string.IsNullOrEmpty(currentVersion.MediaGuid)){
                    mediaId = currentVersion.MediaGuid;
                }
                _logger?.LogInformation("Selected version: {Guid} (audio_locale={AudioLocale}, original={Original})", currentVersion.Guid, currentVersion.AudioLocale, currentVersion.Original);
            } else{
                _logger?.LogWarning("Could not find matching version for audio locale {AudioLocale}, using default", episode.AudioLocale);
            }
        }
        
        // Strip any prefix from mediaId/mediaGuid
        if (mediaId.Contains(':')){
            mediaId = mediaId.Split(':')[1];
        }
        if (mediaGuid.Contains(':')){
            mediaGuid = mediaGuid.Split(':')[1];
        }
        
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
        
        _logger?.LogInformation("PSSH: {Pssh}", pssh ?? "(null)");
        if (!string.IsNullOrEmpty(pssh) && _widevine.CanDecrypt){
            progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 25, Doing = "Fetching decryption keys..." });
            var authData = new Dictionary<string, string>{
                { "authorization", "Bearer " + (_auth.Token?.access_token ?? "") },
                { "x-cr-content-id", mediaId },
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
        var outputPath = Path.Combine(outputDir, fileName + ".mkv");
        
        // Download streams
        var tempDir = Path.Combine(config.Download.TempDirectory, Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        try{
            var downloadedFiles = new List<string>();
            
            var audioTrackLanguages = new List<(string Path, string Lang)>();
            
            // Handle DASH manifest (contains both video and audio)
            if (playbackData.VideoUrl != null && (playbackData.VideoUrl.Contains(".mpd") || playbackData.VideoUrl.Contains("/dash/"))){
                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 30, Doing = "Downloading DASH streams..." });
                var (videoPath, audioPaths) = await DownloadDashTracksAsync(playbackData.VideoUrl, tempDir, config, progress, 30, 80, cancellationToken, playbackData.VideoToken);
                if (videoPath != null) downloadedFiles.Add(videoPath);
                foreach (var (path, _) in audioPaths){
                    downloadedFiles.Add(path);
                }
                audioTrackLanguages = audioPaths;
            } else{
                // Download video
                if (playbackData.VideoUrl != null){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 30, Doing = "Downloading video..." });
                    var videoPath = Path.Combine(tempDir, "video.mp4");
                    await DownloadStreamAsync(playbackData.VideoUrl, videoPath, progress, 30, 60, cancellationToken, playbackData.VideoToken);
                    downloadedFiles.Add(videoPath);
                }
                
                // Download audio
                if (playbackData.AudioUrl != null){
                    progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = 60, Doing = "Downloading audio..." });
                    var audioPath = Path.Combine(tempDir, "audio.m4a");
                    await DownloadStreamAsync(playbackData.AudioUrl, audioPath, progress, 60, 80, cancellationToken, playbackData.VideoToken);
                    downloadedFiles.Add(audioPath);
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
            
            // Download cover art if available
            string? coverPath = null;
            if (!string.IsNullOrEmpty(episode.CoverArtUrl) && !config.Download.SkipMuxing){
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
            }
            
            // Mux
            progress?.Report(new DownloadProgress{ State = DownloadState.Processing, Percent = 90, Doing = "Muxing..." });
            if (!config.Download.SkipMuxing){
                await MuxFilesAsync(downloadedFiles, audioTrackLanguages, subtitleFiles, chapterFile, fontAttachments, coverPath, outputPath, config, cancellationToken);
            }
            
            progress?.Report(new DownloadProgress{ State = DownloadState.Done, Percent = 100, Doing = "Complete" });
            
            // Record in history
            if (_history != null && config.Download.HistoryEnabled){
                try{
                    var fileInfo = new FileInfo(outputPath);
                    await _history.AddAsync(new DownloadHistory{
                        EpisodeId = episode.Id,
                        SeriesId = episode.Guid,
                        SeriesTitle = episode.SeriesTitle,
                        EpisodeTitle = episode.Title,
                        SeasonNumber = episode.SeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        AudioLanguage = episode.Locale,
                        SubtitleLanguages = subtitleFiles.Select(s => s.Lang).ToList(),
                        DownloadedAt = DateTime.UtcNow,
                        OutputPath = outputPath,
                        FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
                    });
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
            try{
                if (Directory.Exists(tempDir)){
                    Directory.Delete(tempDir, true);
                }
            } catch{
                // Ignore cleanup errors
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
        
        const int maxRetries = 3;
        var endpoints = new[]{
            (Endpoint: $"{ApiUrls.Playback}/{episodeId}/tv/android_tv/play", UserAgent: "ANDROIDTV/3.59.0 Android/16"),
            (Endpoint: $"{ApiUrls.Playback}/{episodeId}/web/firefox/play", UserAgent: ApiUrls.FirefoxUserAgent)
        };
        
        foreach (var (endpoint, userAgent) in endpoints){
            var request = HttpClientWrapper.CreateRequest(endpoint, HttpMethod.Get, true, _auth.Token.access_token);
            request.Headers.Add("User-Agent", userAgent);
            
            var (isOk, content, error, headers) = await _httpClient.SendRequestWithHeadersAsync(request);
            
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
            var data = JsonConvert.DeserializeObject<JObject>(content);
            if (data == null) return null;
            
            var playback = new PlaybackData();
            
            // Extract token (direct key in newer API)
            playback.VideoToken = data["token"]?.ToString();
            
            // Extract URL (direct key in newer API)
            var url = data["url"]?.ToString();
            if (url != null){
                if (url.Contains(".mpd") || url.Contains("/dash/")){
                    // DASH manifest
                    playback.VideoUrl = url;
                } else{
                    // HLS playlist
                    var (video, audio, pssh) = await ParseHlsPlaylistAsync(url, cancellationToken);
                    playback.VideoUrl = video;
                    playback.AudioUrl = audio;
                    playback.Pssh = pssh;
                }
            }
            
            // Extract DRM info
            var drm = data["drm"];
            if (drm != null){
                var pssh = drm["pssh"]?.ToString() ?? drm["widevine"]?.ToString();
                if (!string.IsNullOrEmpty(pssh)){
                    playback.Pssh = pssh;
                }
            }
            
            // Extract subtitles
            var subtitles = data["subtitles"];
            if (subtitles != null){
                playback.Subtitles = new List<SubtitleInfo>();
                foreach (var sub in subtitles){
                    var subProp = sub as JProperty;
                    if (subProp != null){
                        playback.Subtitles.Add(new SubtitleInfo{
                            Lang = subProp.Name,
                            Url = subProp.Value?["url"]?.ToString() ?? "",
                            Format = subProp.Value?["format"]?.ToString() ?? "vtt"
                        });
                    }
                }
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
        // Direct file download
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request);
        
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = File.Create(outputPath);
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[8192];
        long downloaded = 0;
        int read;
        
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0){
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            
            if (totalBytes > 0 && progress != null){
                var percent = startPercent + (downloaded / (double)totalBytes) * (endPercent - startPercent);
                progress.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = percent });
            }
        }
    }
    
    private async Task<(string? VideoPath, List<(string Path, string Lang)> AudioPaths)> DownloadDashTracksAsync(string manifestUrl, string tempDir, CruncharrConfig config, IProgress<DownloadProgress>? progress, double startPercent, double endPercent, CancellationToken cancellationToken, string? videoToken = null){
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
        
        // Parse manifest
        var manifest = DashDownloader.ParseManifest(manifestContent, manifestUrl);
        
        if (manifest.VideoTracks.Count == 0 && manifest.AudioTracks.Count == 0){
            throw new Exception("No video or audio tracks found in DASH manifest");
        }
        
        // Select video/audio tracks using ported quality selection logic
        _logger?.LogInformation("Manifest has {VideoCount} video tracks and {AudioCount} audio tracks", manifest.VideoTracks.Count, manifest.AudioTracks.Count);
        foreach (var audio in manifest.AudioTracks){
            _logger?.LogInformation("Audio track: lang={Lang}, id={Id}, bandwidth={Bandwidth}", audio.Language, audio.Id, audio.Bandwidth);
        }
        _logger?.LogInformation("Config dub languages: {DubLangs}", string.Join(", ", config.Download.DubLanguages));
        
        var videoTrack = QualitySelector.SelectVideoTrack(manifest.VideoTracks, config.Download.QualityVideo);
        var audioTracks = QualitySelector.SelectAudioTracks(manifest.AudioTracks, config.Download.DubLanguages);
        _logger?.LogInformation("Selected {AudioCount} audio tracks for download", audioTracks.Count);
        
        var dashDownloader = new DashDownloader(_httpClient, threads: 5, maxRetries: 3, logger: _logger);
        
        string? videoPath = null;
        var audioPaths = new List<(string Path, string Lang)>();
        
        // Download video
        if (videoTrack != null){
            videoPath = Path.Combine(tempDir, "video.mp4");
            var success = await dashDownloader.DownloadTrackAsync(videoTrack, videoPath, 
                new Progress<double>(percent => {
                    if (progress != null){
                        var overallPercent = startPercent + (percent / 100.0) * (endPercent - startPercent) * 0.6;
                        progress.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = overallPercent });
                    }
                }), cancellationToken);
            
            if (!success){
                throw new Exception("Video track download failed");
            }
        }
        
        // Download audio tracks (multi-dub support)
        if (audioTracks.Count > 0){
            double audioStartPercent = startPercent + (endPercent - startPercent) * 0.6;
            double audioRange = (endPercent - startPercent) * 0.4;
            double perAudioPercent = audioRange / audioTracks.Count;
            
            for (int i = 0; i < audioTracks.Count; i++){
                var (audioTrack, lang) = audioTracks[i];
                var langCode = lang.Replace("-", "").ToLower();
                var audioFileName = audioTracks.Count(a => a.Item2 == lang) > 1 
                    ? $"audio_{langCode}_{i}.m4a" 
                    : $"audio_{langCode}.m4a";
                var audioPath = Path.Combine(tempDir, audioFileName);
                
                progress?.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = audioStartPercent + (i * perAudioPercent), Doing = $"Downloading audio ({lang})..." });
                
                var success = await dashDownloader.DownloadTrackAsync(audioTrack, audioPath, 
                    new Progress<double>(percent => {
                        if (progress != null){
                            var overallPercent = audioStartPercent + (i * perAudioPercent) + (percent / 100.0) * perAudioPercent;
                            progress.Report(new DownloadProgress{ State = DownloadState.Downloading, Percent = overallPercent });
                        }
                    }), cancellationToken);
                
                if (success){
                    audioPaths.Add((audioPath, lang));
                } else{
                    _logger?.LogWarning("Audio track download failed for language {Lang}", lang);
                }
            }
        }
        
        return (videoPath, audioPaths);
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
            var decryptedPath = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "_dec" + Path.GetExtension(file));
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
    
    private async Task MuxFilesAsync(List<string> mediaFiles, List<(string Path, string Lang)> audioTracks, List<(string Path, string Lang)> subtitles, string? chapterFile, List<FontAttachment> fonts, string? coverPath, string outputPath, CruncharrConfig config, CancellationToken cancellationToken){
        // Try mkvmerge first, fallback to ffmpeg
        var mkvmergePath = FindExecutable("mkvmerge");
        var ffmpegPath = FindExecutable("ffmpeg");
        
        if (mkvmergePath != null){
            await MuxWithMkvmergeAsync(mkvmergePath, mediaFiles, audioTracks, subtitles, chapterFile, fonts, coverPath, outputPath, config, cancellationToken);
        } else if (ffmpegPath != null){
            await MuxWithFfmpegAsync(ffmpegPath, mediaFiles, audioTracks, subtitles, chapterFile, fonts, coverPath, outputPath, config, cancellationToken);
        } else{
            _logger?.LogWarning("No muxer found. Files left in temp directory.");
        }
    }
    
    private async Task MuxWithMkvmergeAsync(string mkvmergePath, List<string> mediaFiles, List<(string Path, string Lang)> audioTracks, List<(string Path, string Lang)> subtitles, string? chapterFile, List<FontAttachment> fonts, string? coverPath, string outputPath, CruncharrConfig config, CancellationToken cancellationToken){
        var args = new List<string>{ "-o", outputPath };
        
        if (!string.IsNullOrEmpty(chapterFile)){
            args.Add("--chapters");
            args.Add(chapterFile);
        }
        
        // Add cover art attachment
        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath)){
            args.Add("--attachment-mime-type");
            args.Add("image/png");
            args.Add("--attachment-name");
            args.Add("cover.png");
            args.Add("--attach-file");
            args.Add(coverPath);
        }
        
        // Add font attachments
        foreach (var font in fonts){
            args.Add($"--attachment-name");
            args.Add(font.Name);
            args.Add($"--attachment-mime-type");
            args.Add(font.Mime);
            args.Add($"--attach-file");
            args.Add(font.Path);
        }
        
        // Add media files with language metadata for audio tracks
        foreach (var file in mediaFiles){
            var audioTrack = audioTracks.FirstOrDefault(a => a.Path == file);
            if (audioTrack != default){
                args.Add("--language");
                args.Add($"0:{audioTrack.Lang}");
                // Set default track based on config
                if (audioTrack.Lang.Equals(config.Download.DefaultAudio, StringComparison.OrdinalIgnoreCase)){
                    args.Add("--default-track");
                    args.Add("0:yes");
                } else{
                    args.Add("--default-track");
                    args.Add("0:no");
                }
            }
            args.Add(file);
        }
        
        foreach (var (subPath, lang) in subtitles){
            args.Add("--language");
            args.Add($"0:{lang}");
            args.Add(subPath);
        }
        
        await RunProcessAsync(mkvmergePath, args, cancellationToken);
    }
    
    private async Task MuxWithFfmpegAsync(string ffmpegPath, List<string> mediaFiles, List<(string Path, string Lang)> audioTracks, List<(string Path, string Lang)> subtitles, string? chapterFile, List<FontAttachment> fonts, string? coverPath, string outputPath, CruncharrConfig config, CancellationToken cancellationToken){
        var args = new List<string>{
            "-y",
            "-hide_banner",
            "-loglevel", "error"
        };
        
        // Add chapter metadata file if available
        string? ffmetadataFile = null;
        if (!string.IsNullOrEmpty(chapterFile)){
            ffmetadataFile = ConvertChapterFileToFfmetadata(chapterFile, Path.Combine(Path.GetDirectoryName(chapterFile)!, "ffmetadata.txt"));
            if (ffmetadataFile != null){
                args.Add("-i");
                args.Add(ffmetadataFile);
                args.Add("-map_metadata");
                args.Add("1");
            }
        }
        
        foreach (var file in mediaFiles){
            args.Add("-i");
            args.Add(file);
        }
        
        foreach (var (subPath, lang) in subtitles){
            args.Add("-i");
            args.Add(subPath);
        }
        
        args.Add("-c");
        args.Add("copy");
        args.Add(outputPath);
        
        await RunProcessAsync(ffmpegPath, args, cancellationToken);
        
        // Clean up ffmetadata temp file
        if (!string.IsNullOrEmpty(ffmetadataFile) && File.Exists(ffmetadataFile)){
            try { File.Delete(ffmetadataFile); } catch { }
        }
    }
    
    private string? ConvertChapterFileToFfmetadata(string chapterFilePath, string outputPath){
        try{
            var chapterLines = File.ReadAllLines(chapterFilePath);
            var ffmpegChapterLines = new List<string>{ ";FFMETADATA1" };
            var chapters = new List<(double StartTime, string Title)>();
            
            for (int i = 0; i < chapterLines.Length; i += 2){
                if (i + 1 >= chapterLines.Length) break;
                var timeLine = chapterLines[i];
                var nameLine = chapterLines[i + 1];
                
                var timeParts = timeLine.Split('=');
                var nameParts = nameLine.Split('=');
                
                if (timeParts.Length == 2 && nameParts.Length == 2){
                    var startTime = TimeSpan.Parse(timeParts[1]).TotalMilliseconds;
                    var title = nameParts[1];
                    chapters.Add((startTime, title));
                }
            }
            
            chapters = chapters.OrderBy(c => c.StartTime).ToList();
            
            for (int i = 0; i < chapters.Count; i++){
                var startTime = chapters[i].StartTime;
                var title = chapters[i].Title;
                var endTime = (i + 1 < chapters.Count) ? chapters[i + 1].StartTime : startTime + 10000;
                
                if (endTime < startTime){
                    endTime = startTime + 10000;
                }
                
                ffmpegChapterLines.Add("[CHAPTER]");
                ffmpegChapterLines.Add("TIMEBASE=1/1000");
                ffmpegChapterLines.Add($"START={startTime}");
                ffmpegChapterLines.Add($"END={endTime}");
                ffmpegChapterLines.Add($"title={title}");
            }
            
            File.WriteAllLines(outputPath, ffmpegChapterLines);
            return outputPath;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to convert chapter file to FFMetadata");
            return null;
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
}

public class SubtitleInfo{
    public string Lang { get; set; } = "";
    public string Url { get; set; } = "";
    public string Format { get; set; } = "vtt";
}
