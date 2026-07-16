using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
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

        Assert.Equal("Part One  Part Two Finale", name);
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

        var limitedName = DownloadService.LimitOutputFileName(rawName, template, sonarrTitle, string.Empty);

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
