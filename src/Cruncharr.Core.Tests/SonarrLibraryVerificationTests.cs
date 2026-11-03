using Cruncharr.API.Services;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Moq;

namespace Cruncharr.Core.Tests;

public sealed class SonarrLibraryVerificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cruncharr-library-" + Guid.NewGuid());
    private readonly Mock<ISonarrService> _sonarr = new();
    private readonly Mock<ICrunchyrollApiService> _api = new();
    private readonly SonarrConfig _config = new() { Host = "sonarr.test", ApiKey = "private-fixture-key" };
    private readonly List<SonarrSeries> _library = [new() { Id = 858, TvdbId = 247971, Title = "We Without Wings" }];
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string CachePath => Path.Combine(_directory, "identities.json");

    public SonarrLibraryVerificationTests()
    {
        _api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SeriesInfo { Id = "G6759W00R", Title = "We, Without Wings - under the innocent sky", EpisodeCount = 12 }
        ]);
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_library[0]);
    }

    private SonarrLibraryVerificationService Create() => new(_sonarr.Object, _api.Object, cachePath: CachePath);

    [Fact]
    public async Task VerifiedAliasesSurviveRestartAndAreIncludedInTheQuickResponse()
    {
        var result = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Equal(858, result.Matches["G6759W00R"]);
        var restored = await Create().GetAsync(_library, _config, false, false, Token);
        Assert.Equal(858, restored.Matches["G6759W00R"]);
        Assert.False(restored.InProgress);
        _api.Verify(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain("private-fixture-key", await File.ReadAllTextAsync(CachePath, Token));
    }

    [Fact]
    public async Task SlowVerificationDoesNotBlockSnapshotsOrRestartForEachBrowser()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SonarrSeries?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var service = Create();
        var first = await service.GetAsync(_library, _config, true, false, Token);
        Assert.True(first.InProgress);
        await entered.Task.WaitAsync(Token);
        var pending = await service.GetAsync(_library, _config, true, false, Token);
        Assert.Contains("G6759W00R", pending.PendingSeriesIds);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(_library, _config, true, true, cancelled.Token));
        release.SetResult(_library[0]);
        var completed = await service.GetAsync(_library, _config, true, true, Token);
        Assert.Equal(858, completed.Matches["G6759W00R"]);
        Assert.Empty(completed.PendingSeriesIds);
        _api.Verify(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("credential")]
    [InlineData("removed")]
    [InlineData("identity")]
    public async Task ChangedSonarrConnectionOrIdentityInvalidatesSavedIdentities(string change)
    {
        await Create().GetAsync(_library, _config, true, true, Token);
        switch (change)
        {
            case "server": _config.Host = "other.test"; break;
            case "credential": _config.ApiKey = "different-key"; break;
            case "removed": _library.Clear(); break;
            case "identity": _library[0].TvdbId++; break;
        }
        Assert.Empty((await Create().GetAsync(_library, _config, false, false, Token)).Matches);
    }

    [Fact]
    public async Task LibraryChangeKeepsVerifiedIdentityVisibleUntilReVerified()
    {
        await Create().GetAsync(_library, _config, true, true, Token);
        // A newly added remake makes the bare title ambiguous: the saved identity must be
        // re-checked, but the badge stays up meanwhile instead of every match vanishing.
        _library.Add(new() { Id = 859, TvdbId = 99999, Title = "We Without Wings (2026)" });
        var carried = await Create().GetAsync(_library, _config, false, false, Token);
        Assert.Equal(858, carried.Matches["G6759W00R"]);
        Assert.Contains("G6759W00R", carried.PendingSeriesIds);

        // Re-verification rejects it (episodes belong to the remake): a verified rejection removes it.
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonarrSeries?)null);
        var verified = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Empty(verified.Matches);
        Assert.Empty(verified.PendingSeriesIds);
    }

    [Fact]
    public async Task VerifiedRejectionIsRememberedAcrossRunsAndRestarts()
    {
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonarrSeries?)null);
        var first = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Empty(first.Matches);
        Assert.Empty(first.PendingSeriesIds);

        // A new instance reloads the cache from disk and must not re-fetch the rejected title.
        var second = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Empty(second.Matches);
        Assert.Empty(second.PendingSeriesIds);
        _sonarr.Verify(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangedLibraryRechecksPreviouslyRejectedAlias()
    {
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonarrSeries?)null);
        await Create().GetAsync(_library, _config, true, true, Token);

        var added = new SonarrSeries { Id = 859, TvdbId = 99999, Title = "We Without Wings (2026)" };
        _library.Add(added);
        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(added);

        var result = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Equal(859, result.Matches["G6759W00R"]);
        _sonarr.Verify(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExpiredIdentityStaysVisibleWhileReVerificationIsPendingOrFailing()
    {
        await Create().GetAsync(_library, _config, true, true, Token);
        var json = await File.ReadAllTextAsync(CachePath, Token);
        var expired = System.Text.RegularExpressions.Regex.Replace(json, "\"ExpiresUtc\":\"[^\"]+\"", "\"ExpiresUtc\":\"2000-01-01T00:00:00Z\"");
        Assert.NotEqual(json, expired);
        await File.WriteAllTextAsync(CachePath, expired, Token);

        Assert.Equal(858, (await Create().GetAsync(_library, _config, false, false, Token)).Matches["G6759W00R"]);

        _sonarr.Setup(s => s.ResolveSeriesAsync("G6759W00R", It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Crunchyroll lists no episodes"));
        var failing = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.Equal(858, failing.Matches["G6759W00R"]);
        Assert.Contains("G6759W00R", failing.PendingSeriesIds);
        Assert.Equal("Crunchyroll lists no episodes", Assert.Single(failing.Failures).Reason);
    }

    [Fact]
    public async Task FileCountsDoNotInvalidateVerifiedIdentity()
    {
        await Create().GetAsync(_library, _config, true, true, Token);
        _library[0].Statistics = new() { EpisodeFileCount = 0 };
        Assert.Equal(858, (await Create().GetAsync(_library, _config, false, false, Token)).Matches["G6759W00R"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableCatalogDoesNotDiscardPreviouslyVerifiedMatches(bool empty)
    {
        await Create().GetAsync(_library, _config, true, true, Token);
        if (empty) _api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        else _api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Offline"));
        var result = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.True(result.Unavailable);
        Assert.Equal(858, result.Matches["G6759W00R"]);
    }

    [Fact]
    public async Task FailedCandidateStaysUnverifiedWhileUnrelatedExactMatchesRemainAvailable()
    {
        _api.Setup(a => a.GetAllSeriesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SeriesInfo { Id = "G6759W00R", Title = "We, Without Wings - under the innocent sky", EpisodeCount = 12 },
            new SeriesInfo { Id = "EXACT", Title = "We Without Wings", EpisodeCount = 12 }
        ]);
        _sonarr.Setup(s => s.ResolveSeriesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SonarrConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Offline"));
        var result = await Create().GetAsync(_library, _config, true, true, Token);
        Assert.True(result.Unavailable);
        Assert.Equal(858, result.Matches["EXACT"]);
        Assert.Contains("G6759W00R", result.PendingSeriesIds);
        Assert.Equal("G6759W00R", Assert.Single(result.Failures).SeriesId);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
