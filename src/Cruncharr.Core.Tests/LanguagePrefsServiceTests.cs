using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

public class LanguagePrefsServiceTests
{
    private static LanguagePrefsService Enabled()
    {
        var s = new LanguagePrefsService(path: null);
        s.SetEnabled(true);
        return s;
    }

    [Fact]
    public void Disabled_RecordsNothing_AndNeverSuggests()
    {
        var s = new LanguagePrefsService(path: null); // default disabled
        for (var i = 0; i < 20; i++) s.RecordPick("audio", "en-US");
        Assert.Null(s.GetSuggestion("ja-JP", null));
        Assert.Empty(s.State.AudioCounts);
    }

    [Fact]
    public void SuggestsLeader_OnceItClearsTheMargin()
    {
        var s = Enabled();
        // 4 picks: below the min/margin -> no suggestion yet.
        for (var i = 0; i < 4; i++) s.RecordPick("audio", "en-US");
        Assert.Null(s.GetSuggestion("ja-JP", null));

        // 6 picks vs a ja-JP default with 0 count -> lead of 6 >= margin -> suggest.
        s.RecordPick("audio", "en-US");
        s.RecordPick("audio", "en-US");
        var sug = s.GetSuggestion("ja-JP", null);
        Assert.NotNull(sug);
        Assert.Equal("audio", sug!.Category);
        Assert.Equal("en-US", sug.Locale);
    }

    [Fact]
    public void DoesNotSuggest_WhenLeaderIsAlreadyDefault()
    {
        var s = Enabled();
        for (var i = 0; i < 10; i++) s.RecordPick("audio", "en-US");
        Assert.Null(s.GetSuggestion("en-US", null));
    }

    [Fact]
    public void Decline_SuppressesSuggestionPermanently()
    {
        var s = Enabled();
        for (var i = 0; i < 10; i++) s.RecordPick("audio", "en-US");
        Assert.NotNull(s.GetSuggestion("ja-JP", null));

        s.Decline("audio", "en-US");
        Assert.Null(s.GetSuggestion("ja-JP", null));
    }

    [Fact]
    public void Dismiss_Snoozes_UntilLeadGrowsAgain()
    {
        var s = Enabled();
        for (var i = 0; i < 8; i++) s.RecordPick("audio", "en-US");
        Assert.NotNull(s.GetSuggestion("ja-JP", null));

        s.Dismiss("audio", "en-US"); // snooze at count 8
        Assert.Null(s.GetSuggestion("ja-JP", null));

        // A couple more picks isn't enough to clear the snooze margin...
        s.RecordPick("audio", "en-US");
        s.RecordPick("audio", "en-US");
        Assert.Null(s.GetSuggestion("ja-JP", null));

        // ...but once it grows by the full margin past the snooze, it re-surfaces.
        for (var i = 0; i < 4; i++) s.RecordPick("audio", "en-US");
        Assert.NotNull(s.GetSuggestion("ja-JP", null));
    }

    [Fact]
    public void Reset_ClearsCounts_ButKeepsEnabled()
    {
        var s = Enabled();
        for (var i = 0; i < 10; i++) s.RecordPick("audio", "en-US");
        s.Reset();
        Assert.True(s.Enabled);
        Assert.Empty(s.State.AudioCounts);
        Assert.Null(s.GetSuggestion("ja-JP", null));
    }

    [Fact]
    public void AudioAndSub_TrackedIndependently()
    {
        var s = Enabled();
        for (var i = 0; i < 8; i++) s.RecordPick("sub", "en-US");
        var sug = s.GetSuggestion("ja-JP", "pt-BR");
        Assert.NotNull(sug);
        Assert.Equal("sub", sug!.Category);
        Assert.Equal("en-US", sug.Locale);
    }

    [Fact]
    public void State_ReturnsDeepSnapshot()
    {
        var service = Enabled();
        service.RecordPick("audio", "en-US");
        var snapshot = service.State;

        snapshot.AudioCounts["en-US"] = 99;
        snapshot.AudioDeclined.Add("fr-FR");

        Assert.Equal(1, service.State.AudioCounts["en-US"]);
        Assert.Empty(service.State.AudioDeclined);
    }
}
