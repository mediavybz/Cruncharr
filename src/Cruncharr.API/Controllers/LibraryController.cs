using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/library")]
public class LibraryController(ISonarrService sonarr, IHistoryService history, CruncharrConfig config, ILogger<LibraryController> logger, ICrunchyrollApiService? api = null) : ControllerBase
{
    [HttpGet("sonarr")]
    public async Task<IActionResult> GetSonarrLibrary(CancellationToken cancellationToken, [FromQuery] bool verifyTitles = true)
    {
        if (!config.Sonarr.Enabled) return Ok(new { Enabled = false, Series = Array.Empty<LibrarySeriesResponse>() });
        try
        {
            var series = await sonarr.GetCurrentSeriesAsync(config.Sonarr, cancellationToken);
            var tracked = await history.GetHistorySeriesAsync();
            var idsBySonarr = tracked.Where(entry => !string.IsNullOrEmpty(entry.SonarrSeriesId) && !string.IsNullOrEmpty(entry.SeriesId))
                .ToLookup(entry => entry.SonarrSeriesId!, entry => entry.SeriesId!);
            var catalogIds = new Dictionary<int, List<string>>();
            var matchingUnavailable = false;
            var matchingFailures = new List<LibraryMatchingFailure>();
            if (api != null && verifyTitles)
            {
                try
                {
                    var index = new SonarrTitleIndex(series);
                    foreach (var entry in await api.GetAllSeriesAsync(cancellationToken: cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrEmpty(entry.Id)) continue;
                        var match = index.FindExact(entry.Title);
                        if (match == null && index.Candidates(entry.Title).Count > 0)
                        {
                            if (entry.EpisodeCount == 0) continue;
                            try { match = await sonarr.ResolveSeriesAsync(entry.Id, entry.Title, config.Sonarr, cancellationToken); }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                matchingUnavailable = true;
                                matchingFailures.Add(new LibraryMatchingFailure(entry.Id, entry.Title));
                                logger.LogWarning(ex, "Could not verify Sonarr identity for {Title}", entry.Title);
                            }
                        }
                        if (match == null) continue;
                        if (!catalogIds.TryGetValue(match.Id, out var ids)) catalogIds[match.Id] = ids = [];
                        ids.Add(entry.Id);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    matchingUnavailable = true;
                    logger.LogWarning(ex, "Could not finish verifying catalog titles against Sonarr");
                }
            }
            return Ok(new
            {
                Enabled = true,
                MatchingUnavailable = matchingUnavailable,
                MatchingFailures = matchingFailures,
                Series = series.Select(item => new LibrarySeriesResponse
                {
                    SonarrSeriesId = item.Id,
                    Title = item.Title ?? "",
                    Titles = new[] { item.Title, item.CleanTitle }.Concat(item.AlternateTitles?.Select(title => title.Title) ?? [])
                        .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    CrunchyrollSeriesIds = idsBySonarr[item.Id.ToString()].Concat(catalogIds.GetValueOrDefault(item.Id) ?? []).Distinct().ToList(),
                    EpisodeFileCount = item.Statistics?.EpisodeFileCount,
                    EpisodeCount = item.Statistics?.EpisodeCount
                }).ToList()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the Sonarr library for Browse");
            return StatusCode(503, new { Message = "Sonarr library status is temporarily unavailable." });
        }
    }
}

public record LibraryMatchingFailure(string SeriesId, string Title);

public class LibrarySeriesResponse
{
    public int SonarrSeriesId { get; set; }
    public string Title { get; set; } = "";
    public List<string> Titles { get; set; } = [];
    public List<string> CrunchyrollSeriesIds { get; set; } = [];
    public int? EpisodeFileCount { get; set; }
    public int? EpisodeCount { get; set; }
}
