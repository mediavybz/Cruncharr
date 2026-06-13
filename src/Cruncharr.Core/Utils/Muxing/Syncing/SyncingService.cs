using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace Cruncharr.Core.Utils.Muxing.Syncing;

public interface ISyncingService
{
    Task<(bool IsOk, int ErrorCode, double frameRate)> ExtractFrames(string videoPath, string outputDir, double offset, double duration, string ffmpegPath, string? hwAccel = null);
    double ExtractFrameRate(string ffmpegOutput);
    (double ssim, double pixelDiff) ComputeSSIM(string imagePath1, string imagePath2, int targetWidth, int targetHeight);
    float[] GetPixelsArray(string imagePath, int targetWidth = 256, int targetHeight = 144);
    bool AreFramesSimilar(string imagePath1, string imagePath2, double ssimThreshold);
    bool AreFramesSimilarPreprocessed(float[] image1, float[] image2, double ssimThreshold);
    double CalculateOffset(List<FrameData> baseFrames, List<FrameData> compareFrames, bool reverseCompare = false, double ssimThreshold = 0.9);
}

public class SyncingService : ISyncingService
{
    private readonly ILogger<SyncingService>? _logger;

    public SyncingService(ILogger<SyncingService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<(bool IsOk, int ErrorCode, double frameRate)> ExtractFrames(string videoPath, string outputDir, double offset, double duration, string ffmpegPath, string? hwAccel = null)
    {
        // Optional hardware-accelerated decode for the sync frame extraction.
        var hw = !string.IsNullOrWhiteSpace(hwAccel) && !hwAccel.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? $"-hwaccel {hwAccel} "
            : "";
        var arguments =
            $"{hw}-ss {offset} -t {duration} -i \"{videoPath}\" -vf \"select='gt(scene,0.1)',showinfo\" -vsync vfr -frame_pts true \"{outputDir}/frame%05d.jpg\"";

        var output = "";

        try
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = ffmpegPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        output += e.Data;
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                bool isSuccess = process.ExitCode == 0;
                double frameRate = ExtractFrameRate(output);
                return (IsOk: isSuccess, ErrorCode: process.ExitCode, frameRate);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error extracting frames from {VideoPath}", videoPath);
            Console.Error.WriteLine($"An error occurred: {ex.Message}");
            return (IsOk: false, ErrorCode: -1, 0);
        }
    }

    public double ExtractFrameRate(string ffmpegOutput)
    {
        var match = Regex.Match(ffmpegOutput, @"Stream #0:0.*?(\d+(?:\.\d+)?) fps");
        if (match.Success)
        {
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        Console.Error.WriteLine("Failed to extract frame rate from FFmpeg output.");
        return 0;
    }

    private static double CalculateSSIM(float[] pixels1, float[] pixels2)
    {
        double mean1 = pixels1.Average();
        double mean2 = pixels2.Average();

        double var1 = 0, var2 = 0, covariance = 0;
        int count = pixels1.Length;

        for (int i = 0; i < count; i++)
        {
            var1 += (pixels1[i] - mean1) * (pixels1[i] - mean1);
            var2 += (pixels2[i] - mean2) * (pixels2[i] - mean2);
            covariance += (pixels1[i] - mean1) * (pixels2[i] - mean2);
        }

        var1 /= count - 1;
        var2 /= count - 1;
        covariance /= count - 1;

        double c1 = 0.01 * 0.01;
        double c2 = 0.03 * 0.03;

        double ssim = ((2 * mean1 * mean2 + c1) * (2 * covariance + c2)) /
                      ((mean1 * mean1 + mean2 * mean2 + c1) * (var1 + var2 + c2));

        return ssim;
    }

    private static float[] ExtractPixels(Image<Rgba32> image, int width, int height)
    {
        float[] pixels = new float[width * height];
        int index = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    pixels[index++] = row[x].R / 255f;
                }
            }
        });

        return pixels;
    }

    public (double ssim, double pixelDiff) ComputeSSIM(string imagePath1, string imagePath2, int targetWidth, int targetHeight)
    {
        using (var image1 = Image.Load<Rgba32>(imagePath1))
        using (var image2 = Image.Load<Rgba32>(imagePath2))
        {
            image1.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = ResizeMode.Max
            }).Grayscale());

            image2.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = ResizeMode.Max
            }).Grayscale());

            float[] pixels1 = ExtractPixels(image1, targetWidth, targetHeight);
            float[] pixels2 = ExtractPixels(image2, targetWidth, targetHeight);

            if (IsBlackFrame(pixels1) || IsBlackFrame(pixels2) ||
                IsMonochromaticFrame(pixels1) || IsMonochromaticFrame(pixels2))
            {
                return (-1.0, 99);
            }

            return (CalculateSSIM(pixels1, pixels2), CalculatePixelDifference(pixels1, pixels2));
        }
    }

    private static double CalculatePixelDifference(float[] pixels1, float[] pixels2)
    {
        double totalDifference = 0;
        int count = pixels1.Length;

        for (int i = 0; i < count; i++)
        {
            totalDifference += Math.Abs(pixels1[i] - pixels2[i]);
        }

        return totalDifference / count;
    }

    private static bool IsBlackFrame(float[] pixels, float threshold = 0.02f)
    {
        return pixels.All(p => p <= threshold);
    }

    private static bool IsMonochromaticFrame(float[] pixels, float stdDevThreshold = 0.05f)
    {
        float avg = pixels.Average();
        double variance = pixels.Average(p => Math.Pow(p - avg, 2));
        double stdDev = Math.Sqrt(variance);
        return stdDev < stdDevThreshold;
    }

    public bool AreFramesSimilar(string imagePath1, string imagePath2, double ssimThreshold)
    {
        var (ssim, pixelDiff) = ComputeSSIM(imagePath1, imagePath2, 256, 144);
        return ssim > ssimThreshold && pixelDiff < 0.04;
    }

    public float[] GetPixelsArray(string imagePath, int targetWidth = 256, int targetHeight = 144)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Max
        }).Grayscale());
        return ExtractPixels(image, targetWidth, targetHeight);
    }

    public bool AreFramesSimilarPreprocessed(float[] image1, float[] image2, double ssimThreshold)
    {
        if (IsBlackFrame(image1) || IsBlackFrame(image2) ||
            IsMonochromaticFrame(image1) || IsMonochromaticFrame(image2))
        {
            return false;
        }

        var pixelDiff = CalculatePixelDifference(image1, image2);

        if (pixelDiff > 0.04)
        {
            return false;
        }

        var ssim = CalculateSSIM(image1, image2);

        return ssim > ssimThreshold && pixelDiff < 0.04;
    }

    public double CalculateOffset(List<FrameData> baseFrames, List<FrameData> compareFrames, bool reverseCompare = false, double ssimThreshold = 0.9)
    {
        if (reverseCompare)
        {
            baseFrames.Reverse();
            compareFrames.Reverse();
        }

        var preprocessedCompareFrames = compareFrames.Select(f => new
        {
            Frame = f,
            Pixels = GetPixelsArray(f.FilePath)
        }).ToList();

        var delay = double.NaN;

        foreach (var baseFrame in baseFrames)
        {
            var baseFramePixels = GetPixelsArray(baseFrame.FilePath);
            var matchingFrame = preprocessedCompareFrames.AsParallel()
                .WithExecutionMode(ParallelExecutionMode.ForceParallelism).FirstOrDefault(f => AreFramesSimilarPreprocessed(baseFramePixels, f.Pixels, ssimThreshold));
            if (matchingFrame != null)
            {
                _logger?.LogDebug("Matched Frame - Base: {BasePath} Time: {BaseTime}, Compare: {ComparePath} Time: {CompareTime}",
                    baseFrame.FilePath, baseFrame.Time, matchingFrame.Frame.FilePath, matchingFrame.Frame.Time);
                delay = baseFrame.Time - matchingFrame.Frame.Time;
                break;
            }
            else
            {
                Debug.WriteLine($"No Match Found for Base Frame Time: {baseFrame.Time}");
            }
        }

        preprocessedCompareFrames.Clear();
        GC.Collect();

        return delay;
    }
}

public class FrameData
{
    public string FilePath { get; set; } = "";
    public double Time { get; set; }
}
