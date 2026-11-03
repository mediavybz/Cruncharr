namespace Cruncharr.API.Security;

/// <summary>
/// Request-level guards applied by the middleware pipeline.
/// </summary>
public static class RequestGuards
{
    /// <summary>
    /// True when a state-changing /api request was issued by another site's page.
    /// Browsers attach <c>Sec-Fetch-Site</c> to every request; <c>same-origin</c> and
    /// <c>none</c> (address bar, bookmarks) are the UI's own traffic. Anything else is a
    /// cross-site form post or script, the classic CSRF vector, unless its Origin is one
    /// explicitly allowed through CORS_ORIGINS. Requests without the header (curl,
    /// scripts, non-browser clients) are never blocked.
    /// </summary>
    public static bool IsCrossSiteStateChange(HttpRequest request, IReadOnlySet<string> allowedOrigins)
    {
        var method = request.Method;
        var path = request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsOptions(method))
            return false;
        // Diagnostics logs expose stack traces and file paths, so even reads of them are
        // limited to the UI's own origin.
        var sensitiveRead = path.StartsWith("/api/v1/diagnostics", StringComparison.OrdinalIgnoreCase);
        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) && !sensitiveRead)
            return false;

        var site = request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        if (string.IsNullOrEmpty(site))
            return false;
        if (site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) ||
            site.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;

        var origin = request.Headers.Origin.FirstOrDefault()?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin))
            return false;

        return true;
    }

    /// <summary>
    /// Normalizes the CORS_ORIGINS list for <see cref="IsCrossSiteStateChange"/>.
    /// </summary>
    public static IReadOnlySet<string> NormalizeOrigins(IEnumerable<string> origins)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in origins)
        {
            var trimmed = origin?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(trimmed)) set.Add(trimmed);
        }
        return set;
    }
}
