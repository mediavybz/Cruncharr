using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

/// <summary>
/// Caching proxy for Crunchyroll catalog images. Crunchyroll image URLs are content-addressed
/// (the path contains a hash of the asset, e.g. .../catalog/crunchyroll/&lt;hash&gt;.jpg), so a new
/// image always means a new URL. That lets us cache aggressively by URL: a given URL is fetched
/// from Crunchyroll exactly once, stored on disk, and served thereafter with an immutable cache
/// header so the browser caches it too. When Crunchyroll updates an image the URL changes and the
/// new one is fetched on first use — exactly "only pull a new image when it changed upstream".
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly ILogger<ImagesController> _logger;

    // One shared client; CR image CDN is happy with default settings.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // Persistent cache dir (lives on the /config volume so it survives restarts; not the temp dir,
    // which a user may point at a RAM disk for transcoding).
    private static readonly string _cacheDir = ResolveCacheDir();

    private const long MaxImageBytes = 25 * 1024 * 1024; // guardrail against pathological responses

    public ImagesController(ILogger<ImagesController> logger)
    {
        _logger = logger;
    }

    private static string ResolveCacheDir()
    {
        var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
        var baseDir = Path.GetDirectoryName(configPath);
        var dir = string.IsNullOrEmpty(baseDir)
            ? Path.Combine(Path.GetTempPath(), "cruncharr-imgcache")
            : Path.Combine(baseDir, "imgcache");
        try { Directory.CreateDirectory(dir); } catch { /* created lazily on first write otherwise */ }
        return dir;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return BadRequest(new { Error = "A valid absolute image url is required" });
        }

        // SSRF guard: only proxy Crunchyroll image hosts over https.
        if (!IsAllowedImageHost(uri))
        {
            return BadRequest(new { Error = "Only Crunchyroll image URLs are allowed" });
        }

        var ext = NormalizeExtension(uri.AbsolutePath);
        var contentType = ContentTypeForExtension(ext);
        var hash = Sha256Hex(url);
        var cachePath = Path.Combine(_cacheDir, hash + ext);

        // Cache hit: serve from disk.
        if (System.IO.File.Exists(cachePath))
        {
            SetCacheHeaders(hash);
            return PhysicalFile(cachePath, contentType);
        }

        // Cache miss: fetch from Crunchyroll once, persist, then serve.
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Image fetch {Url} returned {Status}", url, (int)resp.StatusCode);
                return StatusCode(502, new { Error = "Failed to fetch image" });
            }

            if (resp.Content.Headers.ContentLength is long len && len > MaxImageBytes)
            {
                return StatusCode(502, new { Error = "Image too large" });
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return StatusCode(502, new { Error = "Image empty or too large" });
            }

            // Prefer the upstream content-type when present and sane.
            if (resp.Content.Headers.ContentType?.MediaType is string upstreamType
                && upstreamType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                contentType = upstreamType;
            }

            // Write atomically: temp file then move, so a cache hit never sees a partial file
            // (matters when two requests race for the same uncached image).
            try
            {
                Directory.CreateDirectory(_cacheDir);
                var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await System.IO.File.WriteAllBytesAsync(tmp, bytes, cancellationToken);
                System.IO.File.Move(tmp, cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // If caching fails (disk full / perms) still serve the bytes this time.
                _logger.LogWarning(ex, "Failed to cache image {Url}", url);
            }

            SetCacheHeaders(hash);
            return File(bytes, contentType);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { Error = "Request cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image proxy error for {Url}", url);
            return StatusCode(502, new { Error = "Failed to fetch image" });
        }
    }

    private void SetCacheHeaders(string etag)
    {
        // Content-addressed URL -> content never changes -> cache for a year, immutable.
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        Response.Headers["ETag"] = "\"" + etag + "\"";
    }

    private static bool IsAllowedImageHost(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "crunchyroll.com" || host.EndsWith(".crunchyroll.com", StringComparison.Ordinal);
    }

    private static string NormalizeExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" => ".jpg",
            ".png" => ".png",
            ".webp" => ".webp",
            ".gif" => ".gif",
            ".avif" => ".avif",
            _ => ".jpg"
        };
    }

    private static string ContentTypeForExtension(string ext) => ext switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        _ => "image/jpeg"
    };

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
