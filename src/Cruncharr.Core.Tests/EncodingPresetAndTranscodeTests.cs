using System.Linq;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class EncodingPresetAndTranscodeTests
{
    [Fact]
    public void BuiltInPresets_IncludeCrunchArrAv1Main10Source()
    {
        var svc = new EncodingService();
        var preset = svc.GetPreset("[CrunchArr] AV1 Main10 Source (SVT preset 8)");

        Assert.NotNull(preset);
        Assert.Equal("libsvtav1", preset!.Codec);
        Assert.Equal(24, preset.Crf);
        // Source-preserving: no scale/fps filter should be emitted for this preset.
        Assert.True(string.IsNullOrEmpty(preset.Resolution));
        Assert.True(string.IsNullOrEmpty(preset.FrameRate));
        Assert.Contains("-preset 8", preset.AdditionalParameters);
        Assert.Contains(preset.AdditionalParameters, p => p.Contains("svtav1-params") && p.Contains("lookahead=120"));
        // Metadata values with spaces must stay quoted so the arg splitter keeps them intact.
        Assert.Contains(preset.AdditionalParameters, p => p.Contains("encoding_tool=") && p.Contains("\"FFmpeg Nightly + SVT-AV1\""));
        Assert.Contains("-c:a copy", preset.AdditionalParameters);
    }

    [Fact]
    public void QueueConfig_DefaultsToSingleTranscode()
    {
        // Default: allow parallel downloads but serialize transcoding to one at a time.
        Assert.Equal(1, new QueueConfig().MaxSimultaneousTranscodes);
    }
}
