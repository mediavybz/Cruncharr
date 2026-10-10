using Cruncharr.API.Security;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils.Muxing.Syncing;
using Microsoft.AspNetCore.Http;

namespace Cruncharr.Core.Tests;

public class SecurityHardeningTests
{
    private static HttpRequest Request(string method, string path, string? fetchSite = null, string? origin = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (fetchSite != null) ctx.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin != null) ctx.Request.Headers.Origin = origin;
        return ctx.Request;
    }

    private static readonly IReadOnlySet<string> Allowed =
        RequestGuards.NormalizeOrigins(new[] { "http://localhost:8585", "https://dash.example/ " });

    [Theory]
    [InlineData("POST", "/api/v1/config/reset", "cross-site")]
    [InlineData("POST", "/api/v1/queue/pause", "same-site")]
    [InlineData("DELETE", "/api/v1/diagnostics/logs", "cross-site")]
    [InlineData("GET", "/api/v1/diagnostics/logs", "cross-site")]
    [InlineData("GET", "/api/v1/diagnostics/logs/errors", "same-site")]
    public void CrossSiteStateChanges_AreBlocked(string method, string path, string site)
    {
        Assert.True(RequestGuards.IsCrossSiteStateChange(Request(method, path, site, "https://evil.example"), Allowed));
    }

    [Theory]
    [InlineData("POST", "/api/v1/config/reset", "same-origin", null)]
    [InlineData("POST", "/api/v1/config/reset", "none", null)]
    [InlineData("POST", "/api/v1/config/reset", null, null)]              // curl / scripts
    [InlineData("GET", "/api/v1/config", "cross-site", "https://evil.example")]
    [InlineData("GET", "/api/v1/diagnostics/logs", "same-origin", null)]
    [InlineData("GET", "/api/v1/diagnostics/logs", null, null)]
    [InlineData("OPTIONS", "/api/v1/config", "cross-site", "https://evil.example")]
    [InlineData("POST", "/api/v1/config", "cross-site", "https://dash.example")] // CORS_ORIGINS entry
    [InlineData("POST", "/index.html", "cross-site", "https://evil.example")]   // not an API path
    public void LegitimateRequests_AreAllowed(string method, string path, string? site, string? origin)
    {
        Assert.False(RequestGuards.IsCrossSiteStateChange(Request(method, path, site, origin), Allowed));
    }

    [Fact]
    public void FailureThrottle_BlocksAfterLimitWithinWindowAndRecoversAfterIt()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new FailureThrottle(3, TimeSpan.FromMinutes(1), () => now);

        Assert.False(throttle.IsBlocked("10.0.0.5"));
        throttle.RecordFailure("10.0.0.5");
        throttle.RecordFailure("10.0.0.5");
        Assert.False(throttle.IsBlocked("10.0.0.5"));
        throttle.RecordFailure("10.0.0.5");
        Assert.True(throttle.IsBlocked("10.0.0.5"));
        Assert.False(throttle.IsBlocked("10.0.0.6"));

        now = now.AddSeconds(61);
        Assert.False(throttle.IsBlocked("10.0.0.5"));
        throttle.RecordFailure("10.0.0.5");
        Assert.False(throttle.IsBlocked("10.0.0.5"));
    }

    [Theory]
    [InlineData("/config/background.png", true)]
    [InlineData("/config/sub/dir/token.json", true)]
    [InlineData("/downloads/art/bg.jpg", true)]
    [InlineData("relative/background.png", true)]
    [InlineData("", true)]
    [InlineData("/etc/passwd.png", false)]
    [InlineData("/configs/other.png", false)]        // prefix of a root is not inside it
    [InlineData("/config/../etc/hostname.png", false)]
    public void ConfigPaths_AreConfinedToOwnedDirectories(string path, bool expected)
    {
        var roots = new[] { "/config", "/downloads", null, "" };
        Assert.Equal(expected, ConfigPathPolicy.IsWithinAllowedRoots(path, roots));
    }

    [Theory]
    [InlineData("Cruncharr/queue.json", "/config/Cruncharr/queue.json")]
    [InlineData("token.json", "/config/token.json")]
    [InlineData("/downloads/bg.png", "/downloads/bg.png")]
    [InlineData("", "")]
    public void RelativeConfigPaths_AreAnchoredUnderTheConfigDirectory(string input, string expected)
    {
        // Newly anchored paths use the host's drive and directory separators on Windows.
        if (input is "Cruncharr/queue.json" or "token.json") expected = Path.GetFullPath(expected);
        Assert.Equal(expected, ConfigPathPolicy.RootRelative(input, "/config"));
    }

    [Theory]
    [InlineData("cuda", true)]
    [InlineData("vaapi", true)]
    [InlineData("videotoolbox", true)]
    [InlineData("d3d11va", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("none", false)]
    [InlineData("cuda -i /config/cruncharr.yaml", false)]
    [InlineData("vaapi;rm", false)]
    public void SyncHwAccel_MustBeAnIdentifier(string? value, bool valid)
    {
        Assert.Equal(valid, SyncingService.IsValidHwAccel(value));
    }

    [Fact]
    public void ExtractFramesArguments_KeepPathsAsSingleArgumentsAndDropInvalidHwAccel()
    {
        var args = SyncingService.BuildExtractFramesArguments(
            "/tmp/my show \"quoted\".mkv", "/tmp/frames dir", 1.5, 120, "cuda -i /config/cruncharr.yaml");

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("/tmp/my show \"quoted\".mkv", args[args.IndexOf("-i") + 1]);
        Assert.Equal(Path.Combine("/tmp/frames dir", "frame%05d.jpg"), args[^1]);
    }

    [Fact]
    public void ExtractFramesArguments_IncludeValidHwAccel()
    {
        var args = SyncingService.BuildExtractFramesArguments("/v.mkv", "/out", 0, 10, "vaapi");
        Assert.Equal(new[] { "-hwaccel", "vaapi", "-ss" }, args.Take(3));
    }

    [Theory]
    [InlineData("mp4decrypt --key 0123456789abcdef0123456789abcdef:fedcba9876543210fedcba9876543210 in.enc out.mp4",
                "mp4decrypt --key 0123456789abcdef0123456789abcdef:[redacted] in.enc out.mp4")]
    [InlineData("--enable_raw_key_decryption --keys key_id=00112233445566778899aabbccddeeff:key=ffeeddccbbaa99887766554433221100",
                "--enable_raw_key_decryption --keys key_id=00112233445566778899aabbccddeeff:key=[redacted]")]
    [InlineData("ffprobe -v quiet -show_streams in.mp4", "ffprobe -v quiet -show_streams in.mp4")]
    public void CommandLines_HaveContentKeysRedacted(string input, string expected)
    {
        Assert.Equal(expected, DownloadService.RedactContentKeys(input));
    }
}
