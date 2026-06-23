using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

/// <summary>
/// Adaptive language defaults: learns which audio/subtitle locale the user keeps picking and,
/// when enabled, suggests promoting it to the default. Opt-in, confirm-by-prompt, resettable.
/// State persists on the /config volume (survives updates) via <see cref="ILanguagePrefsService"/>.
/// </summary>
[ApiController]
[Route("api/v1/language-prefs")]
public class LanguagePrefsController : ControllerBase
{
    private readonly ILanguagePrefsService _prefs;
    private readonly CruncharrConfig _config;
    private readonly ILogger<LanguagePrefsController> _logger;
    private static readonly object _saveLock = new();

    public LanguagePrefsController(ILanguagePrefsService prefs, CruncharrConfig config, ILogger<LanguagePrefsController> logger)
    {
        _prefs = prefs;
        _config = config;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult Get()
    {
        var s = _prefs.State;
        var suggestion = _prefs.GetSuggestion(_config.Download?.DefaultAudio, _config.Download?.DefaultSub);
        return Ok(new
        {
            enabled = s.Enabled,
            audioCounts = s.AudioCounts,
            subCounts = s.SubCounts,
            currentAudioDefault = _config.Download?.DefaultAudio,
            currentSubDefault = _config.Download?.DefaultSub,
            suggestion = suggestion == null ? null : new
            {
                category = suggestion.Category,
                locale = suggestion.Locale,
                count = suggestion.Count,
                currentDefaultCount = suggestion.CurrentDefaultCount
            }
        });
    }

    [HttpPost("enabled")]
    public ActionResult SetEnabled([FromBody] EnabledRequest request)
    {
        _prefs.SetEnabled(request?.Enabled ?? false);
        return Ok(new { enabled = _prefs.Enabled });
    }

    [HttpPost("accept")]
    public ActionResult Accept([FromBody] SuggestionRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Locale) || string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(new { Error = "category and locale are required" });

        // Promote the chosen locale to the REAL default setting (visible in Settings, flows into the
        // Add Download pre-select + the muxed default track, and is cleared by config reset).
        if (request.Category == "audio") _config.Download.DefaultAudio = request.Locale;
        else if (request.Category == "sub") _config.Download.DefaultSub = request.Locale;
        else return BadRequest(new { Error = "category must be 'audio' or 'sub'" });

        SaveConfig();
        return Ok(new { accepted = true, request.Category, request.Locale });
    }

    [HttpPost("decline")]
    public ActionResult Decline([FromBody] SuggestionRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Locale) || string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(new { Error = "category and locale are required" });
        _prefs.Decline(request.Category, request.Locale);
        return Ok(new { declined = true });
    }

    [HttpPost("dismiss")]
    public ActionResult Dismiss([FromBody] SuggestionRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Locale) || string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(new { Error = "category and locale are required" });
        _prefs.Dismiss(request.Category, request.Locale);
        return Ok(new { dismissed = true });
    }

    [HttpPost("reset")]
    public ActionResult Reset()
    {
        _prefs.Reset();
        return Ok(new { reset = true });
    }

    private void SaveConfig()
    {
        lock (_saveLock)
        {
            var configPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
            _config.Save(configPath);
            _logger.LogInformation("Saved config after adaptive language accept");
        }
    }

    public class EnabledRequest { public bool Enabled { get; set; } }
    public class SuggestionRequest { public string? Category { get; set; } public string? Locale { get; set; } }
}
