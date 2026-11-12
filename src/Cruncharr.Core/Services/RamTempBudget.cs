using System.Globalization;
using Cruncharr.Core.Configuration;

namespace Cruncharr.Core.Services;

// A hard memory ceiling for users who point the temp folder at RAM (tmpfs). Everything written
// there is memory: on 2026-11-11 episodes waiting behind an AV1 encode took the container to
// 18 GB and the host killed a virtual machine. With the folder in RAM, a download starts only
// when the container's memory (app, encoder and temp files together) leaves room for one more
// episode and for an encoder that is not running yet. A temp folder on disk is not limited.
public sealed class RamTempBudget
{
    public const double AutomaticShare = 0.25;
    private const long GiB = 1L << 30;
    internal const long DefaultEpisodeBytes = 3 * GiB;
    internal const long DefaultEncoderBytes = 4 * GiB;

    private readonly Func<string, bool> _isRamBacked;
    private readonly Func<string, long> _memoryInUse;
    private readonly long _availableMemory;
    private long _episodeBytes = DefaultEpisodeBytes;
    private long _encoderBytes = DefaultEncoderBytes;

    public RamTempBudget()
        : this(IsRamBackedPath, ReadMemoryInUse, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) { }

    internal RamTempBudget(Func<string, bool> isRamBacked, Func<string, long> memoryInUse, long availableMemory)
    {
        _isRamBacked = isRamBacked;
        _memoryInUse = memoryInUse;
        _availableMemory = availableMemory;
    }

    public long EpisodeBytes => Interlocked.Read(ref _episodeBytes);
    public long EncoderBytes => Interlocked.Read(ref _encoderBytes);

    // The estimates start conservative and keep the largest size actually measured, so a longer
    // episode or a heavier encoder preset raises them and the ceiling still holds.
    public void RecordEpisode(long bytes) => RaiseTo(ref _episodeBytes, bytes);
    public void RecordEncoder(long bytes) => RaiseTo(ref _encoderBytes, bytes);

    public bool Applies(DownloadConfig download) => download.UseTempFolder && _isRamBacked(download.TempDirectory);

    public long LimitBytes(DownloadConfig download) => download.TempMemoryLimitGb > 0
        ? (long)(download.TempMemoryLimitGb * GiB)
        : (long)(_availableMemory * AutomaticShare);

    // newEpisodes: downloads about to start. encodersToStart: free encoder slots, which episodes
    // already downloaded will fill without asking again.
    public bool HasRoomForDownload(DownloadConfig download, int newEpisodes, int encodersToStart, out string? waitReason)
    {
        waitReason = null;
        if (!Applies(download)) return true;
        var used = _memoryInUse(download.TempDirectory);
        var limit = LimitBytes(download);
        if (used + newEpisodes * EpisodeBytes + Math.Max(0, encodersToStart) * EncoderBytes <= limit) return true;
        waitReason = string.Create(CultureInfo.InvariantCulture,
            $"Waiting for RAM: {used / (double)GiB:0.0} of {limit / (double)GiB:0.0} GB in use");
        return false;
    }

    private static void RaiseTo(ref long field, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref field)) &&
               Interlocked.CompareExchange(ref field, value, current) != current) { }
    }

    // The container's memory as the kernel accounts it: process memory of the app and its encoder
    // (anon) plus the files they wrote to tmpfs (shmem). Reclaimable file cache is left out, since
    // writing finished episodes to disk would otherwise count against the ceiling. Outside a
    // container cgroup, the app's own memory plus the temp folder's files is the closest figure.
    private static long ReadMemoryInUse(string tempDirectory)
    {
        foreach (var (path, anon, shmem) in new[]
                 {
                     ("/sys/fs/cgroup/memory.stat", "anon", "shmem"),               // cgroup v2
                     ("/sys/fs/cgroup/memory/memory.stat", "total_rss", "total_shmem") // cgroup v1
                 })
        {
            try
            {
                if (File.Exists(path) && ParseMemoryStat(File.ReadAllLines(path), anon, shmem) is { } bytes) return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable accounting file: try the next source, then the fallback below.
            }
        }
        return Environment.WorkingSet + DirectoryBytes(tempDirectory);
    }

    internal static long? ParseMemoryStat(IEnumerable<string> lines, string anonKey, string shmemKey)
    {
        long? anon = null, shmem = null;
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length != 2 || !long.TryParse(parts[1], out var value)) continue;
            if (parts[0] == anonKey) anon = value;
            else if (parts[0] == shmemKey) shmem = value;
        }
        return anon.HasValue ? anon + (shmem ?? 0) : null;
    }

    internal static long DirectoryBytes(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsRamBackedPath(string path)
    {
        try
        {
            return File.Exists("/proc/self/mountinfo") &&
                   IsRamBacked(File.ReadAllLines("/proc/self/mountinfo"), Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // mountinfo: "id parent major:minor root mount-point options ... - fstype source options".
    // The mount with the longest mount point containing the path decides its filesystem.
    internal static bool IsRamBacked(IEnumerable<string> mountInfo, string fullPath)
    {
        string? bestMount = null;
        string? bestType = null;
        foreach (var line in mountInfo)
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 0 || separator + 1 >= fields.Length) continue;
            var mount = fields[4].Replace("\\040", " ");
            var contains = mount == "/" || fullPath == mount || fullPath.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (!contains || bestMount != null && mount.Length < bestMount.Length) continue;
            bestMount = mount;
            bestType = fields[separator + 1];
        }
        return bestType is "tmpfs" or "ramfs";
    }
}
