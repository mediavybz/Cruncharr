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
        var preset = svc.GetPreset("[CrunchArr] AV1 Main10 Source (SVT preset 6)");

        Assert.NotNull(preset);
        Assert.Equal("libsvtav1", preset!.Codec);
        Assert.Equal(24, preset.Crf);
        // Source-preserving: no scale/fps filter should be emitted for this preset.
        Assert.True(string.IsNullOrEmpty(preset.Resolution));
        Assert.True(string.IsNullOrEmpty(preset.FrameRate));
        // User-tuned 2026-07-10: SVT preset 6 (slower, better compression) at CRF 24.
        Assert.Contains("-preset 6", preset.AdditionalParameters);
        Assert.Contains(preset.AdditionalParameters, p => p.Contains("svtav1-params") && p.Contains("lookahead=120"));
        // Metadata values with spaces must stay quoted so the arg splitter keeps them intact.
        Assert.Contains(preset.AdditionalParameters, p => p.Contains("encoding_tool=") && p.Contains("\"FFmpeg Nightly + SVT-AV1\""));
        Assert.Contains("-c:a copy", preset.AdditionalParameters);
    }

    [Fact]
    public void RenamedBuiltInPreset_OldNameStillResolves()
    {
        // cruncharr.yaml stores encoding_preset by NAME; configs written before the preset-6
        // rename must keep encoding. The legacy name aliases to the current preset.
        var svc = new EncodingService();
        var preset = svc.GetPreset("[CrunchArr] AV1 Main10 Source (SVT preset 8)");

        Assert.NotNull(preset);
        Assert.Equal("[CrunchArr] AV1 Main10 Source (SVT preset 6)", preset!.PresetName);
        Assert.Contains("-preset 6", preset.AdditionalParameters);
        Assert.True(svc.IsBuiltIn("[CrunchArr] AV1 Main10 Source (SVT preset 8)"));
    }

    [Fact]
    public void TrixPreset_UsesMainlineSafeSvtParams()
    {
        // The published Trix recipe targets the SVT-AV1-PSY fork. Our BtbN ffmpeg bundles
        // MAINLINE SVT-AV1, where photon-noise/min-keyint/enable-alt-cdef abort encoder init
        // ("Error parsing option") and enable-dlf must be <= 2 — one forbidden key kills every
        // encode using the preset. Verified empirically against the shipping image (v4.1.0).
        var svc = new EncodingService();
        var preset = svc.GetPreset("[Trix] Anime AV1 10-bit (unofficial)");

        Assert.NotNull(preset);
        Assert.Equal("libsvtav1", preset!.Codec);
        Assert.Equal(25, preset.Crf);
        Assert.Contains("-preset 2", preset.AdditionalParameters);
        var svtParams = preset.AdditionalParameters.FirstOrDefault(p => p.StartsWith("-svtav1-params"));
        Assert.NotNull(svtParams);
        // Mainline-safe substitutions present…
        Assert.Contains("film-grain=8", svtParams);
        Assert.Contains("keyint=193", svtParams);
        Assert.Contains("luminance-qp-bias=33", svtParams);
        Assert.Contains("enable-dlf=2", svtParams);
        // …and PSY-fork-only keys absent (each is fatal on mainline).
        Assert.DoesNotContain("photon-noise", svtParams);
        Assert.DoesNotContain("min-keyint", svtParams);
        Assert.DoesNotContain("enable-alt-cdef", svtParams);
        Assert.DoesNotContain("enable-dlf=3", svtParams);
    }

    [Fact]
    public void QueueConfig_DefaultsToSingleTranscode()
    {
        // Default: allow parallel downloads but serialize transcoding to one at a time.
        Assert.Equal(1, new QueueConfig().MaxSimultaneousTranscodes);
    }

    [Fact]
    public void FailedPresetPersistence_DoesNotActivatePresetInMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cruncharr-preset-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, "cruncharr.yaml");
        var presetsPath = Path.Combine(root, "encoding-presets");
        var previousConfigPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");

        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", configPath);
            var svc = new EncodingService();
            File.WriteAllText(presetsPath, "blocks directory creation");
            var preset = new VideoPreset
            {
                PresetName = "Must Not Activate",
                Codec = "libx264",
                Crf = 23
            };

            Assert.False(svc.AddPreset(preset));
            Assert.Null(svc.GetPreset(preset.PresetName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", previousConfigPath);
            if (File.Exists(presetsPath)) File.Delete(presetsPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(-1, true, false)]
    public void EncodedOutput_ReplacesMuxedSourceOnlyAfterSuccessfulFfmpeg(
        int exitCode,
        bool tempOutputExists,
        bool expected)
    {
        Assert.Equal(
            expected,
            DownloadService.ShouldReplaceEncodedOutput(exitCode, tempOutputExists));
    }

    [Theory]
    [InlineData("episode.mp4", "episode.encoding.mp4")]
    [InlineData("episode.mkv", "episode.encoding.mkv")]
    public void EncodingTempOutput_PreservesContainerExtension(string inputName, string expectedName)
    {
        var inputPath = Path.Combine("downloads", inputName);

        var tempPath = DownloadService.GetEncodingTempOutputPath(inputPath);

        Assert.Equal(Path.Combine("downloads", expectedName), tempPath);
    }
}
