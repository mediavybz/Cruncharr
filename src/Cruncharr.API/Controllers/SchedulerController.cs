using Cruncharr.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SchedulerController : ControllerBase
{
    private readonly ILogger<SchedulerController> _logger;
    private readonly ScheduledDownloadsService _subscriptions;

    public SchedulerController(ILogger<SchedulerController> logger, ScheduledDownloadsService subscriptions)
    {
        _logger = logger;
        _subscriptions = subscriptions;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        try
        {
            return Ok(_subscriptions.GetStatus());
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
            await _subscriptions.RunCheckAsync(true, HttpContext.RequestAborted);
            return Ok(new { Message = "Scheduler check triggered successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scheduler trigger failed");
            return StatusCode(500, new { Error = "Scheduler trigger failed", Details = ex.Message });
        }
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Configure(SchedulerSettingsRequest request)
    {
        try
        {
            await _subscriptions.ConfigureAsync(request.Enabled, request.IntervalMinutes, HttpContext.RequestAborted);
            return Ok(_subscriptions.GetStatus());
        }
        catch (ArgumentException ex) { return BadRequest(new { Message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpPut("subscriptions/{seriesId}")]
    public async Task<IActionResult> Subscribe(string seriesId, SubscriptionRequest request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(seriesId, "^[A-Za-z0-9_-]{1,64}$"))
            return BadRequest(new { Message = "Invalid series ID" });
        try
        {
            await _subscriptions.SubscribeAsync(seriesId, request.Enabled, HttpContext.RequestAborted);
            return Ok(_subscriptions.GetStatus());
        }
        catch (ArgumentException ex) { return BadRequest(new { Message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpDelete("subscriptions/{seriesId}")]
    public async Task<IActionResult> Unsubscribe(string seriesId)
    {
        await _subscriptions.RemoveAsync(seriesId, HttpContext.RequestAborted);
        return NoContent();
    }
}

public record SchedulerSettingsRequest(bool Enabled, int IntervalMinutes);
public record SubscriptionRequest(bool Enabled);
