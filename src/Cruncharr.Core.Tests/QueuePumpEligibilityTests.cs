using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Xunit;

namespace Cruncharr.Core.Tests;

// GUARD — the auto-download pump must NOT restart a Paused (or Cancelled) item. With
// AutoDownload on, an earlier bug had the pump immediately re-start a download the moment it
// was paused, so Pause did nothing. Only an explicit Resume (-> Queued) may requeue.
public class QueuePumpEligibilityTests
{
    private static DownloadProgress P(DownloadState state) => new() { State = state };

    [Fact]
    public void Paused_IsNotAutoStartEligible()
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(DownloadState.Paused)));
    }

    [Fact]
    public void Cancelled_IsNotAutoStartEligible()
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(DownloadState.Cancelled)));
    }

    [Theory]
    [InlineData(DownloadState.Downloading)]
    [InlineData(DownloadState.Processing)]
    [InlineData(DownloadState.Done)]
    [InlineData(DownloadState.Error)]
    public void TerminalOrInFlight_IsNotAutoStartEligible(DownloadState state)
    {
        Assert.False(QueueService.IsAutoStartEligibleState(P(state)));
    }

    [Fact]
    public void Queued_IsAutoStartEligible()
    {
        Assert.True(QueueService.IsAutoStartEligibleState(P(DownloadState.Queued)));
    }
}
