using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Utils.Muxing.Syncing;

public interface IVideoSyncer
{
    Task<(double offSet, double startOffset, double endOffset, double lengthDiff)> ProcessVideo(string baseVideoPath, string compareVideoPath, string tempDir, string ffmpegPath, string? hwAccel = null);
}

public class VideoSyncer : IVideoSyncer
{
    private readonly ISyncingService _syncingService;
    private readonly ILogger<VideoSyncer>? _logger;

    public VideoSyncer(ISyncingService syncingService, ILogger<VideoSyncer>? logger = null)
    {
        _syncingService = syncingService;
        _logger = logger;
    }

    public async Task<(double offSet, double startOffset, double endOffset, double lengthDiff)> ProcessVideo(string baseVideoPath, string compareVideoPath, string tempDir, string ffmpegPath, string? hwAccel = null)
    {
        string baseFramesDir, baseFramesDirEnd;
        string compareFramesDir, compareFramesDirEnd;
        string cleanupDir = string.Empty;
        double baseEndWindowOffset = 0;
        double compareEndWindowOffset = 0;
        try
        {
            string uuid = Guid.NewGuid().ToString();

            cleanupDir = Path.Combine(tempDir, uuid);
            baseFramesDir = Path.Combine(tempDir, uuid, "base_frames_start");
            baseFramesDirEnd = Path.Combine(tempDir, uuid, "base_frames_end");
            compareFramesDir = Path.Combine(tempDir, uuid, "compare_frames_start");
            compareFramesDirEnd = Path.Combine(tempDir, uuid, "compare_frames_end");

            Directory.CreateDirectory(baseFramesDir);
            Directory.CreateDirectory(baseFramesDirEnd);
            Directory.CreateDirectory(compareFramesDir);
            Directory.CreateDirectory(compareFramesDirEnd);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to create sync directories");
            Console.Error.WriteLine(e);
            return (-100, 0, 0, 0);
        }

        try
        {
            var extractFramesBaseStart = await _syncingService.ExtractFrames(baseVideoPath, baseFramesDir, 0, 120, ffmpegPath, hwAccel);
            var extractFramesCompareStart = await _syncingService.ExtractFrames(compareVideoPath, compareFramesDir, 0, 120, ffmpegPath, hwAccel);

            TimeSpan? baseVideoDurationTimeSpan = await GetMediaDurationAsync(ffmpegPath, baseVideoPath);
            TimeSpan? compareVideoDurationTimeSpan = await GetMediaDurationAsync(ffmpegPath, compareVideoPath);

            if (baseVideoDurationTimeSpan == null || compareVideoDurationTimeSpan == null)
            {
                Console.Error.WriteLine("Failed to retrieve video durations");
                return (-100, 0, 0, 0);
            }

            var baseEndWindowDuration = Math.Min(360, baseVideoDurationTimeSpan.Value.TotalSeconds);
            var compareEndWindowDuration = Math.Min(360, compareVideoDurationTimeSpan.Value.TotalSeconds);
            baseEndWindowOffset = Math.Max(0, baseVideoDurationTimeSpan.Value.TotalSeconds - baseEndWindowDuration);
            compareEndWindowOffset = Math.Max(0, compareVideoDurationTimeSpan.Value.TotalSeconds - compareEndWindowDuration);

            var extractFramesBaseEnd = await _syncingService.ExtractFrames(baseVideoPath, baseFramesDirEnd, baseEndWindowOffset, baseEndWindowDuration, ffmpegPath, hwAccel);
            var extractFramesCompareEnd = await _syncingService.ExtractFrames(compareVideoPath, compareFramesDirEnd, compareEndWindowOffset, compareEndWindowDuration, ffmpegPath, hwAccel);

            if (!extractFramesBaseStart.IsOk || !extractFramesCompareStart.IsOk || !extractFramesBaseEnd.IsOk || !extractFramesCompareEnd.IsOk)
            {
                Console.Error.WriteLine("Failed to extract Frames to Compare");
                return (-100, 0, 0, 0);
            }

            var baseFramesStart = Directory.GetFiles(baseFramesDir).Select(fp => new FrameData
            {
                FilePath = fp,
                Time = GetTimeFromFileName(fp, extractFramesBaseStart.frameRate, 0)
            }).OrderBy(frame => frame.Time).ToList();

            var compareFramesStart = Directory.GetFiles(compareFramesDir).Select(fp => new FrameData
            {
                FilePath = fp,
                Time = GetTimeFromFileName(fp, extractFramesCompareStart.frameRate, 0)
            }).OrderBy(frame => frame.Time).ToList();

            var baseFramesEnd = Directory.GetFiles(baseFramesDirEnd).Select(fp => new FrameData
            {
                FilePath = fp,
                Time = GetTimeFromFileName(fp, extractFramesBaseEnd.frameRate, baseEndWindowOffset)
            }).OrderBy(frame => frame.Time).ToList();

            var compareFramesEnd = Directory.GetFiles(compareFramesDirEnd).Select(fp => new FrameData
            {
                FilePath = fp,
                Time = GetTimeFromFileName(fp, extractFramesCompareEnd.frameRate, compareEndWindowOffset)
            }).OrderBy(frame => frame.Time).ToList();

            var startOffset = _syncingService.CalculateOffset(baseFramesStart, compareFramesStart);
            var endOffset = _syncingService.CalculateOffset(baseFramesEnd, compareFramesEnd, true);

            var lengthDiff = (baseVideoDurationTimeSpan.Value.TotalMicroseconds - compareVideoDurationTimeSpan.Value.TotalMicroseconds) / 1000000;

            if (double.IsNaN(startOffset) || double.IsNaN(endOffset))
            {
                Console.Error.WriteLine("Couldn't find enough matching frames to sync dub.");
                return (-100, startOffset, endOffset, lengthDiff);
            }

            _logger?.LogInformation("Start offset: {StartOffset} seconds, End offset: {EndOffset} seconds", startOffset, endOffset);

            baseFramesStart.Clear();
            baseFramesEnd.Clear();
            compareFramesStart.Clear();
            compareFramesEnd.Clear();

            var difference = Math.Abs(startOffset - endOffset);

            switch (difference)
            {
                case < 0.1:
                    return (startOffset, startOffset, endOffset, lengthDiff);
                case > 1:
                    _logger?.LogWarning("Couldn't sync dub - Start: {StartOffset}, End: {EndOffset}, LengthDiff: {LengthDiff}", startOffset, endOffset, lengthDiff);
                    Console.Error.WriteLine($"Couldn't sync dub:");
                    Console.Error.WriteLine($"\tStart offset: {startOffset} seconds");
                    Console.Error.WriteLine($"\tEnd offset: {endOffset} seconds");
                    Console.Error.WriteLine($"\tVideo length difference: {lengthDiff} seconds");
                    return (-100, startOffset, endOffset, lengthDiff);
                default:
                    return (endOffset, startOffset, endOffset, lengthDiff);
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error processing video sync");
            Console.Error.WriteLine(e);
            return (-100, 0, 0, 0);
        }
        finally
        {
            CleanupDirectory(cleanupDir);
        }
    }

    private static void CleanupDirectory(string dirPath)
    {
        if (!string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
        {
            Directory.Delete(dirPath, true);
        }
    }

    private static double GetTimeFromFileName(string fileName, double frameRate, double timeOffset)
    {
        var match = Regex.Match(Path.GetFileName(fileName), @"frame(\d+)");
        if (match.Success)
        {
            return timeOffset + int.Parse(match.Groups[1].Value) / frameRate;
        }

        return timeOffset;
    }

    private static async Task<TimeSpan?> GetMediaDurationAsync(string ffmpegPath, string videoPath)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = $"-i \"{videoPath}\" -f null -";
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var match = Regex.Match(output, @"Duration: (\d+):(\d+):(\d+\.\d+)");
            if (match.Success)
            {
                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }
}
