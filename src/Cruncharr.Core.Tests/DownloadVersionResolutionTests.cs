using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

/// <summary>
/// REGRESSION GUARD for the "English label, Japanese audio" bug (beta.120). The Add Download flow
/// posted a versions array built from the BASE episode guid (no per-dub MediaGuid), and the
/// downloader trusted it -> streamed the original audio under the requested dub's label. The fix:
/// when a specific dub is requested, ALWAYS re-resolve versions from Crunchyroll. These tests pin
/// that behaviour so a future change can't silently start trusting client-posted dub versions.
/// </summary>
public class DownloadVersionResolutionTests
{
    private static EpisodeVersion V(string locale, string guid, bool original = false) =>
        new EpisodeVersion { AudioLocale = locale, Guid = guid, Original = original };

    [Fact]
    public void Refetches_WhenNoVersionsPosted()
    {
        var ep = new EpisodeInfo { Id = "GYW4G5536", SelectedDubs = new() { "en-US" } };
        Assert.True(DownloadService.ShouldRefetchVersions(ep));
    }

    [Fact]
    public void Refetches_WhenDubRequested_EvenIfVersionsPosted()
    {
        // The exact broken payload shape: a single version carrying the base episode guid.
        var ep = new EpisodeInfo
        {
            Id = "GYW4G5536",
            SelectedDubs = new() { "en-US" },
            Versions = new() { V("en-US", "GYW4G5536") }
        };
        Assert.True(DownloadService.ShouldRefetchVersions(ep));
    }

    [Fact]
    public void DoesNotRefetch_WhenVersionsPosted_AndNoSpecificDubRequested()
    {
        var ep = new EpisodeInfo
        {
            Id = "GYW4G5536",
            SelectedDubs = null,
            Versions = new() { V("ja-JP", "G60XVK42R", original: true) }
        };
        Assert.False(DownloadService.ShouldRefetchVersions(ep));
    }

    [Fact]
    public void Refetches_WhenSelectedDubsContainsOnlyBlank()
    {
        // Blank/whitespace dub entries are not a real request -> only the no-versions rule applies.
        var ep = new EpisodeInfo
        {
            Id = "GYW4G5536",
            SelectedDubs = new() { "  " },
            Versions = new() { V("ja-JP", "G60XVK42R", original: true) }
        };
        Assert.False(DownloadService.ShouldRefetchVersions(ep));
    }
}
