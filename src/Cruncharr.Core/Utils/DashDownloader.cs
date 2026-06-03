using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Cruncharr.Core.Utils.HLS;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Utils;

// DASH manifest downloader for video/audio track extraction
public class DashDownloader{
    private readonly HttpClientWrapper _httpClient;
    private readonly ILogger? _logger;
    private readonly int _threads;
    private readonly int _maxRetries;
    private readonly int _speedLimitKbPerSecond;
    
    public DashDownloader(HttpClientWrapper httpClient, int threads = 5, int maxRetries = 3, int speedLimitKbPerSecond = 0, ILogger? logger = null){
        _httpClient = httpClient;
        _logger = logger;
        _threads = threads;
        _maxRetries = maxRetries;
        _speedLimitKbPerSecond = speedLimitKbPerSecond;
    }
    
    public async Task<bool> DownloadTrackAsync(DashTrack track, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default){
        try{
            // Download init segment if present
            if (track.InitSegment != null){
                await DownloadSegmentAsync(track.InitSegment, outputPath, true, cancellationToken);
            }
            
            // Download all segments
            int completed = 0;
            int total = track.Segments.Count;
            
            foreach (var segment in track.Segments){
                await DownloadSegmentAsync(segment, outputPath, false, cancellationToken);
                completed++;
                if (completed % 10 == 0){
                    _logger?.LogDebug("Downloaded {Completed}/{Total} segments", completed, total);
                }
                if (completed % 5 == 0){
                    progress?.Report((completed / (double)total) * 100.0);
                }
            }
            
            progress?.Report(100.0);
            return true;
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to download track");
            return false;
        }
    }
    
    private async Task DownloadSegmentAsync(DashSegment segment, string outputPath, bool isInit, CancellationToken cancellationToken){
        var request = new HttpRequestMessage(HttpMethod.Get, segment.Url);
        
        if (segment.StartByte.HasValue){
            long endByte = segment.EndByte ?? (segment.StartByte.Value + 1024 * 1024 * 10); // Default 10MB chunk
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(segment.StartByte.Value, endByte);
        }
        
        using var response = await _httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var mode = isInit ? FileMode.Create : FileMode.Append;
        await using var fileStream = new FileStream(outputPath, mode, FileAccess.Write);
        
        if (_speedLimitKbPerSecond > 0){
            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var throttledStream = new ThrottledStream(contentStream, _speedLimitKbPerSecond);
            await throttledStream.CopyToAsync(fileStream, cancellationToken);
        } else{
            await response.Content.CopyToAsync(fileStream, cancellationToken);
        }
    }
    
    public static async Task<DashManifest> ParseManifestAsync(string manifestXml, string manifestUrl, HttpClient httpClient){
        // Use the ported MpdParser and convert to DashManifest format
        var parsed = await Cruncharr.Core.Utils.Parser.MpdParser.Parse(manifestXml, null, manifestUrl, httpClient);
        return ConvertToDashManifest(parsed);
    }
    
    private static DashManifest ConvertToDashManifest(Cruncharr.Core.Utils.Parser.MPDParsed parsed){
        var manifest = new DashManifest();
        
        foreach (var serverData in parsed.Data.Values){
            // Convert video playlists
            if (serverData.video != null){
                foreach (var vp in serverData.video){
                    var track = new DashTrack{
                        Id = $"video_{vp.bandwidth}",
                        Type = "video",
                        Bandwidth = vp.bandwidth,
                        Width = vp.quality?.width,
                        Height = vp.quality?.height,
                        Codecs = vp.codecs ?? "",
                        Pssh = vp.pssh,
                        BaseUrl = ""
                    };
                    
                    if (vp.segments != null && vp.segments.Count > 0){
                        // Extract init segment from first segment's map
                        var firstSeg = vp.segments[0];
                        if (firstSeg.map != null && !string.IsNullOrEmpty(firstSeg.map.uri)){
                            track.InitSegment = new DashSegment{
                                Url = firstSeg.map.uri,
                                StartByte = firstSeg.map.byteRange?.Offset,
                                EndByte = firstSeg.map.byteRange != null
                                    ? firstSeg.map.byteRange.Offset + firstSeg.map.byteRange.Length - 1
                                    : null
                            };
                        }
                        
                        foreach (var seg in vp.segments){
                            track.Segments.Add(new DashSegment{
                                Url = seg.uri,
                                Duration = seg.duration,
                                StartByte = seg.byteRange?.Offset,
                                EndByte = seg.byteRange != null
                                    ? seg.byteRange.Offset + seg.byteRange.Length - 1
                                    : null
                            });
                        }
                    }
                    
                    manifest.VideoTracks.Add(track);
                }
            }
            
            // Convert audio playlists
            if (serverData.audio != null){
                foreach (var ap in serverData.audio){
                    var roleValue = ObjectUtilities.GetMemberValue(ap, "attributes") != null
                        ? ObjectUtilities.GetMemberValue(ObjectUtilities.GetMemberValue(ap, "attributes"), "role")?.ToString()
                        : null;
                    var track = new DashTrack{
                        Id = $"audio_{ap.language?.CrLocale ?? "unknown"}_{ap.bandwidth}",
                        Type = "audio",
                        Bandwidth = ap.bandwidth,
                        Language = ap.language?.CrLocale,
                        Pssh = ap.pssh,
                        Role = roleValue,
                        BaseUrl = ""
                    };
                    
                    if (ap.segments != null && ap.segments.Count > 0){
                        // Extract init segment from first segment's map
                        var firstSeg = ap.segments[0];
                        if (firstSeg.map != null && !string.IsNullOrEmpty(firstSeg.map.uri)){
                            track.InitSegment = new DashSegment{
                                Url = firstSeg.map.uri,
                                StartByte = firstSeg.map.byteRange?.Offset,
                                EndByte = firstSeg.map.byteRange != null
                                    ? firstSeg.map.byteRange.Offset + firstSeg.map.byteRange.Length - 1
                                    : null
                            };
                        }
                        
                        foreach (var seg in ap.segments){
                            track.Segments.Add(new DashSegment{
                                Url = seg.uri,
                                Duration = seg.duration,
                                StartByte = seg.byteRange?.Offset,
                                EndByte = seg.byteRange != null
                                    ? seg.byteRange.Offset + seg.byteRange.Length - 1
                                    : null
                            });
                        }
                    }
                    
                    manifest.AudioTracks.Add(track);
                }
            }
        }
        
        return manifest;
    }
}

public class DashManifest{
    public List<DashTrack> VideoTracks { get; set; } = new();
    public List<DashTrack> AudioTracks { get; set; } = new();
}

public class DashTrack{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int Bandwidth { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string Codecs { get; set; } = "";
    public string? Language { get; set; }
    public string? Pssh { get; set; }
    public string? Role { get; set; }
    public List<DashSegment> Segments { get; set; } = new();
    public DashSegment? InitSegment { get; set; }
    public string BaseUrl { get; set; } = "";
}

public class DashSegment{
    public string Url { get; set; } = "";
    public long? StartByte { get; set; }
    public long? EndByte { get; set; }
    public double Duration { get; set; }
}


