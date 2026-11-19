using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public interface IManagedGitHubAccess
{
    bool IsConfigured { get; }
    Task<ManagedGitHubIdentity> GetIdentityAsync(CancellationToken token);
}

/// <summary>Resolves and protects the server-managed PAT without consulting per-user GitHub credentials.</summary>
public sealed class SharedGitHubConnection(IHttpClientFactory httpFactory, IDataProtectionProvider protection,
    IOptionsMonitor<CodeRAGOptions> options) : IManagedGitHubAccess
{
    public const string HttpClientName = "PoolSense.GitHub.ManagedToken";
    public const string ServiceIdentity = "poolsense-managed-pat";
    private readonly SemaphoreSlim gate = new(1, 1);
    private (string Fingerprint, long Id, string Login, string ProtectedToken)? identity;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.GitHubToken);

    public async Task<ManagedGitHubIdentity> GetIdentityAsync(CancellationToken token)
    {
        var pat = options.CurrentValue.GitHubToken.Trim();
        if (pat.Length == 0)
            throw new CodeAccessException("Server-managed GitHub access is not configured. Configure CodeRAG:GitHubToken.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pat)));
        if (identity is { } cached && cached.Fingerprint == fingerprint)
            return ToIdentity(cached);

        await gate.WaitAsync(token);
        try
        {
            if (identity is { } current && current.Fingerprint == fingerprint)
                return ToIdentity(current);
            identity = await ResolveAsync(pat, fingerprint, token);
            return ToIdentity(identity.Value);
        }
        finally { gate.Release(); }
    }

    private static ManagedGitHubIdentity ToIdentity((string Fingerprint, long Id, string Login, string ProtectedToken) value) =>
        new(value.Id, value.Login, value.ProtectedToken);

    private async Task<(string Fingerprint, long Id, string Login, string ProtectedToken)> ResolveAsync(
        string pat, string fingerprint, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new("Bearer", pat);
        request.Headers.UserAgent.ParseAdd("PoolSense-CodeKnowledge/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var client = httpFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new CodeAccessException($"GitHub rejected the configured CodeRAG:GitHubToken ({(int)response.StatusCode}). Check the token and its SSO authorization.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = document.RootElement;
        // Protect once so repeated lookups yield an identical record (indexer compares connections by value).
        return (fingerprint, root.GetProperty("id").GetInt64(), root.GetProperty("login").GetString()!,
            protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect(pat));
    }
}
