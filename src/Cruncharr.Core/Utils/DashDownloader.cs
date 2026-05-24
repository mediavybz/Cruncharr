using System.Xml;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Utils;

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

public class DashDownloader{
    private readonly HttpClientWrapper _httpClient;
    private readonly ILogger? _logger;
    private readonly int _threads;
    private readonly int _maxRetries;
    
    public DashDownloader(HttpClientWrapper httpClient, int threads = 5, int maxRetries = 3, ILogger? logger = null){
        _httpClient = httpClient;
        _logger = logger;
        _threads = threads;
        _maxRetries = maxRetries;
    }
    
    public static DashManifest ParseManifest(string manifestXml, string manifestUrl){
        // Inject BaseURL if missing (like original does)
        if (!manifestXml.Contains("BaseURL") && !string.IsNullOrEmpty(manifestUrl)){
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(manifestXml);
            XmlElement? mpd = doc.DocumentElement;
            if (mpd != null && mpd.Name == "MPD"){
                string dashNs = "urn:mpeg:dash:schema:mpd:2011";
                XmlElement baseUrlElement = doc.CreateElement("BaseURL", dashNs);
                baseUrlElement.InnerText = manifestUrl;
                mpd.InsertBefore(baseUrlElement, mpd.FirstChild);
                manifestXml = doc.OuterXml;
            }
        }
        
        XmlDocument xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(manifestXml);
        
        var manifest = new DashManifest();
        var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
        nsManager.AddNamespace("dash", "urn:mpeg:dash:schema:mpd:2011");
        nsManager.AddNamespace("cenc", "urn:mpeg:cenc:2013");
        
        var period = xmlDoc.SelectSingleNode("//dash:Period", nsManager);
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
                var track = new DashTrack{
                    Type = isVideo ? "video" : "audio",
                    Id = representation.Attributes?["id"]?.Value ?? "",
                    Bandwidth = ParseInt(representation.Attributes?["bandwidth"]?.Value),
                    Width = ParseNullableInt(representation.Attributes?["width"]?.Value) ?? ParseNullableInt(adaptationSet.Attributes?["width"]?.Value),
                    Height = ParseNullableInt(representation.Attributes?["height"]?.Value) ?? ParseNullableInt(adaptationSet.Attributes?["height"]?.Value),
                    Codecs = representation.Attributes?["codecs"]?.Value ?? adaptationSet.Attributes?["codecs"]?.Value ?? "",
                    Language = lang
                };
                
                // Get PSSH from ContentProtection
                var contentProtections = representation.SelectNodes("dash:ContentProtection", nsManager);
                if (contentProtections == null || contentProtections.Count == 0){
                    contentProtections = adaptationSet.SelectNodes("dash:ContentProtection", nsManager);
                }
                if (contentProtections != null){
                    foreach (XmlNode cp in contentProtections){
                        var cencPssh = cp.SelectSingleNode("cenc:pssh", nsManager);
                        if (cencPssh == null){
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
                
                // Get BaseURL - use the full manifest URL as base (injected above)
                var baseUrlNode = xmlDoc.SelectSingleNode("//dash:BaseURL", nsManager);
                string baseUrl = baseUrlNode?.InnerText ?? manifestUrl;
                
                // Get segments
                var segmentList = representation.SelectSingleNode("dash:SegmentList", nsManager);
                var segmentTemplate = adaptationSet.SelectSingleNode("dash:SegmentTemplate", nsManager);
                var repSegmentTemplate = representation.SelectSingleNode("dash:SegmentTemplate", nsManager);
                
                if (repSegmentTemplate != null) segmentTemplate = repSegmentTemplate;
                
                if (segmentList != null){
                    ParseSegmentList(segmentList, track, nsManager, baseUrl);
                } else if (segmentTemplate != null){
                    ParseSegmentTemplate(segmentTemplate, track, nsManager, baseUrl);
                } else{
                    var segmentBase = representation.SelectSingleNode("dash:SegmentBase", nsManager);
                    if (segmentBase != null){
                        ParseSegmentBase(segmentBase, track, nsManager, baseUrl);
                    }
                }
                
                track.BaseUrl = baseUrl;
                
                if (isVideo){
                    manifest.VideoTracks.Add(track);
                } else{
                    manifest.AudioTracks.Add(track);
                }
            }
        }
        
        return manifest;
    }
    
    private static void ParseSegmentList(XmlNode segmentList, DashTrack track, XmlNamespaceManager nsManager, string baseUrl){
        var initNode = segmentList.SelectSingleNode("dash:Initialization", nsManager);
        if (initNode != null){
            var sourceUrl = initNode.Attributes?["sourceURL"]?.Value;
            if (!string.IsNullOrEmpty(sourceUrl)){
                track.InitSegment = new DashSegment{ Url = UrlUtils.ResolveUrl(baseUrl, sourceUrl) };
            }
        }
        
        var segmentUrls = segmentList.SelectNodes("dash:SegmentURL", nsManager);
        if (segmentUrls != null){
            foreach (XmlNode seg in segmentUrls){
                var media = seg.Attributes?["media"]?.Value;
                var mediaRange = seg.Attributes?["mediaRange"]?.Value;
                
                if (!string.IsNullOrEmpty(media)){
                    var segment = new DashSegment{ Url = UrlUtils.ResolveUrl(baseUrl, media) };
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
    
    private static void ParseSegmentTemplate(XmlNode segmentTemplate, DashTrack track, XmlNamespaceManager nsManager, string baseUrl){
        var media = segmentTemplate.Attributes?["media"]?.Value;
        var initialization = segmentTemplate.Attributes?["initialization"]?.Value;
        var timescale = ParseInt(segmentTemplate.Attributes?["timescale"]?.Value);
        if (timescale == 0) timescale = 1;
        
        var startNumber = ParseInt(segmentTemplate.Attributes?["startNumber"]?.Value);
        if (startNumber == 0) startNumber = 1;
        
        var duration = ParseInt(segmentTemplate.Attributes?["duration"]?.Value);
        
        if (!string.IsNullOrEmpty(initialization)){
            var initUrl = ReplaceTemplateVariables(initialization, track.Id, startNumber);
            track.InitSegment = new DashSegment{ Url = UrlUtils.ResolveUrl(baseUrl, initUrl) };
        }
        
        if (!string.IsNullOrEmpty(media)){
            var segmentTimeline = segmentTemplate.SelectSingleNode("dash:SegmentTimeline", nsManager);
            if (segmentTimeline != null){
                var sNodes = segmentTimeline.SelectNodes("dash:S", nsManager);
                if (sNodes != null){
                    int currentNumber = startNumber;
                    foreach (XmlNode s in sNodes){
                        var d = ParseInt(s.Attributes?["d"]?.Value);
                        var r = ParseInt(s.Attributes?["r"]?.Value);
                        var repeatCount = r + 1;
                        
                        for (int i = 0; i < repeatCount; i++){
                            var segUrl = ReplaceTemplateVariables(media, track.Id, currentNumber);
                            track.Segments.Add(new DashSegment{
                                Url = UrlUtils.ResolveUrl(baseUrl, segUrl),
                                Duration = d / (double)timescale
                            });
                            currentNumber++;
                        }
                    }
                }
            } else if (duration > 0){
                // Without timeline, can't determine segment count from manifest alone
            }
        }
    }
    
    private static void ParseSegmentBase(XmlNode segmentBase, DashTrack track, XmlNamespaceManager nsManager, string baseUrl){
        var initNode = segmentBase.SelectSingleNode("dash:Initialization", nsManager);
        if (initNode != null){
            var sourceUrl = initNode.Attributes?["sourceURL"]?.Value;
            if (!string.IsNullOrEmpty(sourceUrl)){
                track.InitSegment = new DashSegment{ Url = UrlUtils.ResolveUrl(baseUrl, sourceUrl) };
            }
        }
    }
    
    public async Task<bool> DownloadTrackAsync(DashTrack track, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default){
        try{
            _logger?.LogInformation("Starting DASH download: {Segments} segments to {Path}", track.Segments.Count, outputPath);
            
            // Download init segment first
            if (track.InitSegment != null){
                _logger?.LogInformation("Downloading init segment");
                var initData = await DownloadSegmentAsync(track.InitSegment, cancellationToken);
                if (initData != null){
                    await File.WriteAllBytesAsync(outputPath, initData, cancellationToken);
                }
            }
            
            // Download segments in parallel
            if (track.Segments.Count > 0){
                var segmentData = new byte[track.Segments.Count][];
                var completedCount = 0;
                var semaphore = new SemaphoreSlim(_threads);
                var tasks = new List<Task>();
                
                foreach (var index in Enumerable.Range(0, track.Segments.Count)){
                    var segment = track.Segments[index];
                    
                    tasks.Add(Task.Run(async () =>{
                        await semaphore.WaitAsync(cancellationToken);
                        try{
                            var data = await DownloadSegmentAsync(segment, cancellationToken);
                            segmentData[index] = data ?? Array.Empty<byte>();
                            
                            var completed = Interlocked.Increment(ref completedCount);
                            var percent = (double)completed / track.Segments.Count * 100;
                            progress?.Report(percent);
                            
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
            
            _logger?.LogInformation("Download complete: {Path}", outputPath);
            return true;
        } catch (Exception ex){
            _logger?.LogError(ex, "DASH download failed");
            return false;
        }
    }
    
    private async Task<byte[]?> DownloadSegmentAsync(DashSegment segment, CancellationToken cancellationToken){
        for (int attempt = 0; attempt <= _maxRetries; attempt++){
            try{
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(15000);
                
                var request = HttpClientWrapper.CreateRequest(segment.Url, HttpMethod.Get, false);
                
                if (segment.StartByte.HasValue && segment.EndByte.HasValue){
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(segment.StartByte.Value, segment.EndByte.Value);
                }
                
                _logger?.LogDebug("Downloading segment: {Url}", segment.Url);
                // Use raw HttpClient like original HLSDownloader does for segments
                var response = await _httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                
                if (!response.IsSuccessStatusCode){
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger?.LogWarning("Segment download failed: {Status} - {Body} - URL: {Url}", response.StatusCode, body.Substring(0, Math.Min(200, body.Length)), segment.Url);
                }
                
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            } catch (Exception ex) when (attempt < _maxRetries){
                _logger?.LogWarning("Segment download failed (attempt {Attempt}/{Max}): {Error} - URL: {Url}", attempt + 1, _maxRetries + 1, ex.Message, segment.Url);
                await Task.Delay(1000 * (attempt + 1), cancellationToken);
            }
        }
        
        return null;
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
    
    // Ported from CRD.Utils.Parser.Segments.SegmentTemplate - handles $Identifier$ and $Identifier%0Xd$ patterns
    private static readonly System.Text.RegularExpressions.Regex TemplatePattern = new(
        @"\$([A-Za-z]*)(?:(%0)([0-9]+)d)?\$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    
    private static string ReplaceTemplateVariables(string template, string representationId, int number){
        return TemplatePattern.Replace(template, match =>{
            if (match.Value == "$$") return "$"; // escape sequence
            
            var identifier = match.Groups[1].Value;
            var format = match.Groups[2].Value; // %0
            var widthStr = match.Groups[3].Value; // e.g. 4, 5
            
            string value;
            if (identifier == "RepresentationID"){
                value = representationId;
            } else if (identifier == "Number"){
                value = number.ToString();
            } else if (identifier == "Time"){
                value = number.ToString(); // simplified
            } else{
                return match.Value; // unknown identifier, keep as-is
            }
            
            // Handle zero-padding if format specified
            if (!string.IsNullOrEmpty(format) && int.TryParse(widthStr, out var width)){
                if (value.Length < width){
                    value = value.PadLeft(width, '0');
                }
            }
            
            return value;
        });
    }
}