using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Tests;

// Live 2026-10-30: episode 1 of several simulcasts offered English only as audio description,
// and Cruncharr treated it as the English dub - it was downloaded as plain "English" and the
// scheduler considered English released. Audio description is now its own language key.
public class AudioDescriptionLanguageTests
{
    private static EpisodeInfo Episode(params EpisodeVersion[] versions) => new() { Id = "EP1", Versions = [.. versions] };
    private static EpisodeVersion Version(string locale, bool original = false, bool described = false) => new()
    {
        AudioLocale = locale,
        Guid = locale + (described ? "-ad" : ""),
        Original = original,
        Roles = described ? ["description", "dub"] : [original ? "main" : "dub"]
    };

    [Fact]
    public void DescribedVersionHasItsOwnLanguageKey()
    {
        Assert.Equal("en-US", Version("en-US").LanguageKey);
        Assert.Equal("en-US*", Version("en-US", described: true).LanguageKey);
        Assert.True(EpisodeVersion.IsAudioDescriptionKey("en-US*"));
        Assert.False(EpisodeVersion.IsAudioDescriptionKey("en-US"));
    }

    [Fact]
    public void AudioDescriptionDoesNotSatisfyARequestForTheDub()
    {
        var episode = Episode(Version("ja-JP", original: true), Version("en-US", described: true));
        var config = new CruncharrConfig { Download = { SkipSubs = true } };

        episode.SelectedDubs = ["en-US"];
        Assert.Equal(["en-US"], DownloadService.FindMissingSelectedLanguages(episode, config).MissingDubs);

        episode.SelectedDubs = ["en-US*"];
        Assert.Empty(DownloadService.FindMissingSelectedLanguages(episode, config).MissingDubs);
        Assert.True(DownloadService.RequestsAudioDescription(episode));
    }

    [Fact]
    public void AudioDescriptionTrackFollowsTheRequestedLanguage()
    {
        var episode = Episode(Version("ja-JP", original: true), Version("en-US", described: true), Version("fr-FR", described: true));

        Assert.Equal("fr-FR", DownloadService.FindAudioDescriptionVersion(episode, ["fr-FR*"])!.AudioLocale);
        Assert.Equal("fr-FR", DownloadService.FindAudioDescriptionVersion(episode, ["fr-FR"])!.AudioLocale);
        Assert.Equal("en-US", DownloadService.FindAudioDescriptionVersion(episode, ["de-DE"])!.AudioLocale);
        Assert.Null(DownloadService.FindAudioDescriptionVersion(Episode(Version("ja-JP", original: true)), ["en-US*"]));
    }

    [Fact]
    public void HistoryNeverCountsDescribedAudioAsTheDub()
    {
        Assert.False(HistoryEpisode.ContainsLanguage(["ja-JP", "en-US*"], "en-US"));
        Assert.True(HistoryEpisode.ContainsLanguage(["ja-JP", "en-US*"], "EN-us*"));
    }

    [Fact]
    public void SeriesListKeepsTheDubAndItsDescriptionAsSeparateVariants()
    {
        var english = Languages.FindLang("en-US");
        var item = new CrEpisodeDetail { Id = "EP1" };
        var variants = new EpisodeAndLanguage();

        Assert.True(variants.AddUnique(item, english, isAudioDescription: true));
        Assert.True(variants.AddUnique(item, english));
        Assert.False(variants.AddUnique(item, english));

        Assert.Equal(["en-US*", "en-US"], variants.Variants.Select(v => v.LanguageKey));
    }
}
