using Cruncharr.API.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

/// <summary>
/// Runtime diagnostics: recent in-memory logs for troubleshooting downloads and
/// API behavior without needing shell access to the container.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly InMemoryLogStore _logStore;

    public DiagnosticsController(InMemoryLogStore logStore)
    {
        _logStore = logStore;
    }

    /// <summary>
    /// Recent log entries (most recent first).
    /// </summary>
    /// <param name="level">Minimum level: Trace|Debug|Information|Warning|Error|Critical.</param>
    /// <param name="category">Substring match on the logger category (e.g. "DownloadService").</param>
    /// <param name="contains">Substring match on the message or exception text.</param>
    /// <param name="limit">Max entries to return (default 200).</param>
    [HttpGet("logs")]
    public ActionResult<IEnumerable<LogEntry>> GetLogs(
        [FromQuery] string? level = null,
        [FromQuery] string? category = null,
        [FromQuery] string? contains = null,
        [FromQuery] int limit = 200)
    {
        return Ok(_logStore.Query(level, category, contains, limit));
    }

    /// <summary>
    /// Convenience: recent download-related log entries (DownloadService category).
    /// </summary>
    [HttpGet("logs/download")]
    public ActionResult<IEnumerable<LogEntry>> GetDownloadLogs([FromQuery] int limit = 200)
    {
        return Ok(_logStore.Query(category: "DownloadService", limit: limit));
    }

    /// <summary>
    /// Convenience: recent warnings and errors across all categories.
    /// </summary>
    [HttpGet("logs/errors")]
    public ActionResult<IEnumerable<LogEntry>> GetErrorLogs([FromQuery] int limit = 200)
    {
        return Ok(_logStore.Query(minLevel: "Warning", limit: limit));
    }

    /// <summary>
    /// Clear the in-memory log buffer.
    /// </summary>
    [HttpDelete("logs")]
    public IActionResult ClearLogs()
    {
        _logStore.Clear();
        return NoContent();
    }
}
