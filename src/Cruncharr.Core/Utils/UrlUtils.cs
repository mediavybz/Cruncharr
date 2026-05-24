namespace Cruncharr.Core.Utils;

public static class UrlUtils{
    public static string ResolveUrl(string baseUrl, string relativeUrl){
        if (Uri.IsWellFormedUriString(relativeUrl, UriKind.Absolute))
            return relativeUrl;

        Uri baseUri;
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri!)){
            baseUri = new Uri("http://example.com");
        }

        Uri resolvedUri = new Uri(baseUri, relativeUrl);
        return resolvedUri.ToString();
    }
}