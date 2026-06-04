using System.Reflection;
using Cruncharr.API.Services;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase{
    private readonly IQueueService _queueService;
    private readonly ICrunchyrollAuthService _auth;
    private readonly UpdateCheckerService? _updateChecker;

    public HealthController(IQueueService queueService, ICrunchyrollAuthService auth, UpdateCheckerService? updateChecker = null){
        _queueService = queueService;
        _auth = auth;
        _updateChecker = updateChecker;
    }

    /// <summary>
    /// Health check endpoint for *arr integration
    /// </summary>
    [HttpGet]
    public ActionResult<HealthResponse> GetHealth(){
        return Ok(new HealthResponse{
            Status = "healthy",
            Version = GetType().Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.1.0-beta.1",
            Timestamp = DateTimeOffset.UtcNow,
            ActiveDownloads = _queueService.ActiveDownloads,
            HasActiveDownloads = _queueService.HasActiveDownloads,
            AuthStatus = _auth.IsAuthenticated ? "authenticated" : "anonymous"
        });
    }

    /// <summary>
    /// Readiness check for Docker/Kubernetes
    /// </summary>
    [HttpGet("ready")]
    public IActionResult GetReady(){
        return Ok(new { Status = "ready" });
    }

    /// <summary>
    /// Liveness check for Docker/Kubernetes
    /// </summary>
    [HttpGet("live")]
    public IActionResult GetLive(){
        return Ok(new { Status = "alive" });
    }

    /// <summary>
    /// Version and update check endpoint
    /// </summary>
    [HttpGet("version")]
    public ActionResult GetVersion(){
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return Ok(new{
            CurrentVersion = currentVersion?.ToString(),
            LatestVersion = _updateChecker?.LatestVersion,
            UpdateAvailable = _updateChecker?.UpdateAvailable ?? false
        });
    }
}

public class HealthResponse{
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public int ActiveDownloads { get; set; }
    public bool HasActiveDownloads { get; set; }
    public string AuthStatus { get; set; } = "";
}
