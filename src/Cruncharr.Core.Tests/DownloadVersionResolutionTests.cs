using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Newtonsoft.Json;
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

    [Fact]
    public void Refetches_WhenAudioDescriptionNeedsVersionRoles()
    {
        var ep = new EpisodeInfo
        {
            Id = "GYW4G5536",
            Versions = [V("en-US", "english")]
        };

        Assert.True(DownloadService.ShouldRefetchVersions(ep, requireAudioRoles: true));
    }

    [Fact]
    public void RefreshedEpisode_CopiesAuthoritativeSubtitleLocales()
    {
        var queued = new EpisodeInfo { Id = "GE00367377JAJP", SubtitleLocales = [] };
        var refreshed = new EpisodeInfo
        {
            Id = queued.Id,
            SubtitleLocales = ["en-US", "es-419", "pt-BR"]
        };

        DownloadService.ApplyRefreshedEpisodeMetadata(queued, refreshed, needVersions: true);

        Assert.Equal(refreshed.SubtitleLocales, queued.SubtitleLocales);
    }

    [Fact]
    public void RequiredLanguageCheck_AcceptsEnglishAdvertisedByCrunchyroll()
    {
        var episode = new EpisodeInfo
        {
            SelectedDubs = ["ja-JP"],
            SelectedSubs = ["en-US"],
            SubtitleLocales = ["en-US", "es-419"],
            Versions = [V("ja-JP", "GE00367377JAJP", original: true)]
        };
        var config = new CruncharrConfig();

        var missing = DownloadService.FindMissingSelectedLanguages(episode, config);

        Assert.Empty(missing.MissingDubs);
        Assert.Empty(missing.MissingSubs);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("none")]
    public void RequiredLanguageCheck_SubtitleSentinelsAreNotLiteralLocales(string sentinel)
    {
        var episode = new EpisodeInfo
        {
            SelectedSubs = [sentinel],
            SubtitleLocales = [],
            Versions = [V("ja-JP", "original", original: true)]
        };

        var missing = DownloadService.FindMissingSelectedLanguages(episode, new CruncharrConfig());

        Assert.Empty(missing.MissingSubs);
    }

    [Fact]
    public void RequiredLanguageCheck_SkipSubsDoesNotRequireConfiguredSubtitle()
    {
        var episode = new EpisodeInfo
        {
            SubtitleLocales = [],
            Versions = [V("ja-JP", "original", original: true)]
        };
        var config = new CruncharrConfig();
        config.Download.SkipSubs = true;
        config.Download.SoftSubs = ["en-US"];

        var missing = DownloadService.FindMissingSelectedLanguages(episode, config);

        Assert.Empty(missing.MissingSubs);
    }

    [Fact]
    public void RequiredLanguageCheck_FirstAvailableDubAcceptsOneRequestedMatch()
    {
        var episode = new EpisodeInfo
        {
            SelectedDubs = ["en-US", "ja-JP"],
            Versions = [V("en-US", "english")]
        };
        var config = new CruncharrConfig();
        config.Download.DownloadFirstAvailableDub = true;

        var missing = DownloadService.FindMissingSelectedLanguages(episode, config);

        Assert.Empty(missing.MissingDubs);
    }

    [Fact]
    public void SelectedVersionMetadata_UsesActualStreamLocale()
    {
        var episode = new EpisodeInfo { AudioLocale = "ja-JP" };

        DownloadService.ApplySelectedVersionMetadata(episode, V("en-US", "english"));

        Assert.Equal("en-US", episode.AudioLocale);
        Assert.Equal("en-US", episode.Locale);
    }

    [Fact]
    public void ExplicitMultipleDubs_EnableMultiDubWithoutGlobalToggle()
    {
        var episode = new EpisodeInfo
        {
            SelectedDubs = ["en-US", "ja-JP"],
            Versions = [V("en-US", "english"), V("ja-JP", "japanese", original: true)]
        };
        var config = new CruncharrConfig();
        config.Download.DownloadMultipleDubs = false;

        Assert.True(DownloadService.ShouldDownloadMultipleDubs(episode, config));
    }

    [Fact]
    public void RequestedAudioLanguage_UsesFirstPerDownloadDub()
    {
        var episode = new EpisodeInfo { SelectedDubs = ["en-US", "ja-JP"] };
        var config = new CruncharrConfig();
        config.Download.DefaultAudio = "ja-JP";

        Assert.Equal("en-US", DownloadService.ResolveRequestedAudioLanguage(episode, config));
    }

    [Fact]
    public void EpisodeVersionRoles_AreDeserializedAndPreserved()
    {
        var raw = JsonConvert.DeserializeObject<CrEpisodeVersion>(
            "{\"audio_locale\":\"en-US\",\"guid\":\"english\",\"roles\":[\"description\"]}");

        var mapped = CrunchyrollApiService.MapEpisodeVersion(Assert.IsType<CrEpisodeVersion>(raw));

        Assert.Equal(["description"], mapped.Roles);
    }

    [Fact]
    public void AudioDescriptionVersion_IsFoundEvenWhenNormalAudioSharesItsLocale()
    {
        var episode = new EpisodeInfo
        {
            Versions =
            [
                V("en-US", "normal"),
                new EpisodeVersion { AudioLocale = "en-US", Guid = "ad", Roles = ["description"] }
            ]
        };

        var adVersion = DownloadService.FindAudioDescriptionVersion(episode);

        Assert.NotNull(adVersion);
        Assert.Equal("ad", adVersion.Guid);
    }

    [Theory]
    [InlineData("audio_enus_ad.m4a")]
    [InlineData("audio_enus_ad.m4s")]
    [InlineData("audio_enus_ad.enc.m4s")]
    public void AudioDescriptionFile_IsRecognizedForMuxMetadata(string fileName)
    {
        Assert.True(DownloadService.IsAudioDescriptionFile(fileName));
        Assert.False(DownloadService.IsAudioDescriptionFile("audio_enus.m4a"));
    }
}
