using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// Guard for the profile -> default-language sync. Logging in / switching CR profiles sets
// DefaultAudio/DefaultSub from that profile's preferred languages, but only when the active profile
// actually changes - so a manual change made while on one profile is preserved (no regression of the
// "never silently change a user's default" intent).
public class ProfileLanguageSyncTests
{
    private static CruncharrConfig NewConfig(bool sync = true, string lastProfile = "")
    {
        var c = new CruncharrConfig();
        c.Download.SyncDefaultsFromProfile = sync;
        c.Download.DefaultAudio = "ja-JP";
        c.Download.DefaultSub = "en-US";
        c.Crunchyroll.LastSyncedProfileId = lastProfile;
        return c;
    }

    [Fact]
    public void NewProfile_setsDefaultsAndRecordsProfile()
    {
        var c = NewConfig();
        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "profile-troy", "en-US", "de-DE");

        Assert.True(changed);
        Assert.Equal("en-US", c.Download.DefaultAudio);
        Assert.Equal("de-DE", c.Download.DefaultSub);
        Assert.Equal("profile-troy", c.Crunchyroll.LastSyncedProfileId);
    }

    [Fact]
    public void SameProfile_doesNotClobberManualChange()
    {
        var c = NewConfig(lastProfile: "profile-troy");
        c.Download.DefaultAudio = "ko-KR"; // user manually changed it while on this profile

        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "profile-troy", "en-US", "en-US");

        Assert.False(changed);
        Assert.Equal("ko-KR", c.Download.DefaultAudio); // preserved
    }

    [Fact]
    public void SwitchingProfile_reSyncs()
    {
        var c = NewConfig(lastProfile: "profile-troy");
        c.Download.DefaultAudio = "ko-KR";

        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "profile-risi", "ja-JP", "es-ES");

        Assert.True(changed);
        Assert.Equal("ja-JP", c.Download.DefaultAudio);
        Assert.Equal("es-ES", c.Download.DefaultSub);
        Assert.Equal("profile-risi", c.Crunchyroll.LastSyncedProfileId);
    }

    [Fact]
    public void Disabled_doesNothing()
    {
        var c = NewConfig(sync: false);
        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "profile-troy", "en-US", "en-US");

        Assert.False(changed);
        Assert.Equal("ja-JP", c.Download.DefaultAudio);
        Assert.Equal("", c.Crunchyroll.LastSyncedProfileId);
    }

    [Fact]
    public void EmptyProfileKey_doesNothing()
    {
        var c = NewConfig();
        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "", "en-US", "en-US");
        Assert.False(changed);
        Assert.Equal("ja-JP", c.Download.DefaultAudio);
    }

    [Fact]
    public void EmptyPrefs_recordsProfileButKeepsDefaults()
    {
        var c = NewConfig();
        var changed = CrunchyrollAuthService.ApplyProfileLanguageDefaults(c, "profile-troy", null, "");

        Assert.True(changed); // profile recorded so we don't retry every refresh
        Assert.Equal("ja-JP", c.Download.DefaultAudio); // no preferred audio -> unchanged
        Assert.Equal("profile-troy", c.Crunchyroll.LastSyncedProfileId);
    }
}
