using System.Dynamic;
using System.Net;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.HLS;
using Cruncharr.Core.Utils.Parser;

namespace Cruncharr.Core.Tests;

// Live 2026-10-10 failure: the parsers emit Cruncharr.Core.Utils.ByteRange but the segment
// downloader declared its own HLS.ByteRange, so every ranged (SegmentBase/sidx) stream crashed with
// a RuntimeBinderException before a single byte was requested. These tests pass real parser output
// into the real downloader, the same handoff DownloadService.DownloadDashTracksAsync performs.
public class RangedSegmentDownloadTests : IDisposable
{
    private const string MediaUrl = "https://cdn.example.test/video.mp4";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-ranges-");

    // init (0-99) | sidx box (100-155) | part A (156-205) | part B (206-275)
    private static readonly byte[] Media = BuildMedia();

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SegmentBaseDash_DownloadsTheRangesTheParserProduced(bool newDownloadMethod)
    {
        var server = new RangeServer(Media);
        using var client = new HttpClient(server);
        const string mpd = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S" minBufferTime="PT2S" profiles="urn:mpeg:dash:profile:isoff-on-demand:2011">
              <Period id="0" start="PT0S">
                <AdaptationSet mimeType="video/mp4" contentType="video">
                  <Representation id="v1" bandwidth="1000000" codecs="avc1.640028" width="1920" height="1080">
                    <BaseURL>{MediaUrl}</BaseURL>
                    <SegmentBase indexRange="100-155"><Initialization range="0-99"/></SegmentBase>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4" contentType="audio" lang="ja-JP">
                  <Representation id="a1" bandwidth="128000" codecs="mp4a.40.2" audioSamplingRate="48000">
                    <BaseURL>https://cdn.example.test/audio.mp4</BaseURL>
                    <SegmentBase indexRange="100-155"><Initialization range="0-99"/></SegmentBase>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var parsed = await MpdParser.Parse(mpd, null, MediaUrl, client);
        var segments = parsed.Data.Values.SelectMany(d => d.video ?? []).Single().segments;
        var output = await Download(segments.Cast<dynamic>().ToList(), client, newDownloadMethod);

        Assert.Equal(["bytes=100-155", "bytes=0-99", "bytes=156-205", "bytes=206-275"],
            server.Requests.Where(r => r.Url == MediaUrl).Select(r => r.Range));
        Assert.Equal(Media[..100].Concat(Media[156..]).ToArray(), await File.ReadAllBytesAsync(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteRangeHls_DownloadsTheRangesTheParserProduced(bool newDownloadMethod)
    {
        var server = new RangeServer(Media);
        using var client = new HttpClient(server);
        const string playlist = $"""
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:2
            #EXT-X-MAP:URI="{MediaUrl}",BYTERANGE="100@0"
            #EXTINF:2.0,
            #EXT-X-BYTERANGE:50@156
            {MediaUrl}
            #EXTINF:2.0,
            #EXT-X-BYTERANGE:70@206
            {MediaUrl}
            #EXT-X-ENDLIST
            """;

        var parsed = M3u8MediaPlaylistParser.Parse(playlist, MediaUrl);
        var output = await Download((List<dynamic>)parsed.Segments, client, newDownloadMethod);

        Assert.Equal(["bytes=0-99", "bytes=156-205", "bytes=206-275"], server.Requests.Select(r => r.Range));
        Assert.Equal(Media[..100].Concat(Media[156..]).ToArray(), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public void From_AcceptsEveryShapeTheParsersHandOver()
    {
        var typed = new ByteRange { Offset = 1, Length = 2 };
        Assert.Same(typed, ByteRange.From(typed));
        Assert.Null(ByteRange.From(null));

        // MPD UrlType builds an anonymous { length, offset } object.
        AssertRange(ByteRange.From(new { length = 20L, offset = 10L }), 10, 20);

        dynamic expando = new ExpandoObject();
        expando.offset = 30L;
        expando.length = 40L;
        AssertRange(ByteRange.From((object)expando), 30, 40);

        Assert.Throws<InvalidDataException>(() => ByteRange.From("0-99"));
    }

    [Fact]
    public void ProgrammingErrors_AreNotRetriedLikeNetworkFailures()
    {
        Assert.True(Cruncharr.Core.Services.QueueService.IsDeterministicFailure(new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException("binder")));
        Assert.True(Cruncharr.Core.Services.QueueService.IsDeterministicFailure(new InvalidCastException()));
        Assert.False(Cruncharr.Core.Services.QueueService.IsDeterministicFailure(new HttpRequestException("offline")));
        Assert.False(Cruncharr.Core.Services.QueueService.IsDeterministicFailure(new IOException("disk")));
    }

    private async Task<string> Download(List<dynamic> segments, HttpClient client, bool newDownloadMethod)
    {
        var output = Path.Combine(_root.FullName, $"video-{newDownloadMethod}.m4s");
        var downloader = new HlsDownloader(new HlsOptions
        {
            Output = output,
            M3U8Json = new M3U8Json { Segments = segments },
            Threads = 1,
            Retries = 0,
            Timeout = 5000
        }, true, false, newDownloadMethod, client, new CruncharrConfig(), cancellationToken: TestContext.Current.CancellationToken);

        var (ok, _) = await downloader.Download();
        Assert.True(ok);
        return output;
    }

    private static void AssertRange(ByteRange? range, long offset, long length)
    {
        Assert.NotNull(range);
        Assert.Equal(offset, range.Offset);
        Assert.Equal(length, range.Length);
    }

    private static byte[] BuildMedia()
    {
        var media = new byte[276];
        for (var i = 0; i < 100; i++) media[i] = (byte)'I';
        for (var i = 156; i < 206; i++) media[i] = (byte)'A';
        for (var i = 206; i < 276; i++) media[i] = (byte)'B';

        // Version-0 sidx box: two references of 50 and 70 bytes starting right after the box.
        var sidx = new List<byte>();
        void U32(uint value) => sidx.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        U32(56); sidx.AddRange("sidx"u8.ToArray());
        U32(0);                 // version + flags
        U32(1); U32(1000);      // reference id, timescale
        U32(0); U32(0);         // earliest presentation time, first offset
        sidx.AddRange([0, 0, 0, 2]); // reserved, reference count
        U32(50); U32(2000); U32(0x90000000);
        U32(70); U32(2000); U32(0x90000000);
        sidx.CopyTo(media, 100);
        return media;
    }

    private sealed class RangeServer(byte[] media) : HttpMessageHandler
    {
        public List<(string Url, string Range)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range!.Ranges.Single();
            lock (Requests) Requests.Add((request.RequestUri!.AbsoluteUri, $"bytes={range.From}-{range.To}"));
            var slice = media[(int)range.From!.Value..((int)range.To!.Value + 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) });
        }
    }
}
