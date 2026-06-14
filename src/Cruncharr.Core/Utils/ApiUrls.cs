namespace Cruncharr.Core.Utils;

public static class ApiUrls
{
    public static readonly string ApiBeta = "https://beta-api.crunchyroll.com";
    public static readonly string ApiN = "https://www.crunchyroll.com";

    // Auth endpoints always use beta API (matching upstream behavior)
    public static string Auth => ApiBeta + "/auth/v1/token";
    public static string Profile => ApiBeta + "/accounts/v1/me/profile";
    public static string MultiProfile => ApiBeta + "/accounts/v1/me/multiprofile";
    public static string Subscription => ApiBeta + "/subs/v3/subscriptions/";

    // Content endpoints can switch between beta and non-beta
    public static string CmsToken(bool useBetaApi) => (useBetaApi ? ApiBeta : ApiN) + "/index/v2";
    public static string Search(bool useBetaApi) => (useBetaApi ? ApiBeta : ApiN) + "/content/v2/discover/search";
    public static string Browse(bool useBetaApi) => (useBetaApi ? ApiBeta : ApiN) + "/content/v2/discover/browse";
    public static string Cms(bool useBetaApi) => (useBetaApi ? ApiBeta : ApiN) + "/content/v2/cms";
    public static string Content(bool useBetaApi) => (useBetaApi ? ApiBeta : ApiN) + "/content/v2";
    public static string Playback => "https://cr-play-service.prd.crunchyrollsvc.com/v3";
    public static readonly string WidevineLicenceUrl = "https://www.crunchyroll.com/license/v1/license/widevine";
    public static readonly string FirefoxUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:137.0) Gecko/20100101 Firefox/137.0";
    public static readonly string Anilist = "https://graphql.anilist.co";
}
