using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Cruncharr.Core.Utils.Muxing.Commands;
using Cruncharr.Core.Utils.Muxing.Structs;
using Xunit;

namespace Cruncharr.Core.Tests;

// Tests for the upstream-parity gaps closed in this change set:
// UseSonarrNumbering (filename) and SubtitleUtils (ScaledBorderAndShadow / FixCccSubtitles).
public class PortedGapTests
{
    private static EpisodeInfo MakeEpisode() => new()
    {
        Title = "The Title",
        SeriesTitle = "My Show",
        SeasonNumber = 2,
        EpisodeNumber = 5,
        Episode = "5"
    };

    [Fact]
    public void AudioDescription_IsNamedAndNeverDefaultInBothMuxers()
    {
        var english = Languages.FindLang("en-US");
        var options = new MergerOptions
        {
            Output = "output.mkv",
            DubLangList = ["en-US"],
            Defaults = new Defaults { Audio = english },
            OnlyAudio =
            [
                new MergerInput { Path = "normal.m4a", Language = english },
                new MergerInput { Path = "description.m4a", Language = english, IsAudioRoleDescription = true }
            ]
        };

        var ffmpeg = new FFmpegCommandBuilder(options).Build();
        var mkvmerge = new MkvMergeCommandBuilder(options).Build();

        Assert.Contains("-metadata:s:a:1 title=\"English [AD]\"", ffmpeg);
        Assert.Contains("-disposition:a:0 default", ffmpeg);
        Assert.Contains("-disposition:a:1 0", ffmpeg);
        Assert.Contains("--track-name 0:\"English [AD]\" --language 0:eng --default-track 0:0", mkvmerge);
    }

    [Fact]
    public void LanguageLookup_AcceptsCaseVariantsWithoutReturningUndefined()
    {
        Assert.Equal("en-US", Languages.FindLang("EN-us").CrLocale);
        Assert.Equal("en-US", Languages.Locale2language("EN-us").CrLocale);
    }

    [Fact]
    public void UseSonarrNumbering_OverridesEpisodeAndSeason()
    {
        var svc = new FilenameService();
        var opts = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode { SeasonNumber = 7, EpisodeNumber = 13, Title = "x" }
        };

        var name = svc.FormatFilename("S{season:00}E{episode:00}", MakeEpisode(), opts);

        Assert.Equal("S07E13", name);
    }

    [Theory]
    [InlineData(SonarrColonReplacementFormat.Smart, "CSI - Vegas")]
    [InlineData(SonarrColonReplacementFormat.Dash, "CSI- Vegas")]
    [InlineData(SonarrColonReplacementFormat.Delete, "CSI Vegas")]
    [InlineData(SonarrColonReplacementFormat.SpaceDash, "CSI - Vegas")]
    [InlineData(SonarrColonReplacementFormat.SpaceDashSpace, "CSI - Vegas")]
    public void UseSonarrNumbering_AppliesCanonicalColonReplacement(
        SonarrColonReplacementFormat replacement,
        string expectedSeriesTitle)
    {
        var episode = MakeEpisode();
        episode.SeriesTitle = "Crunchyroll Series";
        var options = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrSeries = new SonarrSeries { Title = "CSI: Vegas" },
            SonarrEpisode = new SonarrEpisode
            {
                SeasonNumber = 1,
                EpisodeNumber = 5,
                Title = "Episode: Title"
            },
            SonarrNamingConfig = new SonarrNamingConfig
            {
                ReplaceIllegalCharacters = true,
                ColonReplacementFormat = replacement
            }
        };

        var name = new FilenameService().FormatFilename("{Series Title}", episode, options);

        Assert.Equal(expectedSeriesTitle, name);
    }

    [Fact]
    public void UseSonarrNumbering_UsesCanonicalSeriesAndEpisodeIdentity()
    {
        var episode = MakeEpisode();
        episode.SeriesTitle = "Crunchyroll Series";
        episode.Title = "Crunchyroll Episode";
        var options = new FilenameOptions
        {
            Quality = "1080",
            UseSonarrNumbering = true,
            SonarrSeries = new SonarrSeries { Title = "Canonical: Series" },
            SonarrEpisode = new SonarrEpisode
            {
                SeasonNumber = 3,
                EpisodeNumber = 7,
                Title = "Canonical: Episode"
            },
            SonarrNamingConfig = SonarrNamingConfig.Default
        };

        var name = new FilenameService().FormatFilename(
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}",
            episode,
            options);

        Assert.Equal("Canonical - Series - S03E07 - Canonical - Episode WEBDL-1080p", name);
    }

    [Theory]
    [InlineData("---- Is All You Need", "- Is All You Need")]
    [InlineData("A....B", "A.B")]
    [InlineData("A__B", "A_B")]
    public void UseSonarrNumbering_CollapsesRepeatedSeparators(string sonarrTitle, string expected)
    {
        var options = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode
            {
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Title = sonarrTitle
            },
            SonarrNamingConfig = SonarrNamingConfig.Default
        };

        var name = new FilenameService().FormatFilename("{Episode Title}", MakeEpisode(), options);

        Assert.Equal(expected, name);
    }

    [Fact]
    public void UseSonarrNumbering_CrSeriesAliasKeepsCrunchyrollTitle()
    {
        var episode = MakeEpisode();
        episode.SeriesTitle = "Crunchyroll: Series";
        var options = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrSeries = new SonarrSeries { Title = "Canonical: Series" },
            SonarrEpisode = new SonarrEpisode { SeasonNumber = 2, EpisodeNumber = 5, Title = "Title" },
            SonarrNamingConfig = SonarrNamingConfig.Default
        };

        var name = new FilenameService().FormatFilename("{crSeriesTitle}", episode, options);

        Assert.Equal("Crunchyroll Series", name);
    }

    [Fact]
    public void FormatFilename_SupportsSonarrStyleTokens()
    {
        var svc = new FilenameService();
        var opts = new FilenameOptions { Quality = "1080p" };

        var name = svc.FormatFilename(
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}",
            MakeEpisode(), opts);

        // {Quality Full} is Sonarr source-qualified: Crunchyroll = WEBDL.
        Assert.Equal("My Show - S02E05 - The Title WEBDL-1080p", name);
    }

    [Fact]
    public void FormatFilename_LegacyTitleToken_SanitizesDynamicPathSeparators()
    {
        var svc = new FilenameService();
        var episode = MakeEpisode();
        var opts = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode
            {
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Title = "Part One / Part Two: Finale?"
            }
        };

        var name = svc.FormatFilename("{Episode Title}", episode, opts);

        Assert.Equal("Part One + Part Two - Finale!", name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, name);
    }

    [Fact]
    public void DownloadSeriesFolder_UsesDesktopCrossPlatformSanitization()
    {
        var method = typeof(DownloadService).GetMethod(
            "SanitizeFolderName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var folder = (string?)method?.Invoke(null, new object[] { "Wistoria: Wand and Sword" });

        Assert.Equal("Wistoria Wand and Sword", folder);
    }

    [Fact]
    public void DownloadSeriesFolder_PrefersSonarrPathBasename()
    {
        var episode = MakeEpisode();
        var sonarrSeries = new SonarrSeries
        {
            Title = "Sonarr Display Title",
            Path = "/tv/My Show (2026)"
        };

        var folder = DownloadService.ResolveSeriesFolderName(episode, sonarrSeries);

        Assert.Equal("My Show (2026)", folder);
    }

    [Fact]
    public void DownloadSeriesFolder_UsesSonarrTitleWhenPathIsMissing()
    {
        var episode = MakeEpisode();
        var sonarrSeries = new SonarrSeries
        {
            Title = "Sonarr: Display Title"
        };

        var folder = DownloadService.ResolveSeriesFolderName(episode, sonarrSeries);

        Assert.Equal("Sonarr Display Title", folder);
    }

    [Fact]
    public void DownloadSeriesFolder_WithoutSonarrMatch_UsesCrunchyrollTitle()
    {
        var folder = DownloadService.ResolveSeriesFolderName(MakeEpisode(), null);

        Assert.Equal("My Show", folder);
    }

    [Theory]
    [InlineData(0, "Specials")]
    [InlineData(1, "Season 01")]
    [InlineData(12, "Season 12")]
    public void DownloadSeasonFolder_UsesSonarrNamingFormat(int seasonNumber, string expected)
    {
        var naming = new SonarrNamingConfig
        {
            SeasonFolderFormat = "Season {season:00}",
            SpecialsFolderFormat = "Specials"
        };

        var folder = DownloadService.ResolveSeasonFolderName(seasonNumber, naming);

        Assert.Equal(expected, folder);
    }

    [Fact]
    public void DownloadNaming_SavedSonarrIdentityNeverSilentlyFallsBack()
    {
        var series = new SonarrSeries { Id = 801, Title = "Canonical Series" };
        var episode = new SonarrEpisode { Id = 26363, SeriesId = 801 };

        Assert.True(DownloadService.ShouldDeferForMissingSonarrIdentity(true, 26363, null, null));
        Assert.True(DownloadService.ShouldDeferForMissingSonarrIdentity(true, 26363, episode, null));
        Assert.False(DownloadService.ShouldDeferForMissingSonarrIdentity(true, 26363, episode, series));
        Assert.False(DownloadService.ShouldDeferForMissingSonarrIdentity(false, 26363, null, null));
        Assert.False(DownloadService.ShouldDeferForMissingSonarrIdentity(true, null, null, null));
    }

    [Fact]
    public void DownloadNaming_SpecialNeverUsesUnrelatedAbsoluteNumber()
    {
        var episode = new EpisodeInfo
        {
            Title = "Brand New OVA",
            SeasonTitle = "Specials",
            SeasonNumber = 4,
            EpisodeNumber = 1,
            Episode = "1"
        };
        var sonarrEpisodes = new List<SonarrEpisode>
        {
            new()
            {
                Id = 1001,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                AbsoluteEpisodeNumber = 1,
                Title = "Unrelated Pilot"
            }
        };

        var match = DownloadService.ResolveSonarrEpisodeFallback(episode, sonarrEpisodes);

        Assert.Null(match);
    }

    [Fact]
    public void DownloadNaming_MetadataIdentityBeatsSameNumberRegularDecoy()
    {
        var episode = new EpisodeInfo
        {
            Title = "Digression: Hinata Sakaguchi",
            Description = "Hinata considers the events that brought her face to face with Rimuru.",
            SeasonTitle = "Season 2",
            SeasonNumber = 2,
            EpisodeNumber = 24,
            Episode = "24.9"
        };
        var regularDecoy = new SonarrEpisode
        {
            Id = 2024,
            SeasonNumber = 2,
            EpisodeNumber = 24,
            Title = "Octagram"
        };
        var expectedSpecial = new SonarrEpisode
        {
            Id = 2007,
            SeasonNumber = 0,
            EpisodeNumber = 7,
            Title = "Digression - Hinata Sakaguchi"
        };

        var match = DownloadService.ResolveSonarrEpisodeFallback(
            episode,
            [regularDecoy, expectedSpecial]);

        Assert.Same(expectedSpecial, match);
    }

    [Fact]
    public void DownloadNaming_OverviewIdentityMapsSpecialWhenTitlesDiffer()
    {
        const string overview = "A look back at Season 1 and a short preview of what's to come!";
        var episode = new EpisodeInfo
        {
            Title = "Wistoria: Wand and Sword Special Episode",
            Description = overview,
            SeasonTitle = "Season 2",
            SeasonNumber = 2,
            EpisodeNumber = 1,
            Episode = "SP"
        };
        var regularDecoy = new SonarrEpisode
        {
            Id = 2101,
            SeasonNumber = 2,
            EpisodeNumber = 1,
            Title = "The Boy with No Talent"
        };
        var expectedSpecial = new SonarrEpisode
        {
            Id = 2002,
            SeasonNumber = 0,
            EpisodeNumber = 2,
            Title = "Season 2 Broadcast Commemorative Special Compilation Episode",
            Overview = overview
        };

        var match = DownloadService.ResolveSonarrEpisodeFallback(
            episode,
            [regularDecoy, expectedSpecial]);

        Assert.Same(expectedSpecial, match);
    }

    [Fact]
    public void DownloadNaming_ContinuousRegularNumberStillUsesTvdbAbsoluteNumber()
    {
        var episode = new EpisodeInfo
        {
            Title = "The Purification Plan",
            SeasonTitle = "Season 3",
            SeasonNumber = 3,
            EpisodeNumber = 278,
            Episode = "278"
        };
        var expected = new SonarrEpisode
        {
            Id = 4278,
            SeasonNumber = 8,
            EpisodeNumber = 5,
            AbsoluteEpisodeNumber = 278,
            Title = "Purification Strategy"
        };

        var match = DownloadService.ResolveSonarrEpisodeFallback(episode, [expected]);

        Assert.Same(expected, match);
    }

    [Fact]
    public void DownloadNaming_OrdinaryEpisodeKeepsExactSeasonNumberAgainstWeakCrossSeasonTitle()
    {
        var episode = new EpisodeInfo
        {
            Title = "The Promise",
            SeasonNumber = 2,
            EpisodeNumber = 3,
            Episode = "3"
        };
        var expected = new SonarrEpisode
        {
            Id = 2203,
            SeasonNumber = 2,
            EpisodeNumber = 3,
            Title = "A Promise"
        };
        var crossSeasonDecoy = new SonarrEpisode
        {
            Id = 1108,
            SeasonNumber = 1,
            EpisodeNumber = 8,
            Title = "The Promise"
        };

        var match = DownloadService.ResolveSonarrEpisodeFallback(
            episode,
            [expected, crossSeasonDecoy]);

        Assert.Same(expected, match);
    }

    [Fact]
    public void DownloadHistory_RestoresRawProviderEpisodeLabel()
    {
        var episode = new EpisodeInfo { EpisodeNumber = 24 };
        var history = new DownloadHistory { EpisodeNumber = 24, Episode = "24.9" };

        DownloadService.RestoreProviderEpisodeLabel(episode, history);

        Assert.Equal("24.9", episode.Episode);
    }

    [Fact]
    public void DownloadFilename_LongSonarrTitle_IsLimitedWithoutDroppingQualitySuffix()
    {
        const string template = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}";
        const string sonarrTitle = "The Klutzy Health Representative and the Schoolgirl Who Slips Often / Taking More Supplementary Lessons With the Klutz on the Day Her Little Sister Is Touring the Klutz's School";
        var episode = new EpisodeInfo
        {
            Title = "Crunchyroll title",
            SeriesTitle = "The Klutzy Class Monitor and the Girl with the Short Skirt",
            SeasonNumber = 1,
            EpisodeNumber = 5,
            Episode = "5"
        };
        var options = new FilenameOptions
        {
            Quality = "1080",
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode
            {
                SeasonNumber = 1,
                EpisodeNumber = 5,
                Title = sonarrTitle
            }
        };
        var rawName = new FilenameService().FormatFilename(template, episode, options);

        Assert.True((rawName + ".mkv").Length > 255);

        var renderedSonarrTitle = FilenameService.ApplySonarrFilenameRules(
            sonarrTitle,
            SonarrNamingConfig.Default);
        var limitedName = DownloadService.LimitOutputFileName(
            rawName,
            template,
            renderedSonarrTitle,
            string.Empty);

        Assert.Equal(220, Path.GetFileName(limitedName).Length);
        Assert.Contains(" - S01E05 - ", limitedName);
        Assert.EndsWith(" WEBDL-1080p", limitedName);
        Assert.True((limitedName + ".mkv").Length < 255);
    }

    [Fact]
    public void FormatFilename_BareHeightQuality_SourceQualified()
    {
        // The post-download resolution probe passes a bare height ("1080");
        // {Quality Full} should render "WEBDL-1080p" (source + resolution), matching Sonarr.
        var svc = new FilenameService();
        var opts = new FilenameOptions { Quality = "1080" };

        var name = svc.FormatFilename("{Episode Title} {Quality Full}", MakeEpisode(), opts);

        Assert.Equal("The Title WEBDL-1080p", name);
    }

    [Fact]
    public void UseSonarrNumbering_OverridesEpisodeTitleWithSonarrTitle()
    {
        // When Sonarr numbering is on and an episode matched, {Episode Title} uses the
        // Sonarr/TVDB title (e.g. "Purification Strategy") instead of the Crunchyroll title
        // ("The Purification Plan"), so the filename matches Sonarr exactly.
        var svc = new FilenameService();
        var opts = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode { SeasonNumber = 8, EpisodeNumber = 5, Title = "Purification Strategy" }
        };

        var name = svc.FormatFilename("S{season:00}E{episode:00} - {Episode Title}", MakeEpisode(), opts);

        Assert.Equal("S08E05 - Purification Strategy", name);
    }

    [Fact]
    public void UseSonarrNumbering_CrTitleAliasKeepsCrunchyrollTitle()
    {
        // {crEpisodeTitle} always keeps the Crunchyroll title even under Sonarr numbering.
        var svc = new FilenameService();
        var opts = new FilenameOptions
        {
            UseSonarrNumbering = true,
            SonarrEpisode = new SonarrEpisode { SeasonNumber = 8, EpisodeNumber = 5, Title = "Purification Strategy" }
        };

        var name = svc.FormatFilename("{crEpisodeTitle}", MakeEpisode(), opts);

        Assert.Equal("The Title", name);
    }

    [Fact]
    public void UseSonarrNumbering_Disabled_UsesCrunchyrollNumbers()
    {
        var svc = new FilenameService();
        var opts = new FilenameOptions
        {
            UseSonarrNumbering = false,
            SonarrEpisode = new SonarrEpisode { SeasonNumber = 7, EpisodeNumber = 13, Title = "x" }
        };

        var name = svc.FormatFilename("S{season:00}E{episode:00}", MakeEpisode(), opts);

        Assert.Equal("S02E05", name);
    }

    [Fact]
    public void UseSonarrNumbering_WithoutSonarrEpisode_FallsBackToCrunchyroll()
    {
        var svc = new FilenameService();
        var opts = new FilenameOptions { UseSonarrNumbering = true, SonarrEpisode = null };

        var name = svc.FormatFilename("S{season:00}E{episode:00}", MakeEpisode(), opts);

        Assert.Equal("S02E05", name);
    }

    [Fact]
    public void ScaledBorder_Yes_AddsScaledBorderLine()
    {
        var ass = "[Script Info]\r\nTitle: x\r\n\r\n[Events]\r\n";
        var result = SubtitleUtils.CleanAssAndEnsureScriptInfo(ass, false, "ScaledBorderAndShadowYes", "en-US");
        Assert.Contains("ScaledBorderAndShadow: yes", result);
    }

    [Fact]
    public void ScaledBorder_DontAdd_OmitsScaledBorderLine()
    {
        var ass = "[Script Info]\r\nTitle: x\r\n\r\n[Events]\r\n";
        var result = SubtitleUtils.CleanAssAndEnsureScriptInfo(ass, false, "DontAdd", "en-US");
        Assert.DoesNotContain("ScaledBorderAndShadow:", result);
    }

    [Fact]
    public void NormalizeScaledBorder_MapsValues()
    {
        Assert.Equal("ScaledBorderAndShadow: yes", SubtitleUtils.NormalizeScaledBorder("ScaledBorderAndShadowYes"));
        Assert.Equal("ScaledBorderAndShadow: no", SubtitleUtils.NormalizeScaledBorder("ScaledBorderAndShadowNo"));
        Assert.Null(SubtitleUtils.NormalizeScaledBorder("DontAdd"));
        Assert.Null(SubtitleUtils.NormalizeScaledBorder(""));
    }

    [Fact]
    public void FixCcc_RemovesConverterComment_WhenEnabled()
    {
        var ass = "[Script Info]\r\n; Script generated by Closed Caption Converter | www.closedcaptionconverter.com\r\nPlayDepth: 0\r\nTitle: x\r\n\r\n[Events]\r\n";
        var result = SubtitleUtils.CleanAssAndEnsureScriptInfo(ass, true, "DontAdd", "en-US");
        Assert.DoesNotContain("closedcaptionconverter.com", result);
        // CCC subs get PlayRes upserted
        Assert.Contains("PlayResX: 640", result);
    }
}
