using System.Reflection;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Moq;

namespace Cruncharr.Core.Tests;

// Audit F02: replacing media deleted the existing file before the replacement had been staged, so
// a failed copy (or an earlier download/mux/encode failure) left the user with nothing.
public class OutputReplacementTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-replace-");
    private readonly DownloadService _download = new(Mock.Of<ICrunchyrollAuthService>(), Mock.Of<ICrunchyrollApiService>());

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Replace_SwapsTheFinishedFileInAndLeavesNoStagingFile()
    {
        var (source, dest) = Files("new", "old");

        Move(source, dest, replace: true);

        Assert.Equal("new", File.ReadAllText(dest));
        Assert.False(File.Exists(source));
        Assert.Equal([dest], Directory.GetFiles(Path.GetDirectoryName(dest)!));
    }

    [Fact]
    public void Conflict_WithoutReplace_KeepsBothFiles()
    {
        var (source, dest) = Files("new", "old");

        var error = Assert.Throws<DownloadException>(() => Move(source, dest, replace: false));

        Assert.Equal(DownloadErrorType.OutputConflict, error.ErrorType);
        Assert.Equal("old", File.ReadAllText(dest));
        Assert.Equal("new", File.ReadAllText(source));
    }

    [Fact]
    public void FailedMove_KeepsTheExistingFile()
    {
        // An exclusively held source makes both the rename and the copy fallback fail. Only
        // Windows enforces that lock; elsewhere file locks are advisory.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs mandatory file locking");
        var (source, dest) = Files("new", "old");

        using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Move(source, dest, replace: true));
        }

        Assert.Equal("old", File.ReadAllText(dest));
        Assert.Equal([dest], Directory.GetFiles(Path.GetDirectoryName(dest)!));
    }

    [Fact]
    public void FailedSwap_RemovesTheStagingFile()
    {
        var (source, dest) = Files("new", existing: null);
        Directory.CreateDirectory(dest); // the final rename cannot replace a directory

        Assert.ThrowsAny<Exception>(() => Move(source, dest, replace: true));

        Assert.True(Directory.Exists(dest));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!));
    }

    private (string Source, string Dest) Files(string content, string? existing)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root.FullName, "work")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(_root.FullName, "output")).FullName;
        var source = Path.Combine(work, "Episode.mkv");
        var dest = Path.Combine(output, "Episode.mkv");
        File.WriteAllText(source, content);
        if (existing != null) File.WriteAllText(dest, existing);
        return (source, dest);
    }

    private void Move(string source, string dest, bool replace)
    {
        try
        {
            typeof(DownloadService).GetMethod("MoveToFinalPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_download, [source, dest, replace]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }
}
