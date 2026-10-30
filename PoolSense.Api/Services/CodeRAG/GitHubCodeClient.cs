using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record GitHubRepository(long Id, string FullName, string DefaultBranch);

public interface IGitHubCodeClient
{
    Task<GitHubRepository> RepositoryAsync(string user, string fullName, CancellationToken token);
    Task<string> CommitAsync(string user, CodeRepository repository, CancellationToken token);
    Task DownloadAsync(string user, CodeRepository repository, string commit, string destination, CancellationToken token);
}

public sealed partial class GitHubCodeClient(HttpClient http, ICodeRepositoryStore store,
    IDataProtectionProvider protection, IOptions<CodeRAGOptions> options) : IGitHubCodeClient
{
    public const string TokenPurpose = "PoolSense.GitHub.OAuth.v1";
    private readonly IDataProtector protector = protection.CreateProtector(TokenPurpose);

    public static string ParseUrl(string url)
    {
        var match = RepositoryUrl().Match(url.Trim());
        if (!match.Success) throw new ArgumentException("Use an HTTPS github.com owner/repository URL without credentials or query parameters.");
        var name = match.Groups[2].Value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name is "" or "." or "..") throw new ArgumentException("Invalid repository name.");
        return $"{match.Groups[1].Value}/{name}";
    }

    [GeneratedRegex(@"^https://github\.com/([A-Za-z0-9][A-Za-z0-9_.-]*)/([A-Za-z0-9][A-Za-z0-9_.-]*)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    public async Task<GitHubRepository> RepositoryAsync(string user, string fullName, CancellationToken token)
    {
        using var response = await SendAsync(user, $"repos/{fullName}", token);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = document.RootElement;
        return new(root.GetProperty("id").GetInt64(), root.GetProperty("full_name").GetString()!, root.GetProperty("default_branch").GetString()!);
    }

    public async Task<string> CommitAsync(string user, CodeRepository repository, CancellationToken token)
    {
        var metadata = await RepositoryAsync(user, repository.FullName, token);
        if (metadata.Id != repository.GitHubId) throw new CodeAccessException();
        using var response = await SendAsync(user, $"repos/{metadata.FullName}/commits/{Uri.EscapeDataString(repository.Branch)}", token);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var sha = document.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(sha, "^[a-f0-9]{40}$")) throw new CodeAccessException();
        return sha;
    }

    private async Task<HttpResponseMessage> SendAsync(string user, string path, CancellationToken token, bool archive = false)
    {
        var connection = await store.ConnectionAsync(user, token) ?? throw new CodeAccessException();
        string credential;
        try { credential = protector.Unprotect(connection.ProtectedToken); }
        catch (CryptographicException) { throw new CodeAccessException(); }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.UserAgent.ParseAdd("PoolSense-CodeKnowledge/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode || archive && response.StatusCode == HttpStatusCode.Found) return response;
        DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delay) retryAt = DateTimeOffset.UtcNow.Add(delay);
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
            retryAt = DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(5);
        response.Dispose();
        throw new CodeAccessException(retryAt: retryAt);
    }

    public async Task DownloadAsync(string user, CodeRepository repository, string commit, string destination, CancellationToken token)
    {
        using var redirect = await SendAsync(user, $"repos/{repository.FullName}/zipball/{commit}", token, archive: true);
        var location = redirect.Headers.Location;
        if (location is null || !location.IsAbsoluteUri || location.Scheme != "https" || location.Host != "codeload.github.com"
            || !string.IsNullOrEmpty(location.UserInfo) || location.Port != 443) throw new CodeAccessException();
        using var download = new HttpRequestMessage(HttpMethod.Get, location);
        download.Headers.UserAgent.ParseAdd("PoolSense-CodeKnowledge/1.0");
        using var response = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new CodeAccessException();
        var maximum = options.Value.MaxArchiveMegabytes * 1024L * 1024L;
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("Repository archive exceeds configured limit.");
        var archivePath = destination + ".zip";
        Directory.CreateDirectory(destination);
        try
        {
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += count;
                    if (length > maximum) throw new IOException("Repository archive exceeds configured limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            await ExtractAsync(archivePath, destination, options.Value.MaxExtractedMegabytes * 1024L * 1024L, token);
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    public static async Task ExtractAsync(string archivePath, string destination, long maximum, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100000) throw new IOException("Too many repository files.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) continue;
            var separator = entry.FullName.IndexOf('/');
            if (separator < 0 || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[(separator + 1)..];
            if (relative.Contains('\\') || relative.Contains(':')) throw new IOException("Invalid archive path.");
            var path = Path.GetFullPath(Path.Combine(destination, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive path.");
            total += entry.Length;
            if (total > maximum) throw new IOException("Extracted repository exceeds configured limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                written += count;
                if (written > entry.Length) throw new IOException("Invalid archive size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
    }
}