using Cruncharr.Core.Utils.Muxing.Syncing;

namespace Cruncharr.Core.Tests;

// Guard (upstream CRD v1.6.14: "Fixed syncing videos with different scales causing 'unable to
// sync' errors"). The SSIM frame-comparison resized with ResizeMode.Max (aspect-preserving), so
// frames from videos of different scale/aspect produced different actual pixel grids and the two
// pixel arrays misaligned -> bogus SSIM -> sync failed. Frames are now stretched to one fixed grid.
// Frames are decoded through ffmpeg (the tool that wrote them), so these tests need it on PATH
// or in CRUNCHARR_TEST_FFMPEG and skip otherwise.
public class SyncScaleTests : IDisposable
{
    private readonly List<string> _files = new();

    internal static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("CRUNCHARR_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static SyncingService CreateService()
    {
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not available on this machine");
        return new SyncingService { FfmpegPath = ffmpeg! };
    }

    // Binary PGM (P5): a one-line header followed by 8-bit grayscale samples. ffmpeg reads it natively.
    private string MakeCheckerboard(int width, int height, int squares)
    {
        int sw = Math.Max(1, width / squares);
        int sh = Math.Max(1, height / squares);
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var data = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[y * width + x] = ((x / sw) + (y / sh)) % 2 == 0 ? (byte)255 : (byte)0;
        var path = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.pgm");
        using (var stream = File.Create(path))
        {
            stream.Write(header);
            stream.Write(data);
        }
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void DecodeArguments_StretchToGrayscaleGridAndReadOneFrame()
    {
        var args = SyncingService.BuildDecodeArguments("/tmp/frame 01.jpg", 256, 144);
        Assert.Equal("/tmp/frame 01.jpg", args[args.IndexOf("-i") + 1]);
        Assert.Equal("scale=256:144:flags=bilinear,format=gray", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("rawvideo", args[args.IndexOf("-f") + 1]);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public async Task ExtractFrames_WithInstalledFfmpeg_ProducesSceneFramesAndFrameRate()
    {
        var svc = CreateService();
        var directory = Directory.CreateTempSubdirectory("cruncharr-sync-extraction-");
        try
        {
            var video = Path.Combine(directory.FullName, "scene clip.mkv");
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(svc.FfmpegPath)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i",
                "nullsrc=s=160x90:r=25:d=2,geq=lum='if(mod(N,25),20,220)':cb=128:cr=128",
                "-c:v", "ffv1", video })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, errors);

            var result = await svc.ExtractFrames(video, directory.FullName, 0, 2, svc.FfmpegPath);

            Assert.True(result.IsOk, $"FFmpeg exited with {result.ErrorCode}");
            Assert.Equal(25, result.frameRate);
            Assert.NotEmpty(Directory.GetFiles(directory.FullName, "frame*.jpg"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPixelsArray_ReturnsOneNormalizedSamplePerTargetPixel()
    {
        var svc = CreateService();
        var pixels = svc.GetPixelsArray(MakeCheckerboard(64, 64, 2), 32, 32);
        Assert.Equal(32 * 32, pixels.Length);
        Assert.Equal(1f, pixels[0]);          // top-left square is white
        Assert.Equal(0f, pixels[31]);         // top-right square is black
        Assert.All(pixels, p => Assert.InRange(p, 0f, 1f));
    }

    [Fact]
    public void ComputeSSIM_sameContentDifferentScale_matchesHigh()
    {
        var svc = CreateService();
        // Same checkerboard pattern at two very different resolutions (same aspect).
        var big = MakeCheckerboard(640, 360, 8);
        var small = MakeCheckerboard(160, 90, 8);

        var (ssim, _) = svc.ComputeSSIM(big, small, 256, 144);

        Assert.True(ssim > 0.8, $"expected high SSIM for same content at different scale, got {ssim}");
    }

    [Fact]
    public void ComputeSSIM_differentAspect_doesNotThrowAndReturnsFinite()
    {
        var svc = CreateService();
        // Different aspect ratios (16:9 vs 1:1) — the case that previously misaligned the arrays.
        var wide = MakeCheckerboard(320, 180, 8);
        var square = MakeCheckerboard(300, 300, 8);

        var (ssim, pixelDiff) = svc.ComputeSSIM(wide, square, 256, 144);

        Assert.True(!double.IsNaN(ssim) && ssim >= -1.0 && ssim <= 1.0, $"SSIM out of range: {ssim}");
        Assert.False(double.IsNaN(pixelDiff), "pixelDiff was NaN");
    }

    [Fact]
    public void GetPixelsArray_MissingFileReportsFfmpegFailure()
    {
        var svc = CreateService();
        var ex = Assert.Throws<InvalidOperationException>(() => svc.GetPixelsArray(Path.Combine(Path.GetTempPath(), "missing-frame.jpg"), 16, 9));
        Assert.Contains("missing-frame.jpg", ex.Message);
    }
}
