using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PoolSense.Api.Contracts;
using PoolSense.Api.Options;

namespace PoolSense.Api.Services;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly AuthOptions _authOptions;
    private readonly ISessionPasswordStore _sessionPasswordStore;
    private readonly byte[] _secret;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public JwtTokenService(
        IOptions<AuthOptions> authOptions,
        ISessionPasswordStore sessionPasswordStore,
        IHostEnvironment hostEnvironment,
        ILogger<JwtTokenService> logger)
    {
        _authOptions = authOptions.Value;
        _sessionPasswordStore = sessionPasswordStore;

        if (string.IsNullOrWhiteSpace(_authOptions.JwtSecret))
        {
            var devFallbackSecret = LoadOrCreatePersistentFallbackSecret(hostEnvironment.ContentRootPath);
            logger.LogWarning(
                "Auth:JwtSecret is empty in environment {EnvironmentName}. Using a machine-local fallback key stored on disk. Configure a strong secret via environment-specific secret storage before production use.",
                hostEnvironment.EnvironmentName);
            _secret = Encoding.UTF8.GetBytes(devFallbackSecret);
            return;
        }

        _secret = Encoding.UTF8.GetBytes(_authOptions.JwtSecret);
    }

    public TokenEnvelope CreateToken(AuthenticatedUser user, string password, TimeSpan? sessionLifetime = null)
    {
        var jti = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var effectiveLifetime = sessionLifetime ?? TimeSpan.FromHours(_authOptions.SessionHours);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(effectiveLifetime);
        var isAdmin = user.IsAdmin ?? false;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("username", user.Username),
                new Claim("authPrincipal", user.AuthPrincipal),
                new Claim("email", user.Email),
                new Claim("displayName", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
                new Claim("isAdmin", isAdmin.ToString().ToLowerInvariant()),
                new Claim(ClaimTypes.NameIdentifier, user.Username),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, isAdmin ? "Admin" : "User"),
                new Claim(JwtRegisteredClaimNames.Jti, jti)
            ]),
            Expires = expiresAtUtc.UtcDateTime,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_secret), SecurityAlgorithms.HmacSha256)
        };

        var token = _tokenHandler.CreateToken(descriptor);
        var serializedToken = _tokenHandler.WriteToken(token);

        _sessionPasswordStore.Store(jti, password, expiresAtUtc);

        return new TokenEnvelope
        {
            Token = serializedToken,
            Jti = jti
        };
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            return _tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_secret),
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "username",
                RoleClaimType = ClaimTypes.Role
            }, out _);
        }
        catch
        {
            return null;
        }
    }

    public string? GetTokenFromRequest(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader[7..].Trim();
        }

        return request.Cookies.TryGetValue("authToken", out var cookieToken) ? cookieToken : null;
    }

    private static string LoadOrCreatePersistentFallbackSecret(string contentRootPath)
    {
        var authStateDirectory = Path.Combine(contentRootPath, "artifacts", "auth-state");
        var secretFilePath = Path.Combine(authStateDirectory, "jwt-fallback-secret.txt");

        Directory.CreateDirectory(authStateDirectory);

        if (File.Exists(secretFilePath))
        {
            var existing = File.ReadAllText(secretFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        File.WriteAllText(secretFilePath, generated);
        return generated;
    }
}