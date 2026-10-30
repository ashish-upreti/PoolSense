using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class GitHubOAuthStates
{
    public sealed record Pending(string User, string Session, string Verifier, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    public void Add(string state, Pending value)
    {
        foreach (var entry in pending.Where(entry => entry.Value.Expires < DateTimeOffset.UtcNow)) pending.TryRemove(entry.Key, out _);
        pending[state] = value;
    }
    public Pending? Consume(string state) => pending.TryRemove(state, out var value) && value.Expires > DateTimeOffset.UtcNow ? value : null;
    public void Invalidate(string user)
    {
        foreach (var entry in pending.Where(entry => entry.Value.User == user)) pending.TryRemove(entry.Key, out _);
    }
}

public sealed class GitHubDisconnectException() : Exception("Unable to revoke GitHub authorization. Try Disconnect again, or revoke PoolSense in GitHub Settings > Applications before reconnecting.");

public sealed class GitHubOAuthService(HttpClient http, ICodeRepositoryStore store, GitHubOAuthStates states,
    IDataProtectionProvider protection, IOptions<CodeRAGOptions> options)
{
    public (string State, string Url) Begin(string user, string session)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.GitHubClientId) || string.IsNullOrWhiteSpace(settings.GitHubClientSecret))
            throw new CodeAccessException("GitHub OAuth is not configured by the server administrator.");
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        states.Add(state, new(user, session, verifier, DateTimeOffset.UtcNow.AddMinutes(10)));
        return (state, QueryHelpers.AddQueryString("https://github.com/login/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = settings.GitHubClientId, ["redirect_uri"] = settings.CallbackUrl,
            ["scope"] = "repo", ["state"] = state,
            ["prompt"] = "select_account",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        }));
    }

    public async Task DisconnectAsync(string user, CancellationToken token)
    {
        states.Invalidate(user);
        var connection = await store.ConnectionAsync(user, token);
        if (connection is not null)
        {
            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.GitHubClientId) || string.IsNullOrWhiteSpace(settings.GitHubClientSecret))
                throw new GitHubDisconnectException();
            string credential;
            try { credential = protection.CreateProtector(GitHubCodeClient.TokenPurpose).Unprotect(connection.ProtectedToken); }
            catch (CryptographicException) { throw new GitHubDisconnectException(); }
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/applications/{Uri.EscapeDataString(settings.GitHubClientId)}/grant")
            {
                Content = JsonContent.Create(new { access_token = credential })
            };
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.GitHubClientId}:{settings.GitHubClientSecret}")));
            request.Headers.UserAgent.ParseAdd("PoolSense-CodeKnowledge/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            try
            {
                using var response = await http.SendAsync(request, token);
                if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotFound) throw new GitHubDisconnectException();
            }
            catch (HttpRequestException) { throw new GitHubDisconnectException(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GitHubDisconnectException(); }
        }
        await store.DisconnectAsync(user, token);
        states.Invalidate(user);
    }

    public async Task CompleteAsync(string user, string session, string state, string code, CancellationToken token)
    {
        var pending = states.Consume(state);
        if (pending is null || pending.User != user || pending.Session != session) throw new CodeAccessException("GitHub connection expired. Start again.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.Value.GitHubClientId, ["client_secret"] = options.Value.GitHubClientSecret,
                ["redirect_uri"] = options.Value.CallbackUrl, ["code"] = code, ["code_verifier"] = pending.Verifier
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new CodeAccessException();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!document.RootElement.TryGetProperty("access_token", out var accessToken)) throw new CodeAccessException();
        var credential = accessToken.GetString()!;
        using var identityRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        identityRequest.Headers.Authorization = new("Bearer", credential);
        identityRequest.Headers.UserAgent.ParseAdd("PoolSense-CodeKnowledge/1.0");
        using var identityResponse = await http.SendAsync(identityRequest, token);
        if (!identityResponse.IsSuccessStatusCode) throw new CodeAccessException();
        using var identity = await JsonDocument.ParseAsync(await identityResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
        await store.SaveConnectionAsync(new(user, identity.RootElement.GetProperty("id").GetInt64(),
            identity.RootElement.GetProperty("login").GetString()!,
            protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect(credential)), token);
    }
}