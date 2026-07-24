using System.Reflection;
using Cruncharr.API.Services;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IQueueService _queueService;
    private readonly ICrunchyrollAuthService _auth;
    private readonly UpdateCheckerService? _updateChecker;

    public HealthController(IQueueService queueService, ICrunchyrollAuthService auth, UpdateCheckerService? updateChecker = null)
    {
        _queueService = queueService;
        _auth = auth;
        _updateChecker = updateChecker;
    }

    /// <summary>
    /// Health check endpoint for *arr integration
    /// </summary>
    [HttpGet]
    public ActionResult<HealthResponse> GetHealth()
    {
        try
        {
            return Ok(new HealthResponse
            {
                Status = "healthy",
                Version = GetType().Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.62",
                Timestamp = DateTimeOffset.UtcNow,
                ActiveDownloads = _queueService.ActiveDownloads,
                HasActiveDownloads = _queueService.HasActiveDownloads,
                AuthStatus = _auth.IsAuthenticated ? "authenticated" : "anonymous"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = "Health check failed", Message = ex.Message });
        }
    }

    /// <summary>
    /// Readiness check for Docker/Kubernetes
    /// </summary>
    [HttpGet("ready")]
    public IActionResult GetReady()
    {
        try
        {
            return Ok(new { Status = "ready" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = "Readiness check failed", Message = ex.Message });
        }
    }

    [HttpGet("live")]
    public IActionResult GetLive()
    {
        try
        {
            return Ok(new { Status = "alive" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = "Liveness check failed", Message = ex.Message });
        }
    }

    [HttpGet("version")]
    public ActionResult GetVersion()
    {
        try
        {
            var currentVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            return Ok(new
            {
                CurrentVersion = currentVersion,
                LatestVersion = _updateChecker?.LatestVersion,
                UpdateAvailable = _updateChecker?.UpdateAvailable ?? false
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = "Version check failed", Message = ex.Message });
        }
    }
}

public class HealthResponse
{
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public int ActiveDownloads { get; set; }
    public bool HasActiveDownloads { get; set; }
    public string AuthStatus { get; set; } = "";
}
