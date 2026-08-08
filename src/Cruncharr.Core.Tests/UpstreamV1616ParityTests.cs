using System.Dynamic;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.DRM;
using Cruncharr.Core.Utils.Parser;
using Cruncharr.Core.Utils.Parser.Segments;
using Cruncharr.Core.Utils.Parser.Utils;
using Newtonsoft.Json;

namespace Cruncharr.Core.Tests;

public class UpstreamV1616ParityTests
{
    [Theory]
    [InlineData(9, "Example Season 3", "Example", "EXAMPLE|S9", 3)]
    [InlineData(4, "Example Specials", "Example", "EXAMPLE|S4", 0)]
    [InlineData(8, "Example", "Example", "EXAMPLE|S2", 2)]
    [InlineData(7, "Example", "Example", null, 7)]
    public void ResolveSeasonNumber_UsesSpecialTitleThenTitleIdentifierAndApi(
        int apiNumber,
        string seasonTitle,
        string seriesTitle,
        string? identifier,
        int expected)
    {
        Assert.Equal(expected, CrunchyrollApiService.ResolveSeasonNumber(
            apiNumber, seasonTitle, seriesTitle, identifier));
    }

    [Fact]
    public void CalendarEpisodeNumber_PreservesFractionalProviderLabel()
    {
        var metadata = new Cruncharr.Core.Services.CrBrowseEpisodeMetaData
        {
            Episode = "24.5",
            EpisodeCount = 24,
            SequenceNumber = 25
        };

        Assert.Equal("24.5", CalendarService.GetCalendarEpisodeNumber(metadata));
    }

    [Theory]
    [InlineData(12.5, 12, "12.5")]
    [InlineData(0, 1, "1")]
    [InlineData(0, 3, "")]
    public void CalendarEpisodeNumber_UsesUpstreamFallbacks(
        double sequenceNumber,
        int episodeCount,
        string expected)
    {
        var metadata = new Cruncharr.Core.Services.CrBrowseEpisodeMetaData
        {
            SequenceNumber = sequenceNumber,
            EpisodeCount = episodeCount
        };

        Assert.Equal(expected, CalendarService.GetCalendarEpisodeNumber(metadata));
    }

    [Fact]
    public void AudioDescriptionFiles_GetSeparateGroupOnlyWhenConfigured()
    {
        const string path = "episode_audio_en-US_ad.m4a";

        Assert.Equal("en-US.AD", DownloadService.GetSeparateAudioGroupKey(path, "en-US", true));
        Assert.Equal("en-US", DownloadService.GetSeparateAudioGroupKey(path, "en-US", false));
        Assert.Equal("en-US", DownloadService.RemoveAudioDescriptionGroupMarker("en-US.AD"));
    }

    [Fact]
    public void AudioDescriptionHistoryMarker_DoesNotCreateFalsePartialDownload()
    {
        var episode = new HistoryEpisode
        {
            WasDownloaded = true,
            DownloadedDubLang = ["en-US"],
            HistoryEpisodeAvailableDubLang = ["en-US*"]
        };

        Assert.False(episode.IsPartiallyDownloaded(["en-US*"], []));
        Assert.False(episode.HasAvailableMissingDownloadedMedia(["en-US*"], []));
    }

    [Fact]
    public void ContentKeys_ArePreferredOverSigningKeys()
    {
        var signing = new ContentKey { Type = "Signing", Bytes = [1] };
        var content = new ContentKey { Type = "Content", Bytes = [2] };

        Assert.Equal([content], DownloadService.GetContentKeys([signing, content]));
        Assert.Equal([signing], DownloadService.GetContentKeys([signing]));
    }

    [Fact]
    public void PsshEncoding_AcceptsByteArraysAndIndexedDictionaries()
    {
        Assert.Equal("AQID", MpdParser.EncodePssh(new byte[] { 1, 2, 3 }));
        Assert.Equal("AQID", MpdParser.EncodePssh(new Dictionary<string, object>
        {
            ["2"] = 3,
            ["0"] = 1,
            ["1"] = 2
        }));
    }

    [Fact]
    public void ParserMerge_IncludesAnonymousObjectMembers()
    {
        dynamic inherited = new ExpandoObject();
        inherited.timescale = 1000;

        var merged = (IDictionary<string, object>)Cruncharr.Core.Utils.Parser.Utils.ObjectUtilities.MergeExpandoObjects(
            inherited,
            new { duration = 5000, sourceURL = "init.mp4" });

        Assert.Equal(1000, merged["timescale"]);
        Assert.Equal(5000, merged["duration"]);
        Assert.Equal("init.mp4", merged["sourceURL"]);
    }

    [Fact]
    public void SegmentUrl_UsesLowercaseByteRangeAndSafeResolution()
    {
        dynamic input = new ExpandoObject();
        input.baseUrl = "https://cdn.example.test/video/manifest.mpd";
        input.source = "segment.m4s";
        input.range = "10-19";

        var segment = (IDictionary<string, object>)UrlType.UrlTypeToSegment(input);

        Assert.Equal("https://cdn.example.test/video/segment.m4s", segment["resolvedUri"]);
        Assert.True(segment.ContainsKey("byterange"));
        Assert.False(segment.ContainsKey("ByteRange"));
        Assert.Equal("relative.m4s", UrlUtils.ResolveUrl(string.Empty, "relative.m4s"));
    }

    [Fact]
    public void DurationParser_ReturnsFlatSegmentsAndTrimsTheLastDuration()
    {
        dynamic attributes = new ExpandoObject();
        attributes.type = "static";
        attributes.timescale = 1;
        attributes.duration = 5d;
        attributes.periodDuration = 12d;
        attributes.periodStart = 0d;
        attributes.startNumber = 1;

        var segments = DurationTimeParser.ParseByDuration(attributes);

        Assert.Equal(3, segments.Count);
        Assert.Equal(1, (int)segments[0].number);
        Assert.Equal(3, (int)segments[2].number);
        Assert.Equal(2d, (double)segments[2].duration);
    }

    [Fact]
    public void TimelineParser_ExpandsNegativeRepeatUntilTheNextTimelineEntry()
    {
        dynamic attributes = new ExpandoObject();
        attributes.type = "static";
        attributes.timescale = 1L;
        attributes.sourceDuration = 20d;
        attributes.periodStart = 0d;
        attributes.startNumber = 1;
        attributes.media = "segment-$Number$.m4s";

        dynamic first = new ExpandoObject();
        first.t = 0L;
        first.d = 5L;
        first.r = -1;
        dynamic second = new ExpandoObject();
        second.t = 15L;
        second.d = 5L;
        second.r = 0;

        List<dynamic> segments = TimelineTimeParser.ParseByTimeline(
            attributes,
            new List<dynamic> { first, second });

        Assert.Equal(4, segments.Count);
        Assert.Equal(new long[] { 0L, 5L, 10L, 15L }, segments.Select(segment => (long)segment.time));
    }

    [Fact]
    public async Task MpdParser_ParsesStaticSegmentTemplateEndToEnd()
    {
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT12S">
              <Period duration="PT12S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="video-1" bandwidth="1000000" width="1280" height="720"
                                  codecs="avc1.4d401f">
                    <BaseURL>https://cdn.example.test/video/</BaseURL>
                    <SegmentTemplate timescale="1" duration="5" startNumber="1"
                                     initialization="init.mp4" media="segment-$Number$.m4s" />
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en-US">
                  <Representation id="audio-1" bandwidth="128000" codecs="mp4a.40.2"
                                  audioSamplingRate="48000">
                    <BaseURL>https://cdn.example.test/audio/</BaseURL>
                    <SegmentTemplate timescale="1" duration="5" startNumber="1"
                                     initialization="init.mp4" media="segment-$Number$.m4s" />
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var parsed = await MpdParser.Parse(manifest, null, null, new HttpClient());

        var server = Assert.Single(parsed.Data).Value;
        var playlist = Assert.Single(server.video!);
        Assert.Equal(3, playlist.segments.Count);
        Assert.Equal("https://cdn.example.test/video/segment-1.m4s", playlist.segments[0].uri);
        Assert.Equal(2d, playlist.segments[2].duration);
    }

    [Fact]
    public void BrowseMetadata_AcceptsNullUpstreamAvailabilityDates()
    {
        const string json = """{"available_date":null,"premium_date":null}""";

        var metadata = JsonConvert.DeserializeObject<Cruncharr.Core.Services.CrBrowseEpisodeMetaData>(json);

        Assert.NotNull(metadata);
        Assert.Null(metadata.AvailableDate);
        Assert.Null(metadata.PremiumDate);
    }
}
