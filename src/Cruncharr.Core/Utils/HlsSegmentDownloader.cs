using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Utils;

public class HlsSegmentDownloader{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, byte[]> _keys = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxRetries;
    private readonly int _timeoutMs;
    
    public HlsSegmentDownloader(HttpClient httpClient, int threads = 5, int maxRetries = 3, int timeoutMs = 15000, ILogger? logger = null){
        _httpClient = httpClient;
        _logger = logger;
        _semaphore = new SemaphoreSlim(threads);
        _maxRetries = maxRetries;
        _timeoutMs = timeoutMs;
    }
    
    public async Task<bool> DownloadAsync(string playlistUrl, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default){
        try{
            _logger?.LogInformation("Downloading HLS playlist: {Url}", playlistUrl);
            
            // Download and parse playlist
            var playlist = await DownloadPlaylistAsync(playlistUrl, cancellationToken);
            if (playlist == null || playlist.Segments.Count == 0){
                _logger?.LogError("Empty or invalid playlist");
                return false;
            }
            
            _logger?.LogInformation("Found {Count} segments", playlist.Segments.Count);
            
            // Download init segment if present
            if (playlist.MapSegment != null){
                _logger?.LogInformation("Downloading init segment");
                var initData = await DownloadSegmentAsync(playlist.MapSegment, playlist.BaseUrl, cancellationToken);
                if (initData != null){
                    await File.WriteAllBytesAsync(outputPath, initData, cancellationToken);
                }
            }
            
            // Download segments in parallel with throttling
            var segmentData = new byte[playlist.Segments.Count][];
            var completedCount = 0;
            var tasks = new List<Task>();
            
            for (int i = 0; i < playlist.Segments.Count; i++){
                var index = i;
                var segment = playlist.Segments[i];
                
                tasks.Add(Task.Run(async () =>{
                    await _semaphore.WaitAsync(cancellationToken);
                    try{
                        var data = await DownloadSegmentAsync(segment, playlist.BaseUrl, cancellationToken);
                        segmentData[index] = data ?? Array.Empty<byte>();
                        
                        var completed = Interlocked.Increment(ref completedCount);
                        var percent = (double)completed / playlist.Segments.Count * 100;
                        progress?.Report(percent);
                        
                        if (completed % 10 == 0 || completed == playlist.Segments.Count){
                            _logger?.LogInformation("Downloaded {Completed}/{Total} segments ({Percent:F1}%)", completed, playlist.Segments.Count, percent);
                        }
                    } finally{
                        _semaphore.Release();
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
            
            _logger?.LogInformation("Download complete: {Path}", outputPath);
            return true;
        } catch (Exception ex){
            _logger?.LogError(ex, "HLS download failed");
            return false;
        }
    }
    
    private async Task<HlsPlaylist?> DownloadPlaylistAsync(string url, CancellationToken cancellationToken){
        try{
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeoutMs);
            
            var response = await _httpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParsePlaylist(content, url);
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to download playlist");
            return null;
        }
    }
    
    private HlsPlaylist ParsePlaylist(string content, string baseUrl){
        var playlist = new HlsPlaylist{ BaseUrl = GetBaseUrl(baseUrl) };
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        HlsSegment? currentSegment = null;
        HlsKey? currentKey = null;
        
        foreach (var line in lines){
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#EXTM3U")) continue;
            
            if (trimmed.StartsWith("#EXT-X-MAP")){
                // Init segment
                var uriMatch = Regex.Match(trimmed, "URI=\"([^\"]+)\"");
                if (uriMatch.Success){
                    playlist.MapSegment = new HlsSegment{
                        Uri = ResolveUrl(uriMatch.Groups[1].Value, playlist.BaseUrl)
                    };
                }
            } else if (trimmed.StartsWith("#EXT-X-KEY")){
                // Encryption key
                var methodMatch = Regex.Match(trimmed, "METHOD=([^,]+)");
                var uriMatch = Regex.Match(trimmed, "URI=\"([^\"]+)\"");
                var ivMatch = Regex.Match(trimmed, "IV=0x([0-9A-Fa-f]+)");
                
                if (methodMatch.Success && uriMatch.Success){
                    currentKey = new HlsKey{
                        Method = methodMatch.Groups[1].Value.Trim(),
                        Uri = ResolveUrl(uriMatch.Groups[1].Value, playlist.BaseUrl),
                        Iv = ivMatch.Success ? ParseIv(ivMatch.Groups[1].Value) : null
                    };
                }
            } else if (trimmed.StartsWith("#EXTINF")){
                // Segment info line
                currentSegment = new HlsSegment();
                if (currentKey != null){
                    currentSegment.Key = currentKey;
                }
            } else if (!trimmed.StartsWith("#") && currentSegment != null){
                // Segment URL
                currentSegment.Uri = ResolveUrl(trimmed, playlist.BaseUrl);
                playlist.Segments.Add(currentSegment);
                currentSegment = null;
            }
        }
        
        return playlist;
    }
    
    private async Task<byte[]?> DownloadSegmentAsync(HlsSegment segment, string baseUrl, CancellationToken cancellationToken){
        var uri = segment.Uri ?? "";
        
        for (int attempt = 0; attempt <= _maxRetries; attempt++){
            try{
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeoutMs);
                
                var response = await _httpClient.GetAsync(uri, cts.Token);
                response.EnsureSuccessStatusCode();
                
                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                
                // Decrypt if key is present
                if (segment.Key != null){
                    data = await DecryptSegmentAsync(data, segment.Key, cancellationToken);
                }
                
                return data;
            } catch (Exception ex) when (attempt < _maxRetries){
                _logger?.LogWarning("Segment download failed (attempt {Attempt}/{Max}): {Error}", attempt + 1, _maxRetries + 1, ex.Message);
                await Task.Delay(1000 * (attempt + 1), cancellationToken);
            }
        }
        
        return null;
    }
    
    private async Task<byte[]> DecryptSegmentAsync(byte[] data, HlsKey key, CancellationToken cancellationToken){
        if (key.Method != "AES-128"){
            _logger?.LogWarning("Unsupported encryption method: {Method}", key.Method);
            return data;
        }
        
        // Download key if not cached
        if (!_keys.ContainsKey(key.Uri)){
            var keyData = await _httpClient.GetByteArrayAsync(key.Uri, cancellationToken);
            if (keyData.Length != 16){
                throw new Exception($"Invalid key size: {keyData.Length} bytes (expected 16)");
            }
            _keys[key.Uri] = keyData;
        }
        
        var keyBytes = _keys[key.Uri];
        var iv = key.Iv ?? GenerateIvFromSegment(0);
        
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }
    
    private static byte[] ParseIv(string hex){
        var bytes = new byte[16];
        for (int i = 0; i < 16 && i * 2 < hex.Length; i++){
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
    
    private static byte[] GenerateIvFromSegment(int segmentIndex){
        var iv = new byte[16];
        var bytes = BitConverter.GetBytes(segmentIndex + 1);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        bytes.CopyTo(iv, 12);
        return iv;
    }
    
    private static string GetBaseUrl(string url){
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash > 0){
            path = path.Substring(0, lastSlash + 1);
        }
        return $"{uri.Scheme}://{uri.Host}{path}";
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
}

public class HlsPlaylist{
    public string BaseUrl { get; set; } = "";
    public List<HlsSegment> Segments { get; set; } = new();
    public HlsSegment? MapSegment { get; set; }
}

public class HlsSegment{
    public string? Uri { get; set; }
    public HlsKey? Key { get; set; }
}

public class HlsKey{
    public string Method { get; set; } = "";
    public string Uri { get; set; } = "";
    public byte[]? Iv { get; set; }
}