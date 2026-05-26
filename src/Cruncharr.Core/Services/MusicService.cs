using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

public interface IMusicService{
    Task<MusicVideo?> GetMusicVideoAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<MusicVideo?> GetConcertAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<List<MusicVideo>> GetArtistVideosAsync(string artistId, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<ArtistInfo?> GetArtistAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
    Task<List<MusicVideo>> GetFeaturedMusicVideosAsync(string seriesId, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
}

public class MusicService : IMusicService{
    private readonly ICrunchyrollAuthService _auth;
    private readonly HttpClientWrapper _httpClient;
    private readonly ILogger<MusicService>? _logger;

    public MusicService(ICrunchyrollAuthService auth, ILogger<MusicService>? logger = null){
        _auth = auth;
        _httpClient = auth.HttpClient;
        _logger = logger;
    }

    public async Task<MusicVideo?> GetMusicVideoAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default){
        return await ParseMediaByIdAsync($"music/music_videos/{id}", locale, forcedLang, cancellationToken);
    }

    public async Task<MusicVideo?> GetConcertAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default){
        var concert = await ParseMediaByIdAsync($"music/concerts/{id}", locale, forcedLang, cancellationToken);
        if (concert != null){
            concert.EpisodeType = "Concert";
        }
        return concert;
    }

    public async Task<List<MusicVideo>> GetArtistVideosAsync(string artistId, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default){
        if (string.IsNullOrEmpty(artistId)){
            return new List<MusicVideo>();
        }

        var musicVideosTask = FetchMediaListAsync($"music/artists/{artistId}/music_videos", locale, forcedLang, cancellationToken);
        var concertsTask = FetchMediaListAsync($"music/artists/{artistId}/concerts", locale, forcedLang, cancellationToken);

        await Task.WhenAll(musicVideosTask, concertsTask);

        var musicVideos = await musicVideosTask;
        var concerts = await concertsTask;

        foreach (var concert in concerts){
            concert.EpisodeType = "Concert";
        }

        musicVideos.AddRange(concerts);
        return musicVideos;
    }

    public async Task<ArtistInfo?> GetArtistAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default){
        try{
            if (!await _auth.AuthenticateAsync(true, cancellationToken)){
                return null;
            }

            var query = CreateQueryParameters(locale, forcedLang);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrls.Content}/music/artists/{id}?{query}");
            request.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
            if (!isOk){
                _logger?.LogError("Artist request failed: {Error}", error);
                return null;
            }

            var artistList = JsonConvert.DeserializeObject<CrunchyArtistList>(content);
            return artistList?.Data?.FirstOrDefault();
        } catch (Exception ex){
            _logger?.LogError(ex, "Error fetching artist {ArtistId}", id);
            return null;
        }
    }

    public async Task<List<MusicVideo>> GetFeaturedMusicVideosAsync(string seriesId, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default){
        return await FetchMediaListAsync($"music/featured/{seriesId}", locale, forcedLang, cancellationToken);
    }

    private async Task<MusicVideo?> ParseMediaByIdAsync(string endpoint, string locale, bool forcedLang, CancellationToken cancellationToken){
        var mediaList = await FetchMediaListAsync(endpoint, locale, forcedLang, cancellationToken);
        return mediaList.FirstOrDefault();
    }

    private async Task<List<MusicVideo>> FetchMediaListAsync(string endpoint, string locale, bool forcedLang, CancellationToken cancellationToken){
        try{
            if (!await _auth.AuthenticateAsync(true, cancellationToken)){
                return new List<MusicVideo>();
            }

            var query = CreateQueryParameters(locale, forcedLang);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrls.Content}/{endpoint}?{query}");
            request.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
            if (!isOk){
                _logger?.LogError("Music request failed for {Endpoint}: {Error}", endpoint, error);
                return new List<MusicVideo>();
            }

            var videoList = JsonConvert.DeserializeObject<CrunchyMusicVideoList>(content);
            return videoList?.Data ?? new List<MusicVideo>();
        } catch (Exception ex){
            _logger?.LogError(ex, "Error fetching music from {Endpoint}", endpoint);
            return new List<MusicVideo>();
        }
    }

    private static NameValueCollection CreateQueryParameters(string locale, bool forcedLang){
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["preferred_audio_language"] = "ja-JP";
        if (!string.IsNullOrEmpty(locale)){
            query["locale"] = locale;
            if (forcedLang){
                query["force_locale"] = locale;
            }
        }
        return query;
    }
}

public class MusicVideo{
    [JsonProperty("id")]
    public string Id{ get; set; } = "";
    [JsonProperty("title")]
    public string? Title{ get; set; }
    [JsonProperty("episode_type")]
    public string EpisodeType{ get; set; } = "Music Video";
    [JsonProperty("sequence_number")]
    public int SequenceNumber{ get; set; }
    [JsonProperty("description")]
    public string? Description{ get; set; }
    [JsonProperty("images")]
    public CrunchyMusicVideoImages? Images{ get; set; }

    public string GetSeriesTitle() => Title ?? "Music";
    public string GetSeasonTitle() => "";
    public string GetEpisodeTitle() => Title ?? "";
    public string GetSeasonId() => "";
    public string GetSeriesId() => "";
}

public class CrunchyMusicVideoImages{
    [JsonProperty("thumbnail")]
    public List<List<CrunchyImage>>? Thumbnail{ get; set; }
    [JsonProperty("poster_tall")]
    public List<List<CrunchyImage>>? PosterTall{ get; set; }
}

public class CrunchyMusicVideoList{
    [JsonProperty("total")]
    public int Total{ get; set; }
    [JsonProperty("data")]
    public List<MusicVideo>? Data{ get; set; }
}

public class ArtistInfo{
    [JsonProperty("id")]
    public string Id{ get; set; } = "";
    [JsonProperty("name")]
    public string? Name{ get; set; }
    [JsonProperty("description")]
    public string? Description{ get; set; }
    [JsonProperty("images")]
    public CrunchyMusicVideoImages? Images{ get; set; }
}

public class CrunchyArtistList{
    [JsonProperty("total")]
    public int Total{ get; set; }
    [JsonProperty("data")]
    public List<ArtistInfo>? Data{ get; set; }
}
