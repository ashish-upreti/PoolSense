using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

/// <summary>Resolves the server-configured GitHub token (PAT) once and exposes it as a connection for every user.</summary>
public sealed class SharedGitHubConnection(IHttpClientFactory httpFactory, IDataProtectionProvider protection, IOptions<CodeRAGOptions> options)
{
    public const string HttpClientName = "PoolSense.GitHub.SharedToken";
    private readonly SemaphoreSlim gate = new(1, 1);
    private (long Id, string Login, string ProtectedToken)? identity;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.GitHubToken);

    public async Task<GitHubConnection> ForUserAsync(string user, CancellationToken token)
    {
        if (identity is null)
        {
            await gate.WaitAsync(token);
            try { identity ??= await ResolveAsync(token); }
            finally { gate.Release(); }
        }
        var (id, login, secret) = identity.Value;
        return new(user, id, login, secret);
    }

    private async Task<(long, string, string)> ResolveAsync(CancellationToken token)
    {
        var pat = options.Value.GitHubToken.Trim();
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
        return (root.GetProperty("id").GetInt64(), root.GetProperty("login").GetString()!,
            protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect(pat));
    }
}

/// <summary>Store decorator that substitutes the shared token connection for per-user OAuth connections.</summary>
public sealed class SharedGitHubConnectionStore(CodeRepositoryStore inner, SharedGitHubConnection shared) : ICodeRepositoryStore
{
    private const string ManagedMessage = "GitHub access is configured by the server administrator and cannot be changed per user.";

    public async Task<GitHubConnection?> ConnectionAsync(string user, CancellationToken token) =>
        user.Length == 0 ? null : await shared.ForUserAsync(user, token);
    public Task SaveConnectionAsync(GitHubConnection connection, CancellationToken token) => throw new CodeAccessException(ManagedMessage);
    public Task DisconnectAsync(string user, CancellationToken token) => throw new CodeAccessException(ManagedMessage);
    public Task<IReadOnlyList<CodeRepository>> RepositoriesAsync(CancellationToken token) => inner.RepositoriesAsync(token);
    public Task AddAsync(CodeRepository repository, CancellationToken token) => inner.AddAsync(repository, token);
    public Task<bool> UpdateAsync(CodeRepository repository, int expectedRevision, CancellationToken token) => inner.UpdateAsync(repository, expectedRevision, token);
    public Task<bool> DeleteAsync(string id, int revision, CancellationToken token) => inner.DeleteAsync(id, revision, token);
}
