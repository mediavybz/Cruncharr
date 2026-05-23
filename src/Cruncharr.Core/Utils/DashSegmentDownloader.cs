using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Utils;

public class DashManifest{
    public List<DashTrack> VideoTracks { get; set; } = new();
    public List<DashTrack> AudioTracks { get; set; } = new();
}

public class DashTrack{
    public string Id { get; set; } = "";
    public string Type { get; set; } = ""; // video or audio
    public int Bandwidth { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string Codecs { get; set; } = "";
    public string? Language { get; set; }
    public string? Pssh { get; set; }
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

public class DashSegmentDownloader{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly int _threads;
    private readonly int _maxRetries;
    private readonly int _timeoutMs;
    
    public DashSegmentDownloader(HttpClient httpClient, int threads = 5, int maxRetries = 3, int timeoutMs = 15000, ILogger? logger = null){
        _httpClient = httpClient;
        _logger = logger;
        _threads = threads;
        _maxRetries = maxRetries;
        _timeoutMs = timeoutMs;
    }
    
    public static DashManifest ParseManifest(string manifestXml, string manifestUrl){
        var doc = new XmlDocument();
        doc.LoadXml(manifestXml);
        
        var manifest = new DashManifest();
        var baseUri = new Uri(manifestUrl);
        var baseUrl = $"{baseUri.Scheme}://{baseUri.Host}{baseUri.AbsolutePath.Substring(0, baseUri.AbsolutePath.LastIndexOf('/') + 1)}";
        
        var nsManager = new XmlNamespaceManager(doc.NameTable);
        nsManager.AddNamespace("dash", "urn:mpeg:dash:schema:mpd:2011");
        nsManager.AddNamespace("cenc", "urn:mpeg:cenc:2013");
        
        var period = doc.SelectSingleNode("//dash:Period", nsManager);
        if (period == null) return manifest;
        
        var adaptationSets = period.SelectNodes("dash:AdaptationSet", nsManager);
        if (adaptationSets == null) return manifest;
        
        foreach (XmlNode adaptationSet in adaptationSets){
            var mimeType = adaptationSet.Attributes?["mimeType"]?.Value ?? "";
            var contentType = adaptationSet.Attributes?["contentType"]?.Value ?? "";
            
            var isVideo = mimeType.Contains("video") || contentType == "video";
            var isAudio = mimeType.Contains("audio") || contentType == "audio";
            
            if (!isVideo && !isAudio) continue;
            
            var lang = adaptationSet.Attributes?["lang"]?.Value;
            var representations = adaptationSet.SelectNodes("dash:Representation", nsManager);
            if (representations == null) continue;
            
            foreach (XmlNode representation in representations){
                // Inherit attributes from AdaptationSet if not present on Representation
                var track = new DashTrack{
                    Type = isVideo ? "video" : "audio",
                    Id = representation.Attributes?["id"]?.Value ?? "",
                    Bandwidth = ParseInt(representation.Attributes?["bandwidth"]?.Value),
                    Width = ParseNullableInt(representation.Attributes?["width"]?.Value) ?? ParseNullableInt(adaptationSet.Attributes?["width"]?.Value),
                    Height = ParseNullableInt(representation.Attributes?["height"]?.Value) ?? ParseNullableInt(adaptationSet.Attributes?["height"]?.Value),
                    Codecs = representation.Attributes?["codecs"]?.Value ?? adaptationSet.Attributes?["codecs"]?.Value ?? "",
                    Language = lang
                };
                
                // Get PSSH from ContentProtection (check Representation first, then AdaptationSet)
                var contentProtections = representation.SelectNodes("dash:ContentProtection", nsManager);
                if (contentProtections == null || contentProtections.Count == 0){
                    contentProtections = adaptationSet.SelectNodes("dash:ContentProtection", nsManager);
                }
                if (contentProtections != null){
                    foreach (XmlNode cp in contentProtections){
                        // Try XPath with namespace first
                        var cencPssh = cp.SelectSingleNode("cenc:pssh", nsManager);
                        if (cencPssh == null){
                            // Fallback: iterate child nodes manually
                            foreach (XmlNode child in cp.ChildNodes){
                                if (child.LocalName == "pssh" && child.NamespaceURI == "urn:mpeg:cenc:2013"){
                                    cencPssh = child;
                                    break;
                                }
                            }
                        }
                        if (cencPssh != null){
                            track.Pssh = cencPssh.InnerText.Trim();
                            break;
                        }
                    }
                }
                
                // Get BaseURL
                var baseUrlNode = representation.SelectSingleNode("dash:BaseURL", nsManager);
                var repBaseUrl = baseUrlNode?.InnerText ?? "";
                
                if (!string.IsNullOrEmpty(repBaseUrl)){
                    if (repBaseUrl.StartsWith("http")){
                        track.BaseUrl = repBaseUrl;
                    } else{
                        track.BaseUrl = baseUrl + repBaseUrl;
                    }
                } else{
                    track.BaseUrl = baseUrl;
                }
                
                // Get segments
                var segmentList = representation.SelectSingleNode("dash:SegmentList", nsManager);
                var segmentTemplate = adaptationSet.SelectSingleNode("dash:SegmentTemplate", nsManager);
                var repSegmentTemplate = representation.SelectSingleNode("dash:SegmentTemplate", nsManager);
                
                // Prefer representation-level template over adaptation set level
                if (repSegmentTemplate != null) segmentTemplate = repSegmentTemplate;
                
                if (segmentList != null){
                    ParseSegmentList(segmentList, track, nsManager);
                } else if (segmentTemplate != null){
                    ParseSegmentTemplate(segmentTemplate, track, nsManager);
                } else{
                    // Try SegmentBase with sidx
                    var segmentBase = representation.SelectSingleNode("dash:SegmentBase", nsManager);
                    if (segmentBase != null){
                        ParseSegmentBase(segmentBase, track, nsManager);
                    }
                }
                
                if (isVideo){
                    manifest.VideoTracks.Add(track);
                } else{
                    manifest.AudioTracks.Add(track);
                }
            }
        }
        
        return manifest;
    }
    
    private static void ParseSegmentList(XmlNode segmentList, DashTrack track, XmlNamespaceManager nsManager){
        var initNode = segmentList.SelectSingleNode("dash:Initialization", nsManager);
        if (initNode != null){
            var sourceUrl = initNode.Attributes?["sourceURL"]?.Value;
            if (!string.IsNullOrEmpty(sourceUrl)){
                track.InitSegment = new DashSegment{ Url = ResolveUrl(sourceUrl, track.BaseUrl) };
            }
        }
        
        var segmentUrls = segmentList.SelectNodes("dash:SegmentURL", nsManager);
        if (segmentUrls != null){
            foreach (XmlNode seg in segmentUrls){
                var media = seg.Attributes?["media"]?.Value;
                var mediaRange = seg.Attributes?["mediaRange"]?.Value;
                
                if (!string.IsNullOrEmpty(media)){
                    var segment = new DashSegment{ Url = ResolveUrl(media, track.BaseUrl) };
                    if (!string.IsNullOrEmpty(mediaRange)){
                        var parts = mediaRange.Split('-');
                        if (parts.Length == 2){
                            segment.StartByte = long.Parse(parts[0]);
                            segment.EndByte = long.Parse(parts[1]);
                        }
                    }
                    track.Segments.Add(segment);
                }
            }
        }
    }
    
    private static void ParseSegmentTemplate(XmlNode segmentTemplate, DashTrack track, XmlNamespaceManager nsManager){
        var media = segmentTemplate.Attributes?["media"]?.Value;
        var initialization = segmentTemplate.Attributes?["initialization"]?.Value;
        var timescale = ParseInt(segmentTemplate.Attributes?["timescale"]?.Value);
        if (timescale == 0) timescale = 1;
        
        var startNumber = ParseInt(segmentTemplate.Attributes?["startNumber"]?.Value);
        if (startNumber == 0) startNumber = 1;
        
        var duration = ParseInt(segmentTemplate.Attributes?["duration"]?.Value);
        
        if (!string.IsNullOrEmpty(initialization)){
            var initUrl = initialization.Replace("$RepresentationID$", track.Id);
            track.InitSegment = new DashSegment{ Url = ResolveUrl(initUrl, track.BaseUrl) };
        }
        
        if (!string.IsNullOrEmpty(media)){
            // For template-based segments, we need to know how many segments
            // This is typically determined by the total duration / segment duration
            // For now, we'll need to fetch the segments list or calculate
            // A common pattern is to use SegmentTimeline
            var segmentTimeline = segmentTemplate.SelectSingleNode("dash:SegmentTimeline", nsManager);
            if (segmentTimeline != null){
                var sNodes = segmentTimeline.SelectNodes("dash:S", nsManager);
                if (sNodes != null){
                    int currentNumber = startNumber;
                    foreach (XmlNode s in sNodes){
                        var t = ParseInt(s.Attributes?["t"]?.Value);
                        var d = ParseInt(s.Attributes?["d"]?.Value);
                        var r = ParseInt(s.Attributes?["r"]?.Value);
                        // r attribute means additional repeats, so total count = r + 1
                        // r=0 means 1 segment, r=2 means 3 segments
                        var repeatCount = r + 1;
                        
                        for (int i = 0; i < repeatCount; i++){
                            var segUrl = media.Replace("$RepresentationID$", track.Id).Replace("$Number$", currentNumber.ToString()).Replace("$Number%04d$", currentNumber.ToString("D4"));
                            track.Segments.Add(new DashSegment{
                                Url = ResolveUrl(segUrl, track.BaseUrl),
                                Duration = d / (double)timescale
                            });
                            currentNumber++;
                        }
                    }
                }
            } else if (duration > 0){
                // Without timeline, we can't know segment count from manifest alone
                // This is a limitation - we'd need to calculate from Period duration
                // For now, create placeholder that needs to be resolved differently
            }
        }
    }
    
    private static void ParseSegmentBase(XmlNode segmentBase, DashTrack track, XmlNamespaceManager nsManager){
        var initNode = segmentBase.SelectSingleNode("dash:Initialization", nsManager);
        if (initNode != null){
            var sourceUrl = initNode.Attributes?["sourceURL"]?.Value;
            if (!string.IsNullOrEmpty(sourceUrl)){
                track.InitSegment = new DashSegment{ Url = ResolveUrl(sourceUrl, track.BaseUrl) };
            }
        }
        
        // SegmentBase with sidx requires fetching and parsing sidx
        // This is more complex and would need SIDX parsing
    }
    
    public async Task<bool> DownloadTrackAsync(DashTrack track, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default){
        try{
            _logger?.LogInformation("Downloading DASH {Type} track: {Bandwidth}bps, {Segments} segments", 
                track.Type, track.Bandwidth, track.Segments.Count);
            
            if (track.Segments.Count == 0 && track.InitSegment == null){
                _logger?.LogError("No segments or init segment found for track");
                return false;
            }
            
            var resumePath = outputPath + ".resume";
            var resumeData = LoadResumeData(resumePath);
            
            // Check if we can resume
            if (resumeData != null && 
                resumeData.TotalSegments == track.Segments.Count &&
                resumeData.OutputPath == outputPath){
                _logger?.LogInformation("Resuming download from segment {Completed}/{Total}", resumeData.CompletedSegments.Count, resumeData.TotalSegments);
            } else{
                resumeData = new ResumeData{
                    TotalSegments = track.Segments.Count,
                    CompletedSegments = new HashSet<int>(),
                    OutputPath = outputPath,
                    InitSegmentDownloaded = false
                };
            }
            
            // Download init segment first (if not already downloaded)
            if (track.InitSegment != null && !resumeData.InitSegmentDownloaded){
                _logger?.LogInformation("Downloading init segment");
                var initData = await DownloadSegmentAsync(track.InitSegment, cancellationToken);
                if (initData != null){
                    await File.WriteAllBytesAsync(outputPath, initData, cancellationToken);
                    resumeData.InitSegmentDownloaded = true;
                    SaveResumeData(resumePath, resumeData);
                }
            }
            
            // Download segments in parallel
            if (track.Segments.Count > 0){
                var pendingSegments = Enumerable.Range(0, track.Segments.Count)
                    .Where(i => !resumeData.CompletedSegments.Contains(i))
                    .ToList();
                
                if (pendingSegments.Count == 0){
                    _logger?.LogInformation("All segments already downloaded");
                    File.Delete(resumePath);
                    return true;
                }
                
                var segmentData = new byte[track.Segments.Count][];
                var completedCount = resumeData.CompletedSegments.Count;
                var semaphore = new SemaphoreSlim(_threads);
                var tasks = new List<Task>();
                var resumeLock = new object();
                
                foreach (var index in pendingSegments){
                    var segment = track.Segments[index];
                    
                    tasks.Add(Task.Run(async () =>{
                        await semaphore.WaitAsync(cancellationToken);
                        try{
                            var data = await DownloadSegmentAsync(segment, cancellationToken);
                            segmentData[index] = data ?? Array.Empty<byte>();
                            
                            var completed = Interlocked.Increment(ref completedCount);
                            var percent = (double)completed / track.Segments.Count * 100;
                            progress?.Report(percent);
                            
                            // Update resume data periodically
                            lock (resumeLock){
                                resumeData.CompletedSegments.Add(index);
                                if (completed % 10 == 0 || completed == track.Segments.Count){
                                    SaveResumeData(resumePath, resumeData);
                                }
                            }
                            
                            if (completed % 10 == 0 || completed == track.Segments.Count){
                                _logger?.LogInformation("Downloaded {Completed}/{Total} segments ({Percent:F1}%)", completed, track.Segments.Count, percent);
                            }
                        } finally{
                            semaphore.Release();
                        }
                    }, cancellationToken));
                }
                
                await Task.WhenAll(tasks);
                
                // Write segments to file in order
                _logger?.LogInformation("Writing segments to output file");
                await using var fileStream = new FileStream(outputPath, FileMode.Append, FileAccess.Write);
                for (int i = 0; i < segmentData.Length; i++){
                    if (segmentData[i] != null && segmentData[i].Length > 0){
                        await fileStream.WriteAsync(segmentData[i], cancellationToken);
                    }
                }
            }
            
            // Delete resume file on success
            if (File.Exists(resumePath)){
                File.Delete(resumePath);
            }
            
            _logger?.LogInformation("Download complete: {Path}", outputPath);
            return true;
        } catch (Exception ex){
            _logger?.LogError(ex, "DASH download failed");
            return false;
        }
    }
    
    private static ResumeData? LoadResumeData(string resumePath){
        try{
            if (!File.Exists(resumePath)) return null;
            var json = File.ReadAllText(resumePath);
            return JsonConvert.DeserializeObject<ResumeData>(json);
        } catch{
            return null;
        }
    }
    
    private static void SaveResumeData(string resumePath, ResumeData data){
        try{
            var json = JsonConvert.SerializeObject(data);
            File.WriteAllText(resumePath, json);
        } catch{
            // Ignore resume save errors
        }
    }
    
    private async Task<byte[]?> DownloadSegmentAsync(DashSegment segment, CancellationToken cancellationToken){
        for (int attempt = 0; attempt <= _maxRetries; attempt++){
            try{
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeoutMs);
                
                var request = new HttpRequestMessage(HttpMethod.Get, segment.Url);
                
                if (segment.StartByte.HasValue && segment.EndByte.HasValue){
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(segment.StartByte.Value, segment.EndByte.Value);
                }
                
                var response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            } catch (Exception ex) when (attempt < _maxRetries){
                _logger?.LogWarning("Segment download failed (attempt {Attempt}/{Max}): {Error}", attempt + 1, _maxRetries + 1, ex.Message);
                await Task.Delay(1000 * (attempt + 1), cancellationToken);
            }
        }
        
        return null;
    }
    
    private static string ResolveUrl(string url, string baseUrl){
        if (url.StartsWith("http://") || url.StartsWith("https://")){
            return url;
        }
        if (url.StartsWith("/")){
            var uri = new Uri(baseUrl);
            return $"{uri.Scheme}://{uri.Host}{url}";
        }
        return baseUrl + url;
    }
    
    private static int ParseInt(string? value){
        if (string.IsNullOrEmpty(value)) return 0;
        if (int.TryParse(value, out var result)) return result;
        return 0;
    }
    
    private static int? ParseNullableInt(string? value){
        if (string.IsNullOrEmpty(value)) return null;
        if (int.TryParse(value, out var result)) return result;
        return null;
    }
}

public class ResumeData{
    public int TotalSegments { get; set; }
    public HashSet<int> CompletedSegments { get; set; } = new();
    public string OutputPath { get; set; } = "";
    public bool InitSegmentDownloaded { get; set; }
}
