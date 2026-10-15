using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.DRM;
using Moq;

namespace Cruncharr.Core.Tests;

// Live log, 2026-10-15 (Tomb Raider King): every download logged
// [PLAYBACK ERROR BODY] {"error":40016,"reason":"Outdated Token"} from web/firefox, and some
// tracks logged "Failed to get Keys! ... 401 (Unauthorized)., Response: {"error":"Forbidden"}"
// then "No decryption keys obtained for audio, may be unplayable".
// - 40016: the play service rejects a /play URL for a device other than the one whose client
//   minted the token. Cruncharr only has the primary client's token, so the web/firefox
//   fallback (and any secondary device) could never succeed.
// - 401: DASH tracks requested their license AFTER downloading segments, with the token read
//   at /play time. Tokens live 300 s and are only renewed within 60 s of expiry.
// - Still-encrypted tracks were muxed anyway and the item was reported Complete.
public class PlaybackDecryptionTests
{
    [Fact]
    public void PlaybackRequestsOnlyThePrimaryClientsDevice()
    {
        var primary = new CrAuthSettings { Endpoint = "tv/android_tv", UserAgent = "ANDROIDTV/3.75.0_22365 Android/16", Video = true, Audio = true };

        var endpoints = DownloadService.BuildPlaybackEndpoints("GE00378913ENUS", primary);

        var (endpoint, userAgent) = Assert.Single(endpoints);
        Assert.Equal($"{ApiUrls.Playback}/GE00378913ENUS/tv/android_tv/play", endpoint);
        Assert.Equal(primary.UserAgent, userAgent);
        Assert.DoesNotContain(endpoints, e => e.Endpoint.Contains("web/firefox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LicenseHeadersUseATokenRefreshedAtRequestTime()
    {
        var token = new CrToken { access_token = "token-from-play-time" };
        var auth = new Mock<ICrunchyrollAuthService>();
        using var transport = new HttpClientWrapper();
        auth.SetupGet(a => a.HttpClient).Returns(transport);
        auth.SetupGet(a => a.Token).Returns(() => token);
        auth.Setup(a => a.RefreshTokenAsync(true, It.IsAny<CancellationToken>(), false))
            .Callback(() => token = new CrToken { access_token = "renewed-token" })
            .ReturnsAsync(true);
        var download = new DownloadService(auth.Object, Mock.Of<ICrunchyrollApiService>());

        var headers = await download.CreateLicenseAuthDataAsync("GE00378913ENUS", "video-token", TestContext.Current.CancellationToken);

        Assert.Equal("Bearer renewed-token", headers["authorization"]);
        Assert.Equal("GE00378913ENUS", headers["x-cr-content-id"]);
        Assert.Equal("video-token", headers["x-cr-video-token"]);
    }

    [Fact]
    public async Task TrackKeysReuseKnownKeysAndRequestEachOtherPsshOnce()
    {
        var videoKeys = new List<ContentKey> { Key(1) };
        var audioKeys = new List<ContentKey> { Key(2) };
        var requested = new List<string>();

        var keys = await DownloadService.ResolveTrackKeysAsync(
            ["video-pssh", "audio-pssh", null, "audio-pssh", ""],
            new Dictionary<string, List<ContentKey>> { ["video-pssh"] = videoKeys },
            pssh =>
            {
                requested.Add(pssh);
                return Task.FromResult(audioKeys);
            });

        Assert.Equal(["audio-pssh"], requested);
        Assert.Same(videoKeys, keys["video-pssh"]);
        Assert.Same(audioKeys, keys["audio-pssh"]);
    }

    [Fact]
    public async Task EmptyKnownKeysAreRequestedAgain()
    {
        var requested = new List<string>();

        await DownloadService.ResolveTrackKeysAsync(
            ["video-pssh"],
            new Dictionary<string, List<ContentKey>> { ["video-pssh"] = [] },
            pssh =>
            {
                requested.Add(pssh);
                return Task.FromResult(new List<ContentKey> { Key(1) });
            });

        Assert.Equal(["video-pssh"], requested);
    }

    [Fact]
    public void StillEncryptedPrimaryTrackFailsTheDownload()
    {
        var files = new List<string> { "/tmp/x/video.m4s", "/tmp/x/audio_enus.enc.m4s" };
        var audio = new List<(string Path, string Lang)> { ("/tmp/x/audio_enus.enc.m4s", "en-US") };

        var error = Assert.Throws<DownloadException>(() =>
            DownloadService.DropUndecryptedTracks(files, audio, new HashSet<string>(files)));

        Assert.Contains("audio_enus.enc.m4s", error.Message);
        Assert.Equal(DownloadErrorType.Unknown, error.ErrorType);
    }

    [Fact]
    public void StillEncryptedExtraTrackIsDroppedNotMuxed()
    {
        var files = new List<string> { "/tmp/x/video.m4s", "/tmp/x/audio_enus.m4s", "/tmp/x/audio_kokr.enc.m4s" };
        var audio = new List<(string Path, string Lang)>
        {
            ("/tmp/x/audio_enus.m4s", "en-US"),
            ("/tmp/x/audio_kokr.enc.m4s", "ko-KR")
        };
        var required = new HashSet<string> { "/tmp/x/video.m4s", "/tmp/x/audio_enus.m4s" };

        var dropped = DownloadService.DropUndecryptedTracks(files, audio, required);

        Assert.Equal(["/tmp/x/audio_kokr.enc.m4s"], dropped);
        Assert.Equal(["/tmp/x/video.m4s", "/tmp/x/audio_enus.m4s"], files);
        Assert.Equal([("/tmp/x/audio_enus.m4s", "en-US")], audio);
    }

    private static ContentKey Key(byte id) => new() { KeyID = [id], Bytes = [id], Type = "CONTENT" };
}
