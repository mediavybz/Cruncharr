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

public interface IMovieService
{
    Task<MovieInfo?> GetMovieAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default);
}

public class MovieService : IMovieService
{
    private readonly ICrunchyrollAuthService _auth;
    private readonly HttpClientWrapper _httpClient;
    private readonly ILogger<MovieService>? _logger;

    public MovieService(ICrunchyrollAuthService auth, ILogger<MovieService>? logger = null)
    {
        _auth = auth;
        _httpClient = auth.HttpClient;
        _logger = logger;
    }

    public async Task<MovieInfo?> GetMovieAsync(string id, string locale = "en-US", bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _auth.AuthenticateAsync(true, cancellationToken))
            {
                _logger?.LogWarning("Authentication failed for movie request");
                return null;
            }

            var query = HttpUtility.ParseQueryString(string.Empty);
            query["preferred_audio_language"] = "ja-JP";
            if (!string.IsNullOrEmpty(locale))
            {
                query["locale"] = locale;
                if (forcedLang)
                {
                    query["force_locale"] = locale;
                }
            }

            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrls.Cms}/objects/{id}?{query}");
            request.Headers.Add("Authorization", $"Bearer {_auth.Token?.access_token}");

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request);
            if (!isOk || string.IsNullOrEmpty(content))
            {
                _logger?.LogError("Movie request failed: {Error}", error);
                return null;
            }

            var movieList = JsonConvert.DeserializeObject<CrunchyMovieList>(content);
            if (movieList?.Total < 1 || movieList?.Data == null)
            {
                return null;
            }

            var movie = movieList.Data.FirstOrDefault();
            if (movie == null || movie.Type != "movie")
            {
                return null;
            }

            return new MovieInfo
            {
                Id = movie.Id,
                Title = movie.Title ?? "",
                Description = movie.Description,
                AudioLocale = movie.AudioLocale,
                IsSubbed = movie.IsSubbed,
                IsDubbed = movie.IsDubbed,
                ThumbnailUrl = movie.Images?.Thumbnail?.FirstOrDefault()?.FirstOrDefault()?.Source,
                CoverArtUrl = movie.Images?.Thumbnail?.FirstOrDefault()?.LastOrDefault()?.Source
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error fetching movie {MovieId}", id);
            return null;
        }
    }
}

public class MovieInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? AudioLocale { get; set; }
    public bool IsSubbed { get; set; }
    public bool IsDubbed { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CoverArtUrl { get; set; }
}

public class CrunchyMovieList
{
    [JsonProperty("total")]
    public int Total { get; set; }
    [JsonProperty("data")]
    public List<CrunchyMovie>? Data { get; set; }
}

public class CrunchyMovie
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";
    [JsonProperty("title")]
    public string? Title { get; set; }
    [JsonProperty("description")]
    public string? Description { get; set; }
    [JsonProperty("type")]
    public string Type { get; set; } = "";
    [JsonProperty("audio_locale")]
    public string? AudioLocale { get; set; }
    [JsonProperty("is_subbed")]
    public bool IsSubbed { get; set; }
    [JsonProperty("is_dubbed")]
    public bool IsDubbed { get; set; }
    [JsonProperty("images")]
    public CrunchyMovieImages? Images { get; set; }
}

public class CrunchyMovieImages
{
    [JsonProperty("thumbnail")]
    public List<List<CrunchyImage>>? Thumbnail { get; set; }
}

public class CrunchyImage
{
    [JsonProperty("source")]
    public string Source { get; set; } = "";
    [JsonProperty("height")]
    public int Height { get; set; }
    [JsonProperty("width")]
    public int Width { get; set; }
    [JsonProperty("type")]
    public string Type { get; set; } = "";
}
