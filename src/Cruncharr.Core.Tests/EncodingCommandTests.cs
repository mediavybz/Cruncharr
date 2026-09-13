using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

public class EncodingCommandTests
{
    [Fact]
    public void EveryBuiltInProducesValidArgumentsAndMapsAllStreamsOnlyOnce()
    {
        foreach (var preset in new EncodingService().GetPresets().Where(p => new EncodingService().IsBuiltIn(p.PresetName!)))
        {
            var args = EncodingCommand.Build(preset, "source.mkv", "output.mkv");
            Assert.Equal(1, args.Count(a => a == "-map"));
            Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
            Assert.DoesNotContain("-map 0", args);
        }
    }

    [Theory]
    [InlineData("av1_nvenc", "-cq", 60)]
    [InlineData("av1_qsv", "-global_quality", 30)]
    [InlineData("hevc_vaapi", "-qp", 24)]
    [InlineData("libsvtav1", "-crf", 60)]
    public void EncoderSpecificQualityFlagsAreUsed(string codec, string flag, int quality)
    {
        var args = EncodingCommand.Build(new VideoPreset { PresetName = "Test", Codec = codec, Crf = quality }, "in.mkv", "out.mkv");
        Assert.Contains(flag, args);
        if (flag != "-crf") Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-vf", args); // blank resolution/fps preserves the source
    }

    [Fact]
    public void PreviewAndEncodingHonorQuotesAndDefaultQuality()
    {
        var args = EncodingCommand.Build(new VideoPreset
        {
            PresetName = "Test", Codec = "libx264", Crf = -1,
            AdditionalParameters = ["-metadata title='Two words'", "-metadata comment=\"Other words\""]
        }, "in.mkv", "out.mkv");
        Assert.Contains("title=Two words", args);
        Assert.Contains("comment=Other words", args);
        Assert.DoesNotContain("-crf", args);
        Assert.Throws<ArgumentException>(() => EncodingCommand.SplitArguments("-metadata title='broken"));
    }

    [Fact]
    public void FileNameCollisionsDoNotOverwritePresetsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "preset-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new EncodingService(presetsDirectory: root);
            Assert.True(service.AddPreset(new VideoPreset { PresetName = "a/b", Codec = "libx264", Crf = 21 }));
            Assert.True(service.AddPreset(new VideoPreset { PresetName = "a_b", Codec = "libx264", Crf = 23 }));
            service = new EncodingService(presetsDirectory: root);
            Assert.Equal(21, service.GetPreset("a/b")!.Crf);
            Assert.Equal(23, service.GetPreset("a_b")!.Crf);
            Assert.True(service.RemovePreset("a/b"));
            Assert.Equal(23, new EncodingService(presetsDirectory: root).GetPreset("a_b")!.Crf);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
