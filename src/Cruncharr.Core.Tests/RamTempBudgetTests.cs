using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

// The RAM ceiling applies only to a temp folder in RAM, counts the container's real memory (not
// reclaimable file cache) and reserves room for the next episode and an encoder not yet running.
public class RamTempBudgetTests
{
    private const long GiB = 1L << 30;

    // Mount table of the live container on 2026-11-12: /tmp/cruncharr is a tmpfs bind mount.
    private static readonly string[] MountInfo =
    [
        "472 300 0:80 / / rw,relatime master:1 - overlay overlay rw,lowerdir=/x",
        "553 472 0:34 /Cruncharr /tmp/cruncharr rw,relatime - tmpfs tmpfs rw,inode64",
        "560 472 0:51 /appdata/Cruncharr /config rw,relatime - zfs cache/appdata rw",
        "561 472 0:52 /data/media/Anime /downloads rw,relatime - fuse.shfs shfs rw"
    ];

    [Theory]
    [InlineData("/tmp/cruncharr", true)]
    [InlineData("/tmp/cruncharr/.crtmp-1", true)]
    [InlineData("/tmp/cruncharr-other", false)]
    [InlineData("/downloads/temp", false)]
    [InlineData("/config", false)]
    public void TempFolderCountsAsRamOnlyOnTmpfs(string path, bool expected) =>
        Assert.Equal(expected, RamTempBudget.IsRamBacked(MountInfo, path));

    [Fact]
    public void MemoryInUseIsProcessMemoryPlusTmpfsFilesWithoutFileCache()
    {
        string[] v2 = ["anon 654249984", "file 95416320", "shmem 51576832"];
        Assert.Equal(654249984L + 51576832L, RamTempBudget.ParseMemoryStat(v2, "anon", "shmem"));
        Assert.Null(RamTempBudget.ParseMemoryStat(["file 1"], "anon", "shmem"));
    }

    [Fact]
    public void LimitIsAQuarterOfAvailableMemoryUnlessSet()
    {
        var budget = Budget(used: 0);
        Assert.Equal(16 * GiB, budget.LimitBytes(new DownloadConfig()));
        Assert.Equal(6 * GiB, budget.LimitBytes(new DownloadConfig { TempMemoryLimitGb = 6 }));
    }

    [Fact]
    public void ADownloadWaitsUntilTheEpisodeAndAnUnstartedEncoderFit()
    {
        var download = new DownloadConfig { UseTempFolder = true, TempMemoryLimitGb = 10 };
        // 3 GB episode + 4 GB encoder reserve on top of 3 GB in use: exactly 10 GB.
        Assert.True(Budget(used: 3 * GiB).HasRoomForDownload(download, newEpisodes: 1, encodersToStart: 1, out _));
        Assert.False(Budget(used: 4 * GiB).HasRoomForDownload(download, newEpisodes: 1, encodersToStart: 1, out var reason));
        Assert.Equal("Waiting for RAM: 4.0 of 10.0 GB in use", reason);
        // A running encoder is already part of the memory in use.
        Assert.True(Budget(used: 4 * GiB).HasRoomForDownload(download, newEpisodes: 1, encodersToStart: 0, out _));
    }

    [Fact]
    public void ATempFolderOnDiskIsNotLimited()
    {
        var budget = Budget(used: 60 * GiB, ram: false);
        Assert.True(budget.HasRoomForDownload(new DownloadConfig { UseTempFolder = true, TempMemoryLimitGb = 1 }, 1, 1, out _));
        Assert.True(Budget(used: 60 * GiB).HasRoomForDownload(new DownloadConfig { UseTempFolder = false, TempMemoryLimitGb = 1 }, 1, 1, out _));
    }

    [Fact]
    public void EstimatesOnlyGrowWithMeasuredSizes()
    {
        var budget = Budget(used: 0);
        budget.RecordEpisode(1 * GiB);
        budget.RecordEncoder(9 * GiB);
        Assert.Equal(RamTempBudget.DefaultEpisodeBytes, budget.EpisodeBytes);
        Assert.Equal(9 * GiB, budget.EncoderBytes);
    }

    private static RamTempBudget Budget(long used, bool ram = true) =>
        new(_ => ram, _ => used, availableMemory: 64 * GiB);
}
