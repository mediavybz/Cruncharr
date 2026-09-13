using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Cruncharr.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/library")]
public class LibraryController(ISonarrService sonarr, IHistoryService history, CruncharrConfig config, ILogger<LibraryController> logger,
    ICrunchyrollApiService? api = null, SonarrLibraryVerificationService? verification = null) : ControllerBase
{
    [HttpGet("sonarr")]
    public async Task<IActionResult> GetSonarrLibrary(CancellationToken cancellationToken, [FromQuery] bool verifyTitles = true,
        [FromQuery] bool background = false)
    {
        if (!config.Sonarr.Enabled) return Ok(new { Enabled = false, Series = Array.Empty<LibrarySeriesResponse>() });
        try
        {
            var series = await sonarr.GetCurrentSeriesAsync(config.Sonarr, cancellationToken);
            var tracked = await history.GetHistorySeriesAsync();
            var idsBySonarr = tracked.Where(entry => !string.IsNullOrEmpty(entry.SonarrSeriesId) && !string.IsNullOrEmpty(entry.SeriesId))
                .ToLookup(entry => entry.SonarrSeriesId!, entry => entry.SeriesId!);
            var matcher = verification ?? (api == null ? null : new SonarrLibraryVerificationService(sonarr, api));
            var matches = matcher == null ? null : await matcher.GetAsync(series, config.Sonarr, verifyTitles, verifyTitles && !background, cancellationToken);
            var catalogIds = matches?.Matches.ToLookup(p => p.Value, p => p.Key);
            return Ok(new
            {
                Enabled = true,
                MatchingUnavailable = matches?.Unavailable ?? false,
                MatchingFailures = matches?.Failures ?? [],
                MatchingInProgress = matches?.InProgress ?? false,
                PendingSeriesIds = matches?.PendingSeriesIds ?? [],
                Series = series.Select(item => new LibrarySeriesResponse
                {
                    SonarrSeriesId = item.Id,
                    Title = item.Title ?? "",
                    Titles = new[] { item.Title, item.CleanTitle }.Concat(item.AlternateTitles?.Select(title => title.Title) ?? [])
                        .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    CrunchyrollSeriesIds = idsBySonarr[item.Id.ToString()].Concat(catalogIds?[item.Id] ?? []).Distinct().ToList(),
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

public class LibrarySeriesResponse
{
    public int SonarrSeriesId { get; set; }
    public string Title { get; set; } = "";
    public List<string> Titles { get; set; } = [];
    public List<string> CrunchyrollSeriesIds { get; set; } = [];
    public int? EpisodeFileCount { get; set; }
    public int? EpisodeCount { get; set; }
}
