using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/sonarr")]
public sealed class SonarrController(ISonarrService sonarr, ISonarrAcquisitionService requests, CruncharrConfig config) : ControllerBase
{
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken cancellationToken)
    {
        if (!config.Sonarr.Enabled) return BadRequest(new { Message = "Enable and connect Sonarr first." });
        try { return Ok(await sonarr.GetSetupOptionsAsync(config.Sonarr, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return StatusCode(503, new { ex.Message }); }
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Status() => Ok((await requests.GetStatusAsync()).Select(s => new {
        s.SeriesId, s.Title, s.SonarrSeriesId, s.Status, s.LastError, s.UpdatedUtc,
        s.EpisodeCount, s.EpisodeFileCount, PendingEpisodes = s.PendingEpisodes.Count, PendingImports = s.PendingImports.Count
    }));
}
