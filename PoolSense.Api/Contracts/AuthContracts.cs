namespace PoolSense.Api.Contracts;

public sealed class LoginRequest
{
    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? EncryptedPassword { get; init; }

    public string? AdDnsName { get; init; }

    public string? BaseDn { get; init; }

    public string? Domain { get; init; }

    public bool RememberMe { get; init; }
}

public sealed class ValidateAdRequest
{
    public string? AdDnsName { get; init; }

    public string? BaseDn { get; init; }
}

public sealed class AuthenticatedUser
{
    public string Username { get; init; } = string.Empty;

    public string AuthPrincipal { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public IReadOnlyList<string> Groups { get; init; } = [];

    public bool? IsAdmin { get; init; }
}

public sealed class AuthResult
{
    public bool Success { get; init; }

    public int StatusCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Error { get; init; }

    public AuthenticatedUser? User { get; init; }
}

public sealed class TokenEnvelope
{
    public string Token { get; init; } = string.Empty;

    public string Jti { get; init; } = string.Empty;
}

public sealed class SessionPasswordEntry
{
    public string Password { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
}