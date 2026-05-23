using Newtonsoft.Json;

namespace Cruncharr.Core.Models;

public class StreamError{
    [JsonProperty("error")]
    public string? Error{ get; set; }

    [JsonProperty("activeStreams")]
    public List<ActiveStream> ActiveStreams{ get; set; } = new();

    [JsonIgnore]
    public string? RawJson{ get; set; }

    public static StreamError? FromJson(string json){
        try{
            var error = JsonConvert.DeserializeObject<StreamError>(json);
            if (error != null){
                error.RawJson = json;
            }
            return error;
        } catch{
            return null;
        }
    }

    public bool IsTooManyActiveStreamsError(){
        return Error is "TOO_MANY_ACTIVE_STREAMS" or "TOO_MANY_CONCURRENT_STREAMS";
    }

    public bool IsPlaybackRateLimitError(){
        return Error?.Contains("4294") == true || RawJson?.Contains("4294") == true;
    }

    public bool IsMaturityRatingError(){
        return Error?.Contains("Account maturity rating is lower than video rating") == true ||
               RawJson?.Contains("Account maturity rating is lower than video rating") == true;
    }
}

public class ActiveStream{
    [JsonProperty("deviceSubtype")]
    public string DeviceSubtype{ get; set; } = "";

    [JsonProperty("accountId")]
    public string AccountId{ get; set; } = "";

    [JsonProperty("deviceType")]
    public string DeviceType{ get; set; } = "";

    [JsonProperty("subscription")]
    public string Subscription{ get; set; } = "";

    [JsonProperty("maxKeepAliveSeconds")]
    public int MaxKeepAliveSeconds{ get; set; }

    [JsonProperty("ttl")]
    public int Ttl{ get; set; }

    [JsonProperty("episodeIdentity")]
    public string EpisodeIdentity{ get; set; } = "";

    [JsonProperty("tabId")]
    public string TabId{ get; set; } = "";

    [JsonProperty("country")]
    public string Country{ get; set; } = "";

    [JsonProperty("clientId")]
    public string ClientId{ get; set; } = "";

    [JsonProperty("active")]
    public bool Active{ get; set; }

    [JsonProperty("deviceId")]
    public string DeviceId{ get; set; } = "";

    [JsonProperty("token")]
    public string Token{ get; set; } = "";

    [JsonProperty("assetId")]
    public string AssetId{ get; set; } = "";

    [JsonProperty("sessionType")]
    public string SessionType{ get; set; } = "";

    [JsonProperty("contentId")]
    public string ContentId{ get; set; } = "";

    [JsonProperty("usesStreamLimits")]
    public bool UsesStreamLimits{ get; set; }

    [JsonProperty("playbackType")]
    public string PlaybackType{ get; set; } = "";

    [JsonProperty("pk")]
    public string Pk{ get; set; } = "";

    [JsonProperty("id")]
    public string Id{ get; set; } = "";

    [JsonProperty("createdTimestamp")]
    public long CreatedTimestamp{ get; set; }

    [JsonProperty("lastKeepAliveTimestamp")]
    public long LastKeepAliveTimestamp{ get; set; }
}
