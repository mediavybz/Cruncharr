using Cruncharr.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface ISearchService
{
    Task<List<SeriesInfo>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<SeriesInfo?> GetSeriesAsync(string seriesId, CancellationToken cancellationToken = default);
    Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, CancellationToken cancellationToken = default);
}

public class SearchService : ISearchService
{
    private readonly ILogger<SearchService>? _logger;
    private readonly ICrunchyrollApiService _api;

    public SearchService(ICrunchyrollApiService api, ILogger<SearchService>? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    public Task<List<SeriesInfo>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Searching for: {Query}", query);
        return _api.SearchAsync(query, false, cancellationToken);
    }

    public Task<SeriesInfo?> GetSeriesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting series: {SeriesId}", seriesId);
        return _api.GetSeriesAsync(seriesId, false, cancellationToken: cancellationToken);
    }

    public Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Getting episodes for series: {SeriesId}", seriesId);
        return _api.GetEpisodesAsync(seriesId, useBetaApi: true, cancellationToken);
    }
}
