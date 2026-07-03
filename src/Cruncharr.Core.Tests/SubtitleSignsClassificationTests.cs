using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// GUARD TESTS — subtitle "signs" classification (live bug: zero subtitles downloaded).
//
// Upstream (CrunchyrollManager.DownloadSubtitles) classifies a subtitle as signs/songs
// PER VERSION: a non-CC subtitle whose locale equals the audio locale of the playback
// version it came from. The en-US dub version's en-US track is signs-only; the FULL en-US
// dialogue track lives on the ja-JP original version. Classifying against the set of
// downloaded dub locales instead (the old behavior) marked the full dialogue track as
// signs, and IncludeSignsSubs=false then dropped every subtitle. Do not regress.
public class SubtitleSignsClassificationTests
{
    [Fact]
    public void SubFromDubVersion_SameLocale_IsSigns()
    {
        var sub = new SubtitleInfo { Lang = "en-US", IsCC = false, SourceAudioLocale = "en-US" };
        Assert.True(DownloadService.IsSignsSubtitle(sub));
    }

    [Fact]
    public void SubFromOriginalVersion_DifferentLocale_IsFullDialogue()
    {
        // Full en-US dialogue fetched from the ja-JP original version must NOT be signs,
        // even when an en-US dub was downloaded.
        var sub = new SubtitleInfo { Lang = "en-US", IsCC = false, SourceAudioLocale = "ja-JP" };
        Assert.False(DownloadService.IsSignsSubtitle(sub));
    }

    [Fact]
    public void ClosedCaption_IsNeverSigns()
    {
        var sub = new SubtitleInfo { Lang = "en-US", IsCC = true, SourceAudioLocale = "en-US" };
        Assert.False(DownloadService.IsSignsSubtitle(sub));
    }

    [Fact]
    public void UnknownOrigin_IsNotSigns()
    {
        // When the playback response carried no audioLocale we must keep the track —
        // dropping a potential full dialogue sub is worse than muxing an extra signs track.
        var sub = new SubtitleInfo { Lang = "en-US", IsCC = false, SourceAudioLocale = null };
        Assert.False(DownloadService.IsSignsSubtitle(sub));
    }
}
