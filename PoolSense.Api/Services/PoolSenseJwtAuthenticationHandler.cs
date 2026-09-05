using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services;

public sealed class PoolSenseJwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PoolSenseJwt";

    private readonly IJwtTokenService _jwtTokenService;
    private readonly ISessionPasswordStore _sessionPasswordStore;
    private readonly ISessionSlidingExpirationService _slidingExpirationService;

    public PoolSenseJwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IJwtTokenService jwtTokenService,
        ISessionPasswordStore sessionPasswordStore,
        ISessionSlidingExpirationService slidingExpirationService)
        : base(options, logger, encoder)
    {
        _jwtTokenService = jwtTokenService;
        _sessionPasswordStore = sessionPasswordStore;
        _slidingExpirationService = slidingExpirationService;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = _jwtTokenService.GetTokenFromRequest(Request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = _jwtTokenService.ValidateToken(token);
        if (principal is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token."));
        }

        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
            ?? principal.FindFirst("jti")?.Value;
        if (string.IsNullOrWhiteSpace(jti))
        {
            return Task.FromResult(AuthenticateResult.Fail("Session token is missing a token id."));
        }

        if (!_sessionPasswordStore.TryGet(jti, out var entry) || entry is null)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                _sessionPasswordStore.HasDecryptionFailure(jti)
                    ? "Session requires re-authentication."
                    : "Session has expired."));
        }

        _slidingExpirationService.ExtendIfNeeded(Context, jti, entry, principal);

        var identity = principal.Identity as ClaimsIdentity;
        if (identity is not null && string.IsNullOrWhiteSpace(identity.AuthenticationType))
        {
            identity = new ClaimsIdentity(identity.Claims, SchemeName, "username", ClaimTypes.Role);
            principal = new ClaimsPrincipal(identity);
        }

        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        return Response.WriteAsJsonAsync(new
        {
            success = false,
            message = "Unauthorized: authentication is required"
        });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        return Response.WriteAsJsonAsync(new
        {
            success = false,
            message = "Forbidden: insufficient permissions"
        });
    }
}