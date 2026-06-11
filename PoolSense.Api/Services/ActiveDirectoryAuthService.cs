using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;
using PoolSense.Api.Contracts;
using PoolSense.Api.Options;

namespace PoolSense.Api.Services;

public sealed class ActiveDirectoryAuthService : IActiveDirectoryAuthService
{
    private readonly ActiveDirectoryOptions _options;
    private readonly ILogger<ActiveDirectoryAuthService> _logger;

    public ActiveDirectoryAuthService(IOptions<ActiveDirectoryOptions> options, ILogger<ActiveDirectoryAuthService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<AuthResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var username = request.Username?.Trim();
        var password = request.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(new AuthResult
            {
                Success = false,
                StatusCode = StatusCodes.Status400BadRequest,
                Message = "Username and password are required"
            });
        }

        var ldapUrl = string.IsNullOrWhiteSpace(request.AdDnsName) ? _options.Url : request.AdDnsName!;
        var baseDn = string.IsNullOrWhiteSpace(request.BaseDn) ? _options.BaseDn : request.BaseDn!;
        var domain = string.IsNullOrWhiteSpace(request.Domain) ? _options.Domain : request.Domain!;
        var cleanUsername = ExtractCleanUsername(username);
        var authFormats = BuildAdAuthFormats(username, cleanUsername, domain);

        try
        {
            foreach (var authFormat in authFormats)
            {
                using var connection = CreateConnection(ldapUrl, authFormat, password);

                try
                {
                    connection.Bind();
                }
                catch (LdapException ex) when (IsInvalidCredential(ex))
                {
                    continue;
                }
                catch (LdapException ex) when (IsHostUnavailable(ex))
                {
                    return Task.FromResult(new AuthResult
                    {
                        Success = false,
                        StatusCode = StatusCodes.Status503ServiceUnavailable,
                        Message = "Directory service is currently unavailable. Please try again later.",
                        Error = ex.Message
                    });
                }

                var authPrincipal = NormalizeWindowsUsername(authFormat) ?? authFormat;
                return Task.FromResult(AuthenticateAfterBind(connection, baseDn, cleanUsername, authPrincipal));
            }

            _logger.LogWarning("All authentication methods failed for {Username}", username);
            return Task.FromResult(new AuthResult
            {
                Success = false,
                StatusCode = StatusCodes.Status401Unauthorized,
                Message = "Invalid Intel credentials. Please check your username and password."
            });
        }
        catch (LdapException ex) when (IsDnsFailure(ex))
        {
            return Task.FromResult(new AuthResult
            {
                Success = false,
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                Message = "Cannot reach corporate directory server from this network. Connect to VPN/corporate network and try again.",
                Error = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server error during authentication");
            return Task.FromResult(new AuthResult
            {
                Success = false,
                StatusCode = StatusCodes.Status500InternalServerError,
                Message = "Server error during authentication. Please try again.",
                Error = ex.Message
            });
        }
    }

    private AuthResult AuthenticateAfterBind(LdapConnection connection, string baseDn, string cleanUsername, string authPrincipal)
    {
        try
        {
            var request = new SearchRequest(
                baseDn,
                $"(sAMAccountName={EscapeLdapFilter(cleanUsername)})",
                SearchScope.Subtree,
                "memberOf",
                "displayName",
                "mail",
                "cn",
                "sAMAccountName");

            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0)
            {
                return new AuthResult
                {
                    Success = true,
                    StatusCode = StatusCodes.Status200OK,
                    Message = "Authentication successful",
                    User = new AuthenticatedUser
                    {
                        Username = cleanUsername,
                        AuthPrincipal = authPrincipal,
                        DisplayName = cleanUsername,
                        Email = $"{cleanUsername}@intel.com",
                        Groups = []
                    }
                };
            }

            var entry = response.Entries[0];
            var displayName = GetAttributeValue(entry, "displayName")
                ?? GetAttributeValue(entry, "cn")
                ?? cleanUsername;
            var email = GetAttributeValue(entry, "mail") ?? $"{cleanUsername}@intel.com";
            var groups = GetAttributeValues(entry, "memberOf");

            if (!CheckGroupAccess(groups))
            {
                return new AuthResult
                {
                    Success = false,
                    StatusCode = StatusCodes.Status403Forbidden,
                    Message = "Access denied. You are not authorized to use this application. Please contact your administrator."
                };
            }

            var isAdmin = _options.AdminGroupNames.Any(adminName =>
                groups.Any(group => group.Contains(adminName, StringComparison.OrdinalIgnoreCase)));

            return new AuthResult
            {
                Success = true,
                StatusCode = StatusCodes.Status200OK,
                Message = "Authentication successful",
                User = new AuthenticatedUser
                {
                    Username = cleanUsername,
                    AuthPrincipal = authPrincipal,
                    DisplayName = displayName,
                    Email = email,
                    Groups = groups,
                    IsAdmin = isAdmin
                }
            };
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "Group search error for {Username}", cleanUsername);
            return new AuthResult
            {
                Success = false,
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                Message = "Unable to verify group membership. Please try again or contact your administrator."
            };
        }
    }

    private LdapConnection CreateConnection(string ldapUrl, string username, string password)
    {
        var normalizedUrl = ldapUrl.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase)
            ? ldapUrl
            : $"ldap://{ldapUrl}";

        var uri = new Uri(normalizedUrl);
        var identifier = new LdapDirectoryIdentifier(uri.Host, uri.Port > 0 ? uri.Port : 389);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(10),
            Credential = new NetworkCredential(username, password)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        return connection;
    }

    private bool CheckGroupAccess(IReadOnlyList<string> userGroups)
    {
        var allowedGroups = _options.AllowedGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .ToArray();

        if (allowedGroups.Length == 0)
        {
            return true;
        }

        if (userGroups.Count == 0)
        {
            return false;
        }

        foreach (var allowedGroup in allowedGroups)
        {
            foreach (var userGroup in userGroups)
            {
                if (string.Equals(userGroup, allowedGroup, StringComparison.OrdinalIgnoreCase)
                    || userGroup.Contains(allowedGroup, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractCleanUsername(string username)
    {
        if (username.Contains('@'))
        {
            return username.Split('@', 2)[0];
        }

        if (username.Contains('\\'))
        {
            return username.Split('\\').Last();
        }

        return username;
    }

    private static IReadOnlyList<string> BuildAdAuthFormats(string rawUsername, string cleanUsername, string domain)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCandidate(List<string> list, HashSet<string> set, string? value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || !set.Add(normalized))
            {
                return;
            }

            list.Add(normalized);
        }

        var explicitUsername = rawUsername.Trim();
        var domainQualified = explicitUsername.Contains('\\') || explicitUsername.Contains('@');

        if (domainQualified)
        {
            AddCandidate(candidates, seen, explicitUsername);
        }

        AddCandidate(candidates, seen, $"{cleanUsername}@{domain}");
        AddCandidate(candidates, seen, $"{cleanUsername}@intel.com");
        AddCandidate(candidates, seen, $"CORP\\{cleanUsername}");
        AddCandidate(candidates, seen, $"GER\\{cleanUsername}");
        AddCandidate(candidates, seen, $"GAR\\{cleanUsername}");
        AddCandidate(candidates, seen, $"AMR\\{cleanUsername}");
        AddCandidate(candidates, seen, cleanUsername);

        if (!domainQualified)
        {
            AddCandidate(candidates, seen, explicitUsername);
        }

        return candidates;
    }

    private static string? NormalizeWindowsUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var trimmed = username.Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('@'))
        {
            return trimmed;
        }

        return $"CORP\\{trimmed}";
    }

    private static string? GetAttributeValue(SearchResultEntry entry, string attributeName)
    {
        return entry.Attributes.Contains(attributeName) && entry.Attributes[attributeName].Count > 0
            ? entry.Attributes[attributeName][0]?.ToString()
            : null;
    }

    private static IReadOnlyList<string> GetAttributeValues(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName))
        {
            return [];
        }

        return entry.Attributes[attributeName]
            .GetValues(typeof(string))
            .OfType<string>()
            .ToArray();
    }

    private static string EscapeLdapFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static bool IsInvalidCredential(LdapException exception) => exception.ErrorCode == 49;

    private static bool IsHostUnavailable(LdapException exception) => exception.ErrorCode == 81;

    private static bool IsDnsFailure(LdapException exception)
    {
        return exception.ServerErrorMessage?.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) == true
            || exception.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase);
    }
}