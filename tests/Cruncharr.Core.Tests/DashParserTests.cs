using System.Xml;
using Cruncharr.Core.Utils;
using Xunit;

namespace Cruncharr.Core.Tests;

public class DashParserTests{
    private const string SampleDashManifest = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<MPD xmlns=""urn:mpeg:dash:schema:mpd:2011"" xmlns:cenc=""urn:mpeg:cenc:2013"" type=""static"" mediaPresentationDuration=""PT24M12.000S"" minBufferTime=""PT2S"" profiles=""urn:mpeg:dash:profile:isoff-on-demand:2011"">
  <Period duration=""PT24M12.000S"">
    <AdaptationSet id=""0"" contentType=""video"" mimeType=""video/mp4"" codecs=""avc1.640028"" width=""1920"" height=""1080"" frameRate=""24000/1001"" sar=""1:1"" startWithSAP=""1"" segmentAlignment=""true"">
      <ContentProtection schemeIdUri=""urn:mpeg:dash:mp4protection:2011"" value=""cenc"" cenc:default_KID=""12345678-1234-1234-1234-123456789012""/>
      <ContentProtection schemeIdUri=""urn:uuid:EDEF8BA9-79D6-4ACE-A3C8-27DCD51D21ED"">
        <cenc:pssh>AAAAZnBzc2gAAAAA7e+LqXnWSs6jyCfc1R0h7QAAACYiFnNhbXN1bmcta2V5b3MiR0VORVJJQ19TQU1TVU5HIgZzYW1zdW5nSOPclZsG</cenc:pssh>
      </ContentProtection>
      <Representation id=""video-1080p"" bandwidth=""8000000"" width=""1920"" height=""1080"">
        <BaseURL>https://example.com/video/</BaseURL>
        <SegmentBase indexRange=""0-1000"">
          <Initialization sourceURL=""init.mp4""/>
        </SegmentBase>
      </Representation>
      <Representation id=""video-720p"" bandwidth=""4000000"" width=""1280"" height=""720"">
        <BaseURL>https://example.com/video/</BaseURL>
        <SegmentBase indexRange=""0-1000"">
          <Initialization sourceURL=""init.mp4""/>
        </SegmentBase>
      </Representation>
    </AdaptationSet>
    <AdaptationSet id=""1"" contentType=""audio"" mimeType=""audio/mp4"" codecs=""mp4a.40.2"" audioSamplingRate=""48000"" lang=""ja-JP"" startWithSAP=""1"" segmentAlignment=""true"">
      <ContentProtection schemeIdUri=""urn:mpeg:dash:mp4protection:2011"" value=""cenc"" cenc:default_KID=""12345678-1234-1234-1234-123456789013""/>
      <ContentProtection schemeIdUri=""urn:uuid:EDEF8BA9-79D6-4ACE-A3C8-27DCD51D21ED"">
        <cenc:pssh>AAAAZnBzc2gAAAAA7e+LqXnWSs6jyCfc1R0h7QAAACYiFnNhbXN1bmcta2V5b3MiR0VORVJJQ19TQU1TVU5HIgZzYW1zdW5nSOPclZsG</cenc:pssh>
      </ContentProtection>
      <Representation id=""audio-ja"" bandwidth=""192000"">
        <BaseURL>https://example.com/audio/</BaseURL>
        <SegmentBase indexRange=""0-500"">
          <Initialization sourceURL=""init-audio.mp4""/>
        </SegmentBase>
      </Representation>
    </AdaptationSet>
    <AdaptationSet id=""2"" contentType=""audio"" mimeType=""audio/mp4"" codecs=""mp4a.40.2"" audioSamplingRate=""48000"" lang=""en-US"" startWithSAP=""1"" segmentAlignment=""true"">
      <Representation id=""audio-en"" bandwidth=""192000"">
        <BaseURL>https://example.com/audio/</BaseURL>
        <SegmentList duration=""4000"" timescale=""1000"">
          <Initialization sourceURL=""init-en.mp4""/>
          <SegmentURL media=""seg-en-1.m4s""/>
          <SegmentURL media=""seg-en-2.m4s""/>
          <SegmentURL media=""seg-en-3.m4s""/>
        </SegmentList>
      </Representation>
    </AdaptationSet>
  </Period>
</MPD>";

    private const string SegmentTemplateManifest = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<MPD xmlns=""urn:mpeg:dash:schema:mpd:2011"" type=""static"" mediaPresentationDuration=""PT24M12.000S"" minBufferTime=""PT2S"">
  <Period duration=""PT24M12.000S"">
    <AdaptationSet id=""0"" contentType=""video"" mimeType=""video/mp4"" codecs=""avc1.640028"" startWithSAP=""1"" segmentAlignment=""true"">
      <Representation id=""video-1080p"" bandwidth=""8000000"" width=""1920"" height=""1080"">
        <BaseURL>https://cdn.example.com/video/1080p/</BaseURL>
        <SegmentTemplate timescale=""1000"" duration=""4000"" startNumber=""1"" 
          initialization=""init-$RepresentationID$.mp4"" 
          media=""seg-$RepresentationID$-$Number%04d$.m4s"">
          <SegmentTimeline>
            <S t=""0"" d=""4000"" r=""2""/>
            <S d=""3500""/>
          </SegmentTimeline>
        </SegmentTemplate>
      </Representation>
    </AdaptationSet>
  </Period>
</MPD>";

    [Fact]
    public void ParseManifest_WithSegmentBase_ExtractsVideoAndAudioTracks(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.VideoTracks.Count);
        Assert.Equal(2, manifest.AudioTracks.Count);
    }

    [Fact]
    public void ParseManifest_VideoTrack_HasCorrectProperties(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        var video = manifest.VideoTracks.First(v => v.Id == "video-1080p");
        Assert.Equal("video", video.Type);
        Assert.Equal(8000000, video.Bandwidth);
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
        Assert.Equal("avc1.640028", video.Codecs);
        Assert.Equal("https://example.com/video/", video.BaseUrl);
        Assert.NotNull(video.InitSegment);
        Assert.Equal("https://example.com/video/init.mp4", video.InitSegment.Url);
    }

    [Fact]
    public void ParseManifest_AudioTrack_HasCorrectProperties(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        var audio = manifest.AudioTracks.First(a => a.Id == "audio-ja");
        Assert.Equal("audio", audio.Type);
        Assert.Equal(192000, audio.Bandwidth);
        Assert.Equal("ja-JP", audio.Language);
        Assert.Equal("https://example.com/audio/", audio.BaseUrl);
        Assert.NotNull(audio.InitSegment);
        Assert.Equal("https://example.com/audio/init-audio.mp4", audio.InitSegment.Url);
    }

    [Fact]
    public void ParseManifest_AudioTrack_WithSegmentList_HasSegments(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        var audio = manifest.AudioTracks.First(a => a.Id == "audio-en");
        Assert.Equal(3, audio.Segments.Count);
        Assert.Equal("https://example.com/audio/seg-en-1.m4s", audio.Segments[0].Url);
        Assert.Equal("https://example.com/audio/seg-en-2.m4s", audio.Segments[1].Url);
        Assert.Equal("https://example.com/audio/seg-en-3.m4s", audio.Segments[2].Url);
    }

    [Fact]
    public void ParseManifest_ExtractsPssh(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        var video = manifest.VideoTracks.First(v => v.Id == "video-1080p");
        Assert.NotNull(video.Pssh);
        Assert.NotEmpty(video.Pssh);
        
        var audio = manifest.AudioTracks.First(a => a.Id == "audio-ja");
        Assert.NotNull(audio.Pssh);
        Assert.NotEmpty(audio.Pssh);
    }

    [Fact]
    public void ParseManifest_SegmentTemplate_GeneratesCorrectSegmentUrls(){
        var manifest = DashSegmentDownloader.ParseManifest(SegmentTemplateManifest, "https://example.com/manifest.mpd");
        
        var video = manifest.VideoTracks.First();
        Assert.Equal("video-1080p", video.Id);
        Assert.NotNull(video.InitSegment);
        Assert.Equal("https://cdn.example.com/video/1080p/init-video-1080p.mp4", video.InitSegment.Url);
        
        // Should have 4 segments (3 with r=2 + 1 more)
        Assert.Equal(4, video.Segments.Count);
        Assert.Equal("https://cdn.example.com/video/1080p/seg-video-1080p-0001.m4s", video.Segments[0].Url);
        Assert.Equal("https://cdn.example.com/video/1080p/seg-video-1080p-0002.m4s", video.Segments[1].Url);
        Assert.Equal("https://cdn.example.com/video/1080p/seg-video-1080p-0003.m4s", video.Segments[2].Url);
        Assert.Equal("https://cdn.example.com/video/1080p/seg-video-1080p-0004.m4s", video.Segments[3].Url);
    }

    [Fact]
    public void ParseManifest_SelectsBestQuality(){
        var manifest = DashSegmentDownloader.ParseManifest(SampleDashManifest, "https://example.com/manifest.mpd");
        
        var bestVideo = manifest.VideoTracks.OrderByDescending(v => v.Bandwidth).FirstOrDefault();
        Assert.NotNull(bestVideo);
        Assert.Equal("video-1080p", bestVideo.Id);
        Assert.Equal(8000000, bestVideo.Bandwidth);
    }

    [Fact]
    public void ParseManifest_EmptyManifest_ThrowsException(){
        Assert.Throws<XmlException>(() => DashSegmentDownloader.ParseManifest("", "https://example.com/manifest.mpd"));
    }

    [Fact]
    public void ParseManifest_InvalidXml_ThrowsException(){
        Assert.Throws<XmlException>(() => DashSegmentDownloader.ParseManifest("not xml", "https://example.com/manifest.mpd"));
    }
}
