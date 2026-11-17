using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeRAGOptions
{
    public bool Enabled { get; set; }
    public string StoragePath { get; set; } = "artifacts/code-knowledge";
    /// <summary>Server-wide GitHub PAT used for all repository access.</summary>
    public string GitHubToken { get; set; } = "";
    public int RefreshMinutes { get; set; } = 60;
    public int MaxArchiveMegabytes { get; set; } = 100;
    public int MaxExtractedMegabytes { get; set; } = 500;
    public int MaxRepositoriesPerProject { get; set; } = 20;
    public int ContextCharacters { get; set; } = 24000;
    public int SearchConcurrency { get; set; } = 3;
    public double SearchDeadlineSeconds { get; set; } = 20;
    public bool RepositoryRoutingEnabled { get; set; }
    public int RepositoryShortlistSize { get; set; } = 3;
    public int CandidatesPerRepository { get; set; } = 20;
    public int GlobalCandidateLimit { get; set; } = 256;
    public int MaxEvidenceSpans { get; set; } = 12;
    public int BackgroundConcurrency { get; set; } = 2;
    public int BackgroundBatchSize { get; set; } = 4;
    public int BackgroundCheckSeconds { get; set; } = 10;
    public int StartupJitterSeconds { get; set; } = 5;
    public OkfOptions Knowledge { get; set; } = new();
    public CodeSemanticOptions Semantic { get; set; } = new();
}

public sealed record ManagedGitHubIdentity(long GitHubId, string Login, string ProtectedToken);

public sealed record CodeRepository
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = "";
    public long GitHubId { get; init; }
    public string FullName { get; init; } = "";
    public string Branch { get; init; } = "";
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

/// <summary>Commit-pinned source evidence returned only after current managed-PAT access checks.</summary>
public sealed record CodeEvidence(string RepositoryId, string ProjectId, string Repository, string Branch,
    string Commit, string FilePath, string Symbol, int StartLine, int EndLine, string Content, string SourceUrl)
{
    public IReadOnlyList<CodeRelationship> Relationships { get; init; } = [];
}

public sealed record CodeRelationship(string Kind, string FromSymbol, string ToSymbol, string FilePath,
    int SiteLine, string TargetFilePath, int TargetLine, string Resolution);

public sealed record CodeRetrievalResult(string Status, IReadOnlyList<CodeEvidence> Documents)
{
    public static CodeRetrievalResult Skipped { get; } = new("not-available", []);
    public CodeSearchCoverage Coverage { get; init; } = new();
}

public sealed record CodeSearchCoverage
{
    public IReadOnlyDictionary<string, double> TimingsMs { get; init; } = new Dictionary<string, double>();
    public string QueryKind { get; init; } = "concept";
    public int AuthorizedRepositories { get; init; }
    public int SearchedRepositories { get; init; }
    public int FailedRepositories { get; init; }
    public bool AuthorizationComplete { get; init; } = true;
    public bool DeadlineExceeded { get; init; }
    public bool ExpandedFallback { get; init; }
    public string SemanticStatus { get; init; } = "disabled";
    public int SemanticIndexedChunks { get; init; }
    public int SemanticEligibleChunks { get; init; }
    public int SemanticTargetChunks { get; init; }
    public bool SemanticTargetReached { get; init; }
    public string KnowledgeStatus { get; init; } = "not-available";
    public DateTimeOffset? OldestIndexedAt { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class CodeRetrievalMessages
{
    public static string Notice(CodeRetrievalResult result)
    {
        var note = result.Status switch
        {
            "partial" => "Code search is incomplete; showing the evidence collected so far, not exhaustive repository coverage.",
            "timed-out" => "The code search deadline was reached before usable evidence was collected.",
            "not-ready" => "Accessible code indexes are not ready. Indexing and refresh run in the background, not during search.",
            "failed" => "Code search could not finish. Do not interpret this as an absence of relevant code.",
            "authorization-unavailable" => "Current code access could not be verified; source evidence was withheld.",
            "not-connected" => "Server-managed GitHub access is unavailable or the PAT was rejected. Verify the token and organization SSO authorization.",
            "not-configured" => "Server-managed GitHub access is not configured. Ask the administrator to configure CodeRAG:GitHubToken.",
            "disabled" => "Code retrieval is disabled.",
            "not-authenticated" => "Sign in before searching code.",
            "not-scoped" => "No application code scope was selected or resolved.",
            "no-accessible-repositories" => "No repository source is currently accessible for this scope.",
            "no-accessible-matches" => "No matching source was found in the accessible indexed repositories searched.",
            "stale" => "Showing a previous indexed snapshot pending background freshness/refresh; it may differ from current or deployed code.",
            _ => ""
        };
        if (result.Coverage.SemanticStatus.Split(',').Any(status => status.Trim() is not ("disabled" or "ready" or "reused" or "not-used")))
            note += " Semantic retrieval is limited; lexical and graph evidence remains available.";
        if (result.Status is "partial" or "timed-out")
            note += $" Searched {result.Coverage.SearchedRepositories} of {result.Coverage.AuthorizedRepositories} confirmed accessible repositories.";
        if (!result.Coverage.AuthorizationComplete)
            note += " Access verification is incomplete; unverified repositories were not used.";
        if (result.Coverage.SemanticTargetChunks > 0)
            note += $" Known semantic coverage: {result.Coverage.SemanticIndexedChunks}/{result.Coverage.SemanticTargetChunks} target vectors ({result.Coverage.SemanticEligibleChunks} eligible chunks). "
                + (result.Coverage.SemanticTargetReached ? "Configured targets reached, not full-corpus coverage." : "Coverage grows in bounded background batches.");
        return note.Trim();
    }
}

public sealed record CodeChatTurn
{
    [Required, MaxLength(16)] public string Role { get; init; } = "";
    [Required, MaxLength(8000)] public string Content { get; init; } = "";
}

/// <summary>Asks a question against a project's indexed repositories, optionally continuing a saved session.</summary>
public sealed record CodeAskRequest
{
    [Required, MaxLength(4000)] public string Question { get; init; } = "";
    [MaxLength(20)] public IReadOnlyList<CodeChatTurn> History { get; init; } = [];
    [MaxLength(32)] public string? SessionId { get; init; }
}

/// <summary>Asks a question against the indexed repositories of several applications (empty <see cref="ProjectIds"/> = every application).</summary>
public sealed record CodeScopedAskRequest
{
    [MaxLength(500)] public IReadOnlyList<string> ProjectIds { get; init; } = [];
    [Required, MaxLength(4000)] public string Question { get; init; } = "";
    [MaxLength(20)] public IReadOnlyList<CodeChatTurn> History { get; init; } = [];
    [MaxLength(32)] public string? SessionId { get; init; }

    public CodeAskRequest ToAskRequest() => new() { Question = Question, History = History, SessionId = SessionId };
}

/// <summary>
/// The set of applications a code conversation runs against. Empty <see cref="ProjectIds"/> means every application;
/// a single id maps onto the legacy per-application session shape so existing history stays visible.
/// </summary>
public sealed record CodeChatScope
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CodeChatScope(IReadOnlyList<string> projectIds) => ProjectIds = projectIds;

    public static CodeChatScope All { get; } = new([]);

    public IReadOnlyList<string> ProjectIds { get; }
    public bool IsAll => ProjectIds.Count == 0;
    public string? ProjectId => ProjectIds.Count == 1 ? ProjectIds[0] : null;
    /// <summary>Persisted scope for multi-application and all-application sessions; null for single-application sessions.</summary>
    public string? ScopeJson => ProjectIds.Count == 1 ? null : JsonSerializer.Serialize(ProjectIds, JsonOptions);

    public static CodeChatScope From(IEnumerable<string>? projectIds)
    {
        var normalized = (projectIds ?? [])
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? All : new(normalized);
    }

    public static CodeChatScope FromStored(string? projectId, string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson)) return From(projectId is null ? [] : [projectId]);
        try { return From(JsonSerializer.Deserialize<List<string>>(scopeJson, JsonOptions)); }
        catch (JsonException) { return All; }
    }

    public bool Contains(string projectId) => ProjectIds.Contains(projectId, StringComparer.OrdinalIgnoreCase);
}

public sealed record CodeAskResponse(string ProjectId, string SessionId, string Question, string Answer, DateTimeOffset GeneratedAt,
    string CodeKnowledgeStatus, IReadOnlyList<CodeEvidence> Citations)
{
    public IReadOnlyDictionary<string, double> TimingsMs { get; init; } = new Dictionary<string, double>();
    public CodeSearchCoverage? CodeSearchCoverage { get; init; }
    /// <summary>Applications the answer was scoped to; empty means every application.</summary>
    public IReadOnlyList<string> ProjectIds { get; init; } = ProjectId.Length == 0 ? [] : [ProjectId];
}

/// <summary><paramref name="ProjectId"/> is empty for multi-application sessions; <see cref="ProjectIds"/> is empty for all-application sessions.</summary>
public sealed record CodeChatSessionSummary(string Id, string ProjectId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount)
{
    public IReadOnlyList<string> ProjectIds { get; init; } = ProjectId.Length == 0 ? [] : [ProjectId];
}

public sealed record CodeChatStoredMessage(string Role, string Content, IReadOnlyList<CodeEvidence> Citations, DateTimeOffset CreatedAt);

public sealed record CodeChatSession(CodeChatSessionSummary Session, IReadOnlyList<CodeChatStoredMessage> Messages);

public static class CodeUser
{
    public static string Name(ClaimsPrincipal? principal) => principal?.Identity?.IsAuthenticated == true
        ? (principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity.Name ?? "").Trim().ToLowerInvariant()
        : "";
}

public sealed class CodeAccessException(string message = "GitHub code access is unavailable. Check the server-managed PAT and repository permissions.", DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}