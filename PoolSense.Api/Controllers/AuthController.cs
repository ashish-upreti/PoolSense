using System.DirectoryServices.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoolSense.Api.Contracts;
using PoolSense.Api.Data;
using PoolSense.Api.Options;
using PoolSense.Api.Services;

namespace PoolSense.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ActiveDirectoryOptions _activeDirectoryOptions;
    private readonly AuthOptions _authOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AuthController> _logger;
    private readonly IRsaKeyMaterialProvider _rsaKeyMaterialProvider;
    private readonly IActiveDirectoryAuthService _activeDirectoryAuthService;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ISessionPasswordStore _sessionPasswordStore;
    private readonly IAuthUserRepository _authUserRepository;

    public AuthController(
        IOptions<ActiveDirectoryOptions> activeDirectoryOptions,
        IOptions<AuthOptions> authOptions,
        IWebHostEnvironment environment,
        ILogger<AuthController> logger,
        IRsaKeyMaterialProvider rsaKeyMaterialProvider,
        IActiveDirectoryAuthService activeDirectoryAuthService,
        IJwtTokenService jwtTokenService,
        ISessionPasswordStore sessionPasswordStore,
        IAuthUserRepository authUserRepository)
    {
        _activeDirectoryOptions = activeDirectoryOptions.Value;
        _authOptions = authOptions.Value;
        _environment = environment;
        _logger = logger;
        _rsaKeyMaterialProvider = rsaKeyMaterialProvider;
        _activeDirectoryAuthService = activeDirectoryAuthService;
        _jwtTokenService = jwtTokenService;
        _sessionPasswordStore = sessionPasswordStore;
        _authUserRepository = authUserRepository;
    }

    [HttpGet("pubkey")]
    public IActionResult GetPublicKey()
    {
        if (string.IsNullOrWhiteSpace(_rsaKeyMaterialProvider.PublicKeyPem))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = "Public key encryption is temporarily unavailable on the authentication service."
            });
        }

        return Ok(new
        {
            publicKey = _rsaKeyMaterialProvider.PublicKeyPem
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request, CancellationToken cancellationToken)
    {
        var mutableRequest = request ?? new LoginRequest();
        var attemptedUsername = mutableRequest.Username?.Trim();
        var clientAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_environment.IsDevelopment()
            && !_authOptions.AllowInsecurePasswordFallback
            && !string.IsNullOrWhiteSpace(mutableRequest.Password))
        {
            _logger.LogWarning(
                "Login failed for user '{Username}' from {ClientAddress}: plaintext password submission blocked outside development.",
                attemptedUsername ?? "<empty>",
                clientAddress);

            await RecordFailedLoginAttemptAsync(
                attemptedUsername,
                StatusCodes.Status400BadRequest,
                "Plaintext password submission is not allowed outside development.",
                clientAddress,
                cancellationToken);

            return BadRequest(new
            {
                success = false,
                message = "Plaintext password submission is not allowed outside development. Load the app over HTTPS and retry."
            });
        }

        string? password = mutableRequest.Password;

        if (!string.IsNullOrWhiteSpace(mutableRequest.EncryptedPassword) && string.IsNullOrWhiteSpace(password))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_rsaKeyMaterialProvider.PublicKeyPem))
                {
                    _logger.LogWarning(
                        "Login failed for user '{Username}' from {ClientAddress}: password decryption service unavailable.",
                        attemptedUsername ?? "<empty>",
                        clientAddress);

                    await RecordFailedLoginAttemptAsync(
                        attemptedUsername,
                        StatusCodes.Status503ServiceUnavailable,
                        "Password decryption service is unavailable.",
                        clientAddress,
                        cancellationToken);

                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                    {
                        success = false,
                        message = "Password decryption service is unavailable. Retry with plaintext fallback or load over HTTPS."
                    });
                }

                password = _rsaKeyMaterialProvider.DecryptBase64(mutableRequest.EncryptedPassword!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Login failed for user '{Username}' from {ClientAddress}: failed to decrypt encrypted password.",
                    attemptedUsername ?? "<empty>",
                    clientAddress);

                await RecordFailedLoginAttemptAsync(
                    attemptedUsername,
                    StatusCodes.Status400BadRequest,
                    "Invalid encrypted password",
                    clientAddress,
                    cancellationToken);

                return BadRequest(new
                {
                    success = false,
                    message = "Invalid encrypted password"
                });
            }
        }

        var authResult = await _activeDirectoryAuthService.AuthenticateAsync(new LoginRequest
        {
            Username = mutableRequest.Username,
            Password = password,
            EncryptedPassword = mutableRequest.EncryptedPassword,
            AdDnsName = mutableRequest.AdDnsName,
            BaseDn = mutableRequest.BaseDn,
            Domain = mutableRequest.Domain,
            RememberMe = mutableRequest.RememberMe
        }, cancellationToken);

        if (!authResult.Success || authResult.User is null || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "Login failed for user '{Username}' from {ClientAddress}. StatusCode={StatusCode}, Message='{Message}', Error='{Error}'",
                attemptedUsername ?? "<empty>",
                clientAddress,
                authResult.StatusCode,
                authResult.Message,
                authResult.Error ?? string.Empty);

            await RecordFailedLoginAttemptAsync(
                attemptedUsername,
                authResult.StatusCode,
                authResult.Message,
                clientAddress,
                cancellationToken);

            var payload = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["message"] = authResult.Message
            };

            if (!string.IsNullOrWhiteSpace(authResult.Error))
            {
                payload["error"] = authResult.Error;
            }

            return StatusCode(authResult.StatusCode, payload);
        }

        var rememberMeLifetime = TimeSpan.FromDays(Math.Max(1, _authOptions.RememberMeDays));
        var defaultLifetime = TimeSpan.FromHours(Math.Max(1, _authOptions.SessionHours));
        var sessionLifetime = mutableRequest.RememberMe ? rememberMeLifetime : defaultLifetime;

        var tokenEnvelope = _jwtTokenService.CreateToken(authResult.User, password!, sessionLifetime);
        var allowInsecureTransport = _authOptions.AllowInsecurePasswordFallback && !HttpContext.Request.IsHttps;
        var requireSecureCookie = !allowInsecureTransport;

        Response.Cookies.Append("authToken", tokenEnvelope.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = requireSecureCookie,
            SameSite = requireSecureCookie ? SameSiteMode.None : SameSiteMode.Lax,
            MaxAge = sessionLifetime,
            Expires = DateTimeOffset.UtcNow.Add(sessionLifetime),
            Path = "/",
            IsEssential = true
        });

        await RecordSuccessfulLoginAttemptAsync(authResult.User, clientAddress, cancellationToken);

        _logger.LogInformation(
            "Login succeeded for user '{Username}' (principal '{AuthPrincipal}') from {ClientAddress}. RememberMe={RememberMe}, IsAdmin={IsAdmin}",
            authResult.User.Username,
            authResult.User.AuthPrincipal,
            clientAddress,
            mutableRequest.RememberMe,
            authResult.User.IsAdmin ?? false);

        return Ok(new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = authResult.Message,
            ["user"] = ToUserPayload(authResult.User)
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var token = _jwtTokenService.GetTokenFromRequest(Request);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var principal = _jwtTokenService.ValidateToken(token);
            var jti = principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                ?? principal?.FindFirst("jti")?.Value;
            if (!string.IsNullOrWhiteSpace(jti))
            {
                _sessionPasswordStore.Remove(jti);
            }
        }

        var allowInsecureTransport = _authOptions.AllowInsecurePasswordFallback && !HttpContext.Request.IsHttps;
        var requireSecureCookie = !allowInsecureTransport;

        Response.Cookies.Delete("authToken", new CookieOptions
        {
            HttpOnly = true,
            Secure = requireSecureCookie,
            SameSite = requireSecureCookie ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/"
        });

        return Ok(new
        {
            success = true,
            message = "Logged out"
        });
    }

    [HttpGet("session")]
    public IActionResult GetSession()
    {
        var token = _jwtTokenService.GetTokenFromRequest(Request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized(new
            {
                success = false,
                message = "Unauthorized: missing token"
            });
        }

        var principal = _jwtTokenService.ValidateToken(token);
        if (principal is null)
        {
            return Unauthorized(new
            {
                success = false,
                message = "Unauthorized: invalid or expired token"
            });
        }

        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
            ?? principal.FindFirst("jti")?.Value;
        if (string.IsNullOrWhiteSpace(jti))
        {
            return Unauthorized(new
            {
                success = false,
                message = "Unauthorized: session token invalid"
            });
        }

        if (!_sessionPasswordStore.TryGet(jti, out _))
        {
            if (_sessionPasswordStore.HasDecryptionFailure(jti))
            {
                return StatusCode(
                    StatusCodes.Status410Gone,
                    new
                    {
                        success = false,
                        message = "Session requires re-authentication",
                        errorCode = "SESSION_DECRYPT_FAILED"
                    });
            }

            return Unauthorized(new
            {
                success = false,
                message = "Unauthorized: session has expired"
            });
        }

        var username = GetClaimValue(principal, "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            username = GetClaimValue(principal, ClaimTypes.NameIdentifier);
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            username = GetClaimValue(principal, ClaimTypes.Name);
        }

        var authPrincipal = GetClaimValue(principal, "authPrincipal");
        if (string.IsNullOrWhiteSpace(authPrincipal))
        {
            authPrincipal = username;
        }

        var displayName = GetClaimValue(principal, "displayName");
        var email = GetSessionEmail(principal, username, authPrincipal);

        return Ok(new
        {
            success = true,
            user = new
            {
                username,
                authPrincipal,
                displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
                email,
                groups = Array.Empty<string>(),
                isAdmin = bool.TryParse(principal.FindFirst("isAdmin")?.Value, out var isAdmin) && isAdmin
            }
        });
    }

    [HttpPost("validate-ad")]
    public IActionResult ValidateActiveDirectory([FromBody] ValidateAdRequest? request)
    {
        var ldapUrl = _activeDirectoryOptions.Url;

        if (!string.IsNullOrWhiteSpace(request?.AdDnsName)
            && !string.Equals(request.AdDnsName.Trim(), ldapUrl, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                success = false,
                message = "Custom LDAP endpoints are not allowed"
            });
        }

        try
        {
            var normalizedUrl = ldapUrl.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase)
                ? ldapUrl
                : $"ldap://{ldapUrl}";

            var uri = new Uri(normalizedUrl);
            var identifier = new LdapDirectoryIdentifier(uri.Host, uri.Port > 0 ? uri.Port : 389);

            using var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Anonymous,
                Timeout = TimeSpan.FromSeconds(5)
            };

            connection.SessionOptions.ProtocolVersion = 3;
            connection.Credential = new NetworkCredential(string.Empty, string.Empty);
            connection.Bind();

            return Ok(new
            {
                success = true,
                message = "Successfully connected to Intel Active Directory"
            });
        }
        catch (LdapException ex) when (IsConnectivityFailure(ex))
        {
            _logger.LogWarning(ex, "Cannot connect to Intel Active Directory server");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Cannot connect to Intel Active Directory server. Please check network connectivity and VPN."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Connection test failed"
            });
        }
    }

    private async Task RecordSuccessfulLoginAttemptAsync(AuthenticatedUser user, string clientAddress, CancellationToken cancellationToken)
    {
        try
        {
            await _authUserRepository.RecordSuccessfulLoginAsync(user, clientAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist successful authentication metadata for user '{Username}'.", user.Username);
        }
    }

    private async Task RecordFailedLoginAttemptAsync(string? username, int statusCode, string message, string clientAddress, CancellationToken cancellationToken)
    {
        try
        {
            await _authUserRepository.RecordFailedLoginAsync(username, statusCode, message, clientAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist failed authentication attempt for user '{Username}'.", username ?? string.Empty);
        }
    }

    private static Dictionary<string, object?> ToUserPayload(AuthenticatedUser user)
    {
        var userPayload = new Dictionary<string, object?>
        {
            ["username"] = user.Username,
            ["authPrincipal"] = user.AuthPrincipal,
            ["displayName"] = user.DisplayName,
            ["email"] = user.Email,
            ["groups"] = user.Groups
        };

        if (user.IsAdmin.HasValue)
        {
            userPayload["isAdmin"] = user.IsAdmin.Value;
        }

        return userPayload;
    }

    private static string GetClaimValue(ClaimsPrincipal principal, string claimType)
    {
        return principal.FindFirst(claimType)?.Value?.Trim() ?? string.Empty;
    }

    private static string GetSessionEmail(ClaimsPrincipal principal, string username, string authPrincipal)
    {
        var email = GetClaimValue(principal, "email");
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        email = GetClaimValue(principal, ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        if (authPrincipal.Contains('@'))
        {
            return authPrincipal;
        }

        if (username.Contains('@'))
        {
            return username;
        }

        var cleanUsername = username.Contains('\\')
            ? username.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty
            : username;

        return string.IsNullOrWhiteSpace(cleanUsername) ? string.Empty : $"{cleanUsername}@intel.com";
    }

    private static bool IsConnectivityFailure(LdapException exception)
    {
        return exception.ErrorCode == 81
            || exception.ServerErrorMessage?.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) == true
            || exception.Message.Contains("The LDAP server is unavailable", StringComparison.OrdinalIgnoreCase);
    }
}