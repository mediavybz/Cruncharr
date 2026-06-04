using Cruncharr.Core.Configuration;
using Cruncharr.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase{
    private readonly ICrunchyrollAuthService _auth;
    private readonly ILogger<AuthController>? _logger;

    private readonly CruncharrConfig? _config;

    public AuthController(ICrunchyrollAuthService auth, CruncharrConfig? config = null, ILogger<AuthController>? logger = null){
        _auth = auth;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Get current authentication status and profile
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<AuthStatusResponse>> GetStatus(){
        try{
            // Try to refresh token if needed before returning status
            if (_auth.IsAuthenticated){
                try{
                    await _auth.RefreshTokenAsync(_config?.Crunchyroll?.UseBetaApi ?? true);
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Token refresh failed during status check");
                }
            }
            
            // If we have a token but profile is not loaded, fetch it
            if (_auth.Token?.access_token != null && _auth.Profile?.Username == "???"){
                try{
                    await _auth.GetMultiProfileAsync(_config?.Crunchyroll?.UseBetaApi ?? true);
                } catch (Exception ex){
                    _logger?.LogWarning(ex, "Failed to fetch profile during status check");
                }
            }
            
            var profile = _auth.Profile;
            var multiProfile = _auth.MultiProfile;
            
            return Ok(new AuthStatusResponse{
                IsAuthenticated = _auth.IsAuthenticated,
                Username = profile?.Username ?? "",
                HasPremium = profile?.HasPremium ?? false,
                PreferredAudioLanguage = profile?.PreferredContentAudioLanguage ?? "",
                PreferredSubtitleLanguage = profile?.PreferredContentSubtitleLanguage ?? "",
                Avatar = profile?.Avatar,
                MultiProfile = multiProfile?.Profiles?.Select(p => new ProfileDto{
                    ProfileId = p.ProfileId,
                    ProfileName = p.ProfileName,
                    Username = p.Username,
                    IsSelected = p.IsSelected,
                    CanSwitch = p.CanSwitch,
                    IsPinProtected = p.IsPinProtected
                }).ToList() ?? new List<ProfileDto>()
            });
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to get auth status");
            return StatusCode(500, new { Error = "Failed to get auth status", Message = ex.Message });
        }
    }

    /// <summary>
    /// Get available profiles for multi-profile accounts
    /// </summary>
    [HttpGet("profiles")]
    public async Task<ActionResult> GetProfiles(){
        try{
            await _auth.GetMultiProfileAsync(false);
            return Ok(new{
                Profiles = _auth.MultiProfile?.Profiles?.Select(p => new ProfileDto{
                    ProfileId = p.ProfileId,
                    ProfileName = p.ProfileName,
                    Username = p.Username,
                    IsSelected = p.IsSelected,
                    CanSwitch = p.CanSwitch,
                    IsPinProtected = p.IsPinProtected
                }).ToList() ?? new List<ProfileDto>()
            });
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to get profiles");
            return StatusCode(500, new { Error = "Failed to get profiles", Message = ex.Message });
        }
    }

    /// <summary>
    /// Switch to a different profile
    /// </summary>
    [HttpPost("profiles/switch")]
    public async Task<ActionResult> SwitchProfile([FromBody] SwitchProfileRequest request){
        try{
            if (string.IsNullOrEmpty(request.ProfileId)){
                return BadRequest(new { Success = false, Message = "Profile ID is required" });
            }

            var success = await _auth.ChangeProfileAsync(request.ProfileId, false);
            if (success){
                return Ok(new { Success = true, Message = "Profile switched successfully" });
            }
            return BadRequest(new { Success = false, Message = "Failed to switch profile" });
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to switch profile");
            return StatusCode(500, new { Success = false, Message = ex.Message });
        }
    }

    /// <summary>
    /// Test login and return full diagnostics
    /// </summary>
    [HttpPost("login-test")]
    public async Task<ActionResult> LoginTest([FromBody] LoginRequest request){
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password)){
            return BadRequest(new { Success = false, Message = "Email and password are required" });
        }

        try{
            var success = await _auth.LoginAsync(request.Email, request.Password, useBetaApi: _config?.Crunchyroll?.UseBetaApi ?? true);
            return Ok(new { 
                Success = success, 
                Message = success ? "Login successful" : "Login failed",
                Username = _auth.Profile.Username ?? "",
                HasPremium = _auth.Profile.HasPremium,
                UseBetaApi = _config?.Crunchyroll?.UseBetaApi ?? true
            });
        } catch (Exception ex){
            _logger?.LogError(ex, "Login test failed for {Email}", request.Email);
            return Ok(new { 
                Success = false, 
                Message = ex.Message,
                Exception = ex.GetType().Name,
                UseBetaApi = _config?.Crunchyroll?.UseBetaApi ?? true
            });
        }
    }

    /// <summary>
    /// Login with email and password
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request){
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password)){
            return BadRequest(new LoginResponse{
                Success = false,
                Message = "Email and password are required"
            });
        }

        try{
            var success = await _auth.LoginAsync(request.Email, request.Password, useBetaApi: _config?.Crunchyroll?.UseBetaApi ?? true);

            if (success){
                return Ok(new LoginResponse{
                    Success = true,
                    Message = $"Logged in as {_auth.Profile.Username}",
                    Username = _auth.Profile.Username ?? "",
                    HasPremium = _auth.Profile.HasPremium
                });
            } else{
                return Unauthorized(new LoginResponse{
                    Success = false,
                    Message = "Login failed - unknown error"
                });
            }
        } catch (Exception ex){
            _logger?.LogError(ex, "Login failed for {Email}", request.Email);
            return Unauthorized(new LoginResponse{
                Success = false,
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// Logout and clear authentication
    /// </summary>
    [HttpPost("logout")]
    public async Task<ActionResult<LogoutResponse>> Logout(){
        try{
            await _auth.LogoutAsync();
            return Ok(new LogoutResponse{
                Success = true,
                Message = "Logged out successfully"
            });
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to logout");
            return StatusCode(500, new LogoutResponse{
                Success = false,
                Message = ex.Message
            });
        }
    }
    
    /// <summary>
    /// Fetch the Base64-encoded client token from Crunchyroll's JS bundle
    /// </summary>
    [HttpGet("client-token")]
    public async Task<ActionResult> GetClientToken(){
        try{
            var token = await _auth.GetBase64EncodedTokenAsync();
            if (string.IsNullOrEmpty(token)){
                return StatusCode(500, new { Error = "Failed to extract client token" });
            }
            return Ok(new { Token = token });
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to fetch client token");
            return StatusCode(500, new { Error = "Failed to fetch client token", Message = ex.Message });
        }
    }
}

public class AuthStatusResponse{
    public bool IsAuthenticated { get; set; }
    public string Username { get; set; } = "";
    public bool HasPremium { get; set; }
    public string PreferredAudioLanguage { get; set; } = "";
    public string PreferredSubtitleLanguage { get; set; } = "";
    public string? Avatar { get; set; }
    public List<ProfileDto> MultiProfile { get; set; } = new();
}

public class ProfileDto{
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public string? Username { get; set; }
    public bool IsSelected { get; set; }
    public bool CanSwitch { get; set; }
    public bool IsPinProtected { get; set; }
}

public class SwitchProfileRequest{
    public string ProfileId { get; set; } = "";
}

public class LoginRequest{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class LoginResponse{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Username { get; set; } = "";
    public bool HasPremium { get; set; }
}

public class LogoutResponse{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
