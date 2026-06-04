using Cruncharr.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SchedulerController : ControllerBase
{
    private readonly AutoDownloadSchedulerService _scheduler;
    private readonly ILogger<SchedulerController> _logger;

    public SchedulerController(AutoDownloadSchedulerService scheduler, ILogger<SchedulerController> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        try
        {
            return Ok(new
            {
                IsRunning = _scheduler.IsRunning,
                LastRun = _scheduler.LastRun?.ToString("O")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduler status");
            return StatusCode(500, new { Error = "Failed to get scheduler status", Message = ex.Message });
        }
    }

    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger()
    {
        try
        {
            _logger.LogInformation("Manual scheduler trigger requested");
            using var scope = HttpContext.RequestServices.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<Cruncharr.Core.Configuration.CruncharrConfig>();
            await _scheduler.RunCheckAsync(scope.ServiceProvider, config, CancellationToken.None);
            return Ok(new { Message = "Scheduler check triggered successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scheduler trigger failed");
            return StatusCode(500, new { Error = "Scheduler trigger failed", Details = ex.Message });
        }
    }
}
