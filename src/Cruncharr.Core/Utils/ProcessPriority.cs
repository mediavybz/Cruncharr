using System.Diagnostics;

namespace Cruncharr.Core.Utils;

/// <summary>
/// Media tools (ffmpeg, mkvmerge, decrypt) are started below normal priority. A software
/// transcode otherwise takes every core at the same priority as the web app and other
/// containers on the host (Sonarr), and pages took minutes to load while it ran. At lower
/// priority the tools still use all idle CPU; they just yield when something else needs it.
/// Lowering priority needs no privileges (Linux nice / Windows priority class).
/// </summary>
public static class ProcessPriority
{
    /// <summary>Background work that should yield: mux, decrypt, probe, frame extraction (nice 10).</summary>
    public static void Lower(Process? process) => Set(process, ProcessPriorityClass.BelowNormal);

    /// <summary>Long CPU-bound encodes: run only on otherwise idle CPU (nice 19).</summary>
    public static void Idle(Process? process) => Set(process, ProcessPriorityClass.Idle);

    private static void Set(Process? process, ProcessPriorityClass priority)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.PriorityClass = priority;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException)
        {
            // Best effort: the process may already have exited, or the platform may refuse.
        }
    }
}
