using System.Security.Claims;
using Microsoft.Extensions.Options;
using PoolSense.Api.Contracts;
using PoolSense.Api.Options;

namespace PoolSense.Api.Services;

public interface ISessionSlidingExpirationService
{
    void ExtendIfNeeded(HttpContext httpContext, string jti, SessionPasswordEntry entry, ClaimsPrincipal principal);
}

/// <summary>
/// Keeps an active session alive by pushing its expiration forward on use, so a session only
/// lapses after a period of inactivity (Auth:InactivityTimeoutDays) rather than a fixed lifetime.
/// </summary>
public sealed class SessionSlidingExpirationService : ISessionSlidingExpirationService
{
    // Only reissue the sliding cookie once this much of the window has elapsed, to avoid a disk write on every request.
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromHours(24);

    private readonly IJwtTokenService _jwtTokenService;
    private readonly ISessionPasswordStore _sessionPasswordStore;
    private readonly AuthOptions _authOptions;

    public SessionSlidingExpirationService(
        IJwtTokenService jwtTokenService,
        ISessionPasswordStore sessionPasswordStore,
        IOptions<AuthOptions> authOptions)
    {
        _jwtTokenService = jwtTokenService;
        _sessionPasswordStore = sessionPasswordStore;
        _authOptions = authOptions.Value;
    }

    public void ExtendIfNeeded(HttpContext httpContext, string jti, SessionPasswordEntry entry, ClaimsPrincipal principal)
    {
        var timeoutWindow = TimeSpan.FromDays(Math.Max(1, _authOptions.InactivityTimeoutDays));
        var remaining = entry.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining >= timeoutWindow - RefreshThreshold)
        {
            return;
        }

        var newExpiresAtUtc = DateTimeOffset.UtcNow.Add(timeoutWindow);
        _sessionPasswordStore.Store(jti, entry.Password, newExpiresAtUtc);

        var refreshedToken = _jwtTokenService.RefreshToken(principal, newExpiresAtUtc);
        var allowInsecureTransport = _authOptions.AllowInsecurePasswordFallback && !httpContext.Request.IsHttps;
        var requireSecureCookie = !allowInsecureTransport;

        httpContext.Response.Cookies.Append("authToken", refreshedToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = requireSecureCookie,
            SameSite = requireSecureCookie ? SameSiteMode.None : SameSiteMode.Lax,
            MaxAge = timeoutWindow,
            Expires = newExpiresAtUtc,
            Path = "/",
            IsEssential = true
        });
    }
}
