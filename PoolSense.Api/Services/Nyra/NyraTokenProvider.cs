using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolSense.Api.Services.Nyra;

public sealed class NyraTokenProvider(HttpClient httpClient, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaximumTokenLifetime = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, TokenEntry> tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<TokenEntry>>> tokenFlights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DiscoveryEntry> discovery = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> discoveryFlights = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long tokenEndpointRequests;
    private long discoveryRequests;

    public long TokenEndpointRequests => Interlocked.Read(ref tokenEndpointRequests);
    public long DiscoveryRequests => Interlocked.Read(ref discoveryRequests);

    public async Task<string> GetTokenAsync(
        string? tokenUrl = null,
        string? issuer = null,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = new Credentials(
            Required(clientId, "Nyra:ClientId"),
            Required(clientSecret, "Nyra:ClientSecret"),
            Required(audience, "Nyra:Audience"),
            FirstNonEmpty(scope, "openid"));
        var identity = Hash(JsonSerializer.Serialize(new
        {
            TokenUrl = tokenUrl?.Trim(),
            Issuer = issuer?.Trim().TrimEnd('/'),
            credentials.ClientId,
            credentials.ClientSecret,
            credentials.Audience,
            credentials.Scope
        }));

        if (tokens.TryGetValue(identity, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
            return cached.Token;

        var flight = tokenFlights.GetOrAdd(identity, _ =>
            new Lazy<Task<TokenEntry>>(() => RefreshAndRemoveAsync(identity, tokenUrl, issuer, credentials),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var token = await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return token.Token;
    }

    public void InvalidateToken(
        string? tokenUrl = null,
        string? issuer = null,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        string? scope = null)
    {
        var identity = Hash(JsonSerializer.Serialize(new
        {
            TokenUrl = tokenUrl?.Trim(),
            Issuer = issuer?.Trim().TrimEnd('/'),
            ClientId = clientId,
            ClientSecret = clientSecret,
            Audience = audience,
            Scope = FirstNonEmpty(scope, "openid")
        }));
        tokens.TryRemove(identity, out _);
    }

    private async Task<TokenEntry> RefreshAndRemoveAsync(
        string identity,
        string? tokenUrl,
        string? issuer,
        Credentials credentials)
    {
        try
        {
            if (tokens.TryGetValue(identity, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
                return cached;

            var endpoint = !string.IsNullOrWhiteSpace(tokenUrl)
                ? tokenUrl
                : await DiscoverTokenEndpointAsync(issuer).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["scope"] = credentials.Scope,
                ["audience"] = credentials.Audience
            });

            Interlocked.Increment(ref tokenEndpointRequests);
            using var response = await httpClient.PostAsync(endpoint, content, CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("access_token", out var tokenProperty)
                || tokenProperty.GetString() is not { Length: > 0 } accessToken)
                throw new InvalidOperationException("Token endpoint returned 200 but no access_token.");

            var expiresAt = GetExpiry(json.RootElement, accessToken);
            var entry = new TokenEntry(accessToken, expiresAt);
            if (expiresAt is not null && expiresAt > clock.GetUtcNow())
            {
                tokens[identity] = entry;
                Trim(tokens, 256);
            }
            return entry;
        }
        finally
        {
            tokenFlights.TryRemove(identity, out _);
        }
    }

    private async Task<string> DiscoverTokenEndpointAsync(string? issuer)
    {
        var resolvedIssuer = Required(issuer, "Nyra:Issuer or Nyra:TokenUrl").TrimEnd('/');
        if (discovery.TryGetValue(resolvedIssuer, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
            return cached.TokenEndpoint;

        var flight = discoveryFlights.GetOrAdd(resolvedIssuer, key =>
            new Lazy<Task<string>>(() => DiscoverAndRemoveAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return await flight.Value.ConfigureAwait(false);
    }

    private async Task<string> DiscoverAndRemoveAsync(string issuer)
    {
        try
        {
            if (discovery.TryGetValue(issuer, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
                return cached.TokenEndpoint;

            Interlocked.Increment(ref discoveryRequests);
            using var response = await httpClient.GetAsync(
                issuer + "/.well-known/openid-configuration", CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OIDC configuration endpoint returned {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("token_endpoint", out var endpointProperty)
                || endpointProperty.GetString() is not { Length: > 0 } endpoint)
                throw new InvalidOperationException("OIDC configuration endpoint returned 200 but no token_endpoint.");

            discovery[issuer] = new(endpoint, clock.GetUtcNow() + DiscoveryLifetime);
            Trim(discovery, 128);
            return endpoint;
        }
        finally
        {
            discoveryFlights.TryRemove(issuer, out _);
        }
    }

    private DateTimeOffset? GetExpiry(JsonElement root, string accessToken)
    {
        var now = clock.GetUtcNow();
        TimeSpan? lifetime = null;
        if (root.TryGetProperty("expires_in", out var expires)
            && (expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var seconds)
                || expires.ValueKind == JsonValueKind.String && long.TryParse(expires.GetString(), out seconds))
            && seconds > 0)
            lifetime = TimeSpan.FromSeconds(Math.Min(seconds, MaximumTokenLifetime.TotalSeconds));

        if (lifetime is null)
            lifetime = ReadJwtExpiry(accessToken) is { } jwtExpiry ? jwtExpiry - now : null;
        if (lifetime is null || lifetime <= TimeSpan.Zero)
            return null;

        var safeLifetime = lifetime.Value > MaximumTokenLifetime ? MaximumTokenLifetime : lifetime.Value;
        var margin = TimeSpan.FromSeconds(Math.Min(60, Math.Max(1, safeLifetime.TotalSeconds * 0.1)));
        return now + safeLifetime - margin;
    }

    private static DateTimeOffset? ReadJwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Trim<T>(ConcurrentDictionary<string, T> cache, int maximum)
    {
        if (cache.Count <= maximum) return;
        foreach (var key in cache.Keys.Take(cache.Count - maximum))
            cache.TryRemove(key, out _);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} configuration is required.")
            : value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Credentials(string ClientId, string ClientSecret, string Audience, string Scope);
    private sealed record TokenEntry(string Token, DateTimeOffset? ExpiresAt);
    private sealed record DiscoveryEntry(string TokenEndpoint, DateTimeOffset ExpiresAt);
}
