using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeRAGOptions
{
    public bool Enabled { get; set; }
    public string StoragePath { get; set; } = "artifacts/code-knowledge";
    public string GitHubClientId { get; set; } = "";
    public string GitHubClientSecret { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string ReturnUrl { get; set; } = "/";
    public int RefreshMinutes { get; set; } = 60;
    public int MaxArchiveMegabytes { get; set; } = 100;
    public int MaxExtractedMegabytes { get; set; } = 500;
    public int MaxRepositoriesPerProject { get; set; } = 20;
    public int ContextCharacters { get; set; } = 24000;
}

public sealed record GitHubConnection(string UserName, long GitHubId, string Login, string ProtectedToken);

public sealed record CodeRepository
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = "";
    public long GitHubId { get; init; }
    public string FullName { get; init; } = "";
    public string Branch { get; init; } = "";
    public string IndexingUser { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int Revision { get; init; }
    public string Status { get; init; } = "queued";
    public string Message { get; init; } = "";
    public string IndexedCommit { get; init; } = "";
    public string Generation { get; init; } = "";
    public int FileCount { get; init; }
    public int SymbolCount { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public DateTimeOffset? LastIndexedAt { get; init; }
    public DateTimeOffset NextCheckAt { get; init; } = DateTimeOffset.MinValue;
}

/// <summary>Configures a GitHub repository as project code knowledge.</summary>
public sealed record SaveCodeRepositoryRequest
{
    [Required, MaxLength(300)] public string Url { get; init; } = "";
    [MaxLength(255)] public string Branch { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

/// <summary>Reports repository index state without credentials or local paths.</summary>
public sealed record CodeRepositoryResponse(string Id, string ProjectId, string Url, string Branch,
    bool Enabled, string Status, string Message, string IndexedCommit, int FileCount, int SymbolCount,
    DateTimeOffset? LastCheckedAt, DateTimeOffset? LastIndexedAt);

/// <summary>Commit-pinned source evidence visible to the current GitHub user.</summary>
public sealed record CodeEvidence(string RepositoryId, string ProjectId, string Repository, string Branch,
    string Commit, string FilePath, string Symbol, int StartLine, int EndLine, string Content, string SourceUrl);

public sealed record CodeRetrievalResult(string Status, IReadOnlyList<CodeEvidence> Documents)
{
    public static CodeRetrievalResult Skipped { get; } = new("not-available", []);
}

public static class CodeUser
{
    public static string Name(ClaimsPrincipal? principal) => principal?.Identity?.IsAuthenticated == true
        ? (principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity.Name ?? "").Trim().ToLowerInvariant()
        : "";
}

public sealed class CodeAccessException(string message = "GitHub code access is unavailable. Reconnect GitHub or check repository permissions.", DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}