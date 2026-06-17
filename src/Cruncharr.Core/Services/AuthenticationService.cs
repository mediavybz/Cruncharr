using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface IAuthenticationService
{
    Task<bool> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync();
    bool IsAuthenticated { get; }
    string? Username { get; }
}

public class AuthenticationService : IAuthenticationService
{
    private readonly ILogger<AuthenticationService>? _logger;
    private readonly ICrunchyrollAuthService _crAuth;

    public bool IsAuthenticated => _crAuth.IsAuthenticated;
    public string? Username => _crAuth.Profile.Username;

    public AuthenticationService(ICrunchyrollAuthService crAuth, ILogger<AuthenticationService>? logger = null)
    {
        _crAuth = crAuth;
        _logger = logger;
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Attempting Crunchyroll login"); // email omitted: logs are diagnostics-readable
        return await _crAuth.LoginAsync(email, password, false, cancellationToken);
    }

    public Task LogoutAsync()
    {
        return _crAuth.LogoutAsync();
    }
}
