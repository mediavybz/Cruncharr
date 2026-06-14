using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class MoviesController : ControllerBase
{
    private readonly IMovieService _movieService;
    private readonly ILogger<MoviesController> _logger;

    public MoviesController(IMovieService movieService, ILogger<MoviesController> logger)
    {
        _movieService = movieService;
        _logger = logger;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetMovie(string id, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { Error = "Id is required" });
        try
        {
            var movie = await _movieService.GetMovieAsync(id, locale, forcedLang, cancellationToken);
            if (movie == null)
            {
                return NotFound(new { Error = "Movie not found" });
            }
            return Ok(movie);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get movie {MovieId}", id);
            return StatusCode(500, new { Error = "Failed to get movie", Message = ex.Message });
        }
    }
}

[ApiController]
[Route("api/v1/[controller]")]
public class MusicController : ControllerBase
{
    private readonly IMusicService _musicService;
    private readonly ILogger<MusicController> _logger;

    public MusicController(IMusicService musicService, ILogger<MusicController> logger)
    {
        _musicService = musicService;
        _logger = logger;
    }

    [HttpGet("videos/{id}")]
    public async Task<ActionResult> GetMusicVideo(string id, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { Error = "Id is required" });
        try
        {
            var video = await _musicService.GetMusicVideoAsync(id, locale, forcedLang, cancellationToken);
            if (video == null)
            {
                return NotFound(new { Error = "Music video not found" });
            }
            return Ok(video);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get music video {VideoId}", id);
            return StatusCode(500, new { Error = "Failed to get music video", Message = ex.Message });
        }
    }

    [HttpGet("concerts/{id}")]
    public async Task<ActionResult> GetConcert(string id, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { Error = "Id is required" });
        try
        {
            var concert = await _musicService.GetConcertAsync(id, locale, forcedLang, cancellationToken);
            if (concert == null)
            {
                return NotFound(new { Error = "Concert not found" });
            }
            return Ok(concert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get concert {ConcertId}", id);
            return StatusCode(500, new { Error = "Failed to get concert", Message = ex.Message });
        }
    }

    [HttpGet("artists/{id}")]
    public async Task<ActionResult> GetArtist(string id, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { Error = "Id is required" });
        try
        {
            var artist = await _musicService.GetArtistAsync(id, locale, forcedLang, cancellationToken);
            if (artist == null)
            {
                return NotFound(new { Error = "Artist not found" });
            }
            return Ok(artist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist {ArtistId}", id);
            return StatusCode(500, new { Error = "Failed to get artist", Message = ex.Message });
        }
    }

    [HttpGet("artists/{id}/videos")]
    public async Task<ActionResult> GetArtistVideos(string id, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { Error = "Id is required" });
        try
        {
            var videos = await _musicService.GetArtistVideosAsync(id, locale, forcedLang, cancellationToken);
            return Ok(videos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist videos {ArtistId}", id);
            return StatusCode(500, new { Error = "Failed to get artist videos", Message = ex.Message });
        }
    }

    [HttpGet("featured/{seriesId}")]
    public async Task<ActionResult> GetFeaturedMusicVideos(string seriesId, [FromQuery] string locale = "en-US", [FromQuery] bool forcedLang = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId)) return BadRequest(new { Error = "SeriesId is required" });
        try
        {
            var videos = await _musicService.GetFeaturedMusicVideosAsync(seriesId, locale, forcedLang, cancellationToken);
            return Ok(videos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get featured music videos {SeriesId}", seriesId);
            return StatusCode(500, new { Error = "Failed to get featured music videos", Message = ex.Message });
        }
    }
}

[ApiController]
[Route("api/v1/[controller]")]
public class EncodingController : ControllerBase
{
    private readonly IEncodingService _encodingService;
    private readonly ILogger<EncodingController> _logger;

    public EncodingController(IEncodingService encodingService, ILogger<EncodingController> logger)
    {
        _encodingService = encodingService;
        _logger = logger;
    }

    [HttpGet("presets")]
    public ActionResult GetPresets()
    {
        try
        {
            // The settings dropdown stores the preset NAME, so return names (not the full
            // VideoPreset objects, which the UI was rendering as "[object Object]").
            var presets = _encodingService.GetPresets().Select(p => p.PresetName).ToList();
            return Ok(presets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get encoding presets");
            return StatusCode(500, new { Error = "Failed to get presets", Message = ex.Message });
        }
    }

    [HttpGet("presets/{presetName}")]
    public ActionResult GetPreset(string presetName)
    {
        try
        {
            var preset = _encodingService.GetPreset(presetName);
            if (preset == null)
            {
                return NotFound(new { Error = "Preset not found" });
            }
            return Ok(preset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get preset {PresetName}", presetName);
            return StatusCode(500, new { Error = "Failed to get preset", Message = ex.Message });
        }
    }

    // Full preset objects, each flagged built-in vs custom (for the preset editor).
    [HttpGet("presets/all")]
    public ActionResult GetAllPresets()
    {
        var all = _encodingService.GetPresets()
            .Select(p => new { p.PresetName, p.Codec, p.Resolution, p.FrameRate, p.Crf, p.AdditionalParameters, builtIn = _encodingService.IsBuiltIn(p.PresetName ?? "") })
            .ToList();
        return Ok(all);
    }

    // Create or update a custom preset (built-in names are rejected).
    [HttpPost("presets")]
    public ActionResult AddPreset([FromBody] VideoPreset preset)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.PresetName))
            return BadRequest(new { Error = "PresetName is required" });
        if (_encodingService.IsBuiltIn(preset.PresetName))
            return BadRequest(new { Error = "Cannot overwrite a built-in preset" });
        if (preset.Crf < 0 || preset.Crf > 51)
            return BadRequest(new { Error = "CRF must be between 0 and 51" });
        return _encodingService.AddPreset(preset)
            ? Ok(new { Message = "Preset saved", preset.PresetName })
            : StatusCode(500, new { Error = "Failed to save preset" });
    }

    // Delete a custom preset (built-ins cannot be deleted).
    [HttpDelete("presets/{presetName}")]
    public ActionResult DeletePreset(string presetName)
    {
        if (_encodingService.IsBuiltIn(presetName))
            return BadRequest(new { Error = "Built-in presets cannot be deleted" });
        return _encodingService.RemovePreset(presetName)
            ? NoContent()
            : NotFound(new { Error = "Custom preset not found" });
    }
}
