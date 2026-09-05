using System.Security.Claims;
using PoolSense.Api.Contracts;

namespace PoolSense.Api.Services;

public interface IJwtTokenService
{
    TokenEnvelope CreateToken(AuthenticatedUser user, string password, TimeSpan? sessionLifetime = null);

    ClaimsPrincipal? ValidateToken(string token);

    string RefreshToken(ClaimsPrincipal principal, DateTimeOffset expiresAtUtc);

    string? GetTokenFromRequest(HttpRequest request);
}