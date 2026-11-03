using System.Diagnostics;
using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Tests;

// A software transcode ran at normal priority on every core, starving the web app and Sonarr so
// pages took minutes to load. Media tools now run below normal priority.
public class ProcessPriorityTests
{
    [Fact]
    public void MediaProcesses_RunBelowNormalPriority()
    {
        using var lowered = StartIdleProcess();
        using var idle = StartIdleProcess();
        try
        {
            ProcessPriority.Lower(lowered);
            ProcessPriority.Idle(idle);

            lowered.Refresh();
            idle.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, lowered.PriorityClass);
            Assert.Equal(ProcessPriorityClass.Idle, idle.PriorityClass);
        }
        finally
        {
            foreach (var process in new[] { lowered, idle })
                if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void ExitedOrMissingProcess_IsIgnored()
    {
        using var exited = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        exited.WaitForExit();
        ProcessPriority.Lower(exited);
        ProcessPriority.Idle(null);
    }

    private static Process StartIdleProcess()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.CreateNoWindow = true;
        return Process.Start(start)!;
    }
}
