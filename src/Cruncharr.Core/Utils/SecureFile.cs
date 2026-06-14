using System;
using System.IO;

namespace Cruncharr.Core.Utils;

/// <summary>
/// Helpers for writing files that contain secrets (credentials, tokens).
/// </summary>
public static class SecureFile
{
    /// <summary>
    /// Restrict a file to owner read/write only (chmod 600 equivalent) on Unix.
    /// No-op on Windows (ACL inheritance from the parent dir applies) and never
    /// throws - permission hardening is best-effort.
    /// </summary>
    public static void Restrict(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return;
            if (!File.Exists(path)) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort: a filesystem that rejects chmod (some mounts) must not
            // break saving the file.
        }
    }
}
