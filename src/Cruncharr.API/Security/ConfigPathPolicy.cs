namespace Cruncharr.API.Security;

/// <summary>
/// Confines user-settable file paths (background image, token file, queue file) to the
/// directories Cruncharr already owns, so a config write cannot point the app at
/// arbitrary files elsewhere on the host.
/// </summary>
public static class ConfigPathPolicy
{
    /// <summary>
    /// Anchors a relative path under the config directory. The process works from /app,
    /// which is read-only for the service user, so a bare "Cruncharr/queue.json" must
    /// never be stored as-is.
    /// </summary>
    public static string RootRelative(string path, string configDir)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(configDir)) return path;
        return Path.GetFullPath(Path.Combine(configDir, path));
    }

    /// <summary>
    /// True when <paramref name="path"/> is relative, or is located inside one of
    /// <paramref name="allowedRoots"/>. Roots that are null or blank are ignored.
    /// </summary>
    public static bool IsWithinAllowedRoots(string path, IEnumerable<string?> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (!Path.IsPathRooted(path)) return true;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch
            {
                continue;
            }
            var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.StartsWith(prefix, comparison)) return true;
        }
        return false;
    }
}
