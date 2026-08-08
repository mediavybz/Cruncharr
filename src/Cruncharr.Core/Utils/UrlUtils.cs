namespace Cruncharr.Core.Utils;

public static class UrlUtils
{
    public static string ResolveUrl(string baseUrl, string relativeUrl)
    {
        baseUrl ??= string.Empty;
        relativeUrl ??= string.Empty;

        if (Uri.IsWellFormedUriString(relativeUrl, UriKind.Absolute))
            return relativeUrl;

        if (string.IsNullOrEmpty(baseUrl)) return relativeUrl;
        if (string.IsNullOrEmpty(relativeUrl)) return baseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return relativeUrl;

        return new Uri(baseUri, relativeUrl).ToString();
    }
}
