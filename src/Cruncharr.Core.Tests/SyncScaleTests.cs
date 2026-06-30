using Cruncharr.Core.Utils.Muxing.Syncing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Cruncharr.Core.Tests;

// Guard (upstream CRD v1.6.14: "Fixed syncing videos with different scales causing 'unable to
// sync' errors"). The SSIM frame-comparison resized with ResizeMode.Max (aspect-preserving), so
// frames from videos of different scale/aspect produced different actual pixel grids and the two
// pixel arrays misaligned -> bogus SSIM -> sync failed. Frames are now stretched to one fixed grid.
public class SyncScaleTests : IDisposable
{
    private readonly List<string> _files = new();

    private string MakeCheckerboard(int width, int height, int squares)
    {
        using var img = new Image<Rgba32>(width, height);
        int sw = Math.Max(1, width / squares);
        int sh = Math.Max(1, height / squares);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    bool on = ((x / sw) + (y / sh)) % 2 == 0;
                    row[x] = on ? new Rgba32(255, 255, 255) : new Rgba32(0, 0, 0);
                }
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.png");
        img.Save(path);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void ComputeSSIM_sameContentDifferentScale_matchesHigh()
    {
        var svc = new SyncingService();
        // Same checkerboard pattern at two very different resolutions (same aspect).
        var big = MakeCheckerboard(640, 360, 8);
        var small = MakeCheckerboard(160, 90, 8);

        var (ssim, _) = svc.ComputeSSIM(big, small, 256, 144);

        Assert.True(ssim > 0.8, $"expected high SSIM for same content at different scale, got {ssim}");
    }

    [Fact]
    public void ComputeSSIM_differentAspect_doesNotThrowAndReturnsFinite()
    {
        var svc = new SyncingService();
        // Different aspect ratios (16:9 vs 1:1) — the case that previously misaligned the arrays.
        var wide = MakeCheckerboard(320, 180, 8);
        var square = MakeCheckerboard(300, 300, 8);

        var (ssim, pixelDiff) = svc.ComputeSSIM(wide, square, 256, 144);

        Assert.True(!double.IsNaN(ssim) && ssim >= -1.0 && ssim <= 1.0, $"SSIM out of range: {ssim}");
        Assert.False(double.IsNaN(pixelDiff), "pixelDiff was NaN");
    }
}
