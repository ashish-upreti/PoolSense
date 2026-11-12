using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeIndexFiles(IHostEnvironment environment, IOptions<CodeRAGOptions> options, ILogger<CodeIndexFiles>? logger = null)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshGates = new();
    private readonly object leaseLock = new();
    private readonly Dictionary<string, int> readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deleting = new(StringComparer.OrdinalIgnoreCase);
    public SemaphoreSlim RefreshGate(string repositoryId) => refreshGates.GetOrAdd(repositoryId, _ => new(1, 1));
    public string BasePath { get; } = CodeStoragePath.Resolve(options.Value.StoragePath, environment.ContentRootPath);
    public string Folder(CodeRepository repository) => Path.Combine(BasePath, repository.Id, repository.Generation);
    public string Source(CodeRepository repository) => Path.Combine(Folder(repository), "source");
    public string Database(CodeRepository repository) => Path.Combine(Folder(repository), "index.sqlite3");
    public string SemanticCheckpoint(CodeRepository repository) => Path.Combine(BasePath, repository.Id, "semantic-resume");
    public bool Ready(CodeRepository repository) => repository.Generation.Length > 0
        && GraphStore.IsReady(Database(repository), [Source(repository)]);

    public IDisposable? Lease(CodeRepository repository, bool staging = false)
    {
        var folder = Folder(repository);
        lock (leaseLock)
        {
            if (deleting.Contains(folder) || !staging && !Directory.Exists(folder)) return null;
            readers[folder] = readers.GetValueOrDefault(folder) + 1;
        }
        return new SnapshotLease(() => Release(folder));
    }

    private sealed class SnapshotLease(Action release) : IDisposable
    {
        private Action? dispose = release;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    private void Release(string folder)
    {
        var remove = false;
        lock (leaseLock)
        {
            if (--readers[folder] == 0)
            {
                readers.Remove(folder);
                remove = pendingDeletes.Remove(folder);
                if (remove) deleting.Add(folder);
            }
        }
        if (remove) DeleteRetired(folder);
    }

    private void Retire(string folder)
    {
        lock (leaseLock)
        {
            if (deleting.Contains(folder)) return;
            if (readers.ContainsKey(folder)) { pendingDeletes.Add(folder); return; }
            deleting.Add(folder);
        }
        DeleteRetired(folder);
    }

    private void DeleteRetired(string folder)
    {
        OkfCatalog.Forget(folder);
        CodeSemanticIndex.EvictSnapshotCache(folder);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Retired code snapshot cleanup failed ({FailureType}); cleanup will retry.", exception.GetType().Name);
        }
        finally { lock (leaseLock) deleting.Remove(folder); }
    }

    public void Prune(CodeRepository repository)
    {
        var parent = Path.Combine(BasePath, repository.Id);
        if (!Directory.Exists(parent)) return;
        foreach (var folder in Directory.GetDirectories(parent))
            if (Path.GetFileName(folder) != repository.Generation && Path.GetFileName(folder) != "semantic-resume") Retire(folder);
    }

    public void ClearSemanticCheckpoint(CodeRepository repository) => Retire(SemanticCheckpoint(repository));

    public async Task CleanupAsync(ICodeRepositoryStore store, CancellationToken token)
    {
        if (!Directory.Exists(BasePath)) return;
        foreach (var folder in Directory.GetDirectories(BasePath))
        {
            token.ThrowIfCancellationRequested();
            var id = Path.GetFileName(folder);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var gate = RefreshGate(id);
            if (!await gate.WaitAsync(0, token)) continue;
            try
            {
                var repository = (await store.RepositoriesAsync(token)).FirstOrDefault(entry => entry.Id == id);
                if (repository is null)
                {
                    foreach (var generation in Directory.GetDirectories(folder)) Retire(generation);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                else Prune(repository);
            }
            finally { gate.Release(); }
        }
    }
}

public interface ICodeSnapshotBuilder
{
    Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token, string? previousSnapshotFolder = null);
}

public sealed class CodeSnapshotBuilder(IGitHubCodeClient github, CodeIndexFiles files, CodeSemanticIndex? semantic = null,
    IOptions<OkfOptions>? knowledge = null, Func<ILLMService>? llmFactory = null, ILogger<CodeSnapshotBuilder>? logger = null,
    IOptions<CodeRAGOptions>? settings = null) : ICodeSnapshotBuilder
{
    public async Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token, string? previousSnapshotFolder = null)
    {
        var previousSource = previousSnapshotFolder is null ? null : Path.Combine(previousSnapshotFolder, "source");
        var reuseSource = repository.IndexedCommit == commit && previousSource is not null && Directory.Exists(previousSource);
        if (reuseSource)
            await CopySourceAsync(previousSource!, files.Source(repository), settings?.Value.MaxExtractedMegabytes ?? 500, token);
        else
            await github.DownloadAsync(repository, commit, files.Source(repository), token);
        var previousDatabase = previousSnapshotFolder is null ? null : Path.Combine(previousSnapshotFolder, "index.sqlite3");
        IndexSummary summary;
        if (reuseSource && previousDatabase is not null && GraphStore.IsReady(previousDatabase, [previousSource!]))
        {
            token.ThrowIfCancellationRequested();
            using var previous = GraphStore.Open(previousDatabase, readOnly: true);
            using var next = GraphStore.Open(files.Database(repository));
            previous.BackupDatabase(next);
            using var transaction = next.BeginTransaction();
            GraphStore.Execute(next, "PRAGMA defer_foreign_keys=ON");
            var oldRoot = GraphStore.RootKey(previousSource!);
            var newRoot = GraphStore.RootKey(files.Source(repository));
            foreach (var table in new[] { "repositories", "symbols", "edges" })
                GraphStore.Execute(next, $"UPDATE {table} SET {(table == "repositories" ? "root" : "repo_root")}=@p0 WHERE {(table == "repositories" ? "root" : "repo_root")}=@p1", newRoot, oldRoot);
            transaction.Commit();
            token.ThrowIfCancellationRequested();
            summary = GraphStore.Summaries(next).Single(entry => entry.Root == newRoot);
            logger?.LogInformation("Reused unchanged code graph for repository {RepositoryId}; only optional snapshot preparation is upgraded.", repository.Id);
        }
        else
            summary = (await new RepositoryIndexer().IndexAsync(files.Source(repository), files.Database(repository), true, cancellationToken: token)).Summary;
        var metadata = repository with { IndexedCommit = commit, FileCount = summary.FileCount, SymbolCount = summary.SymbolCount };
        var okfOptions = knowledge?.Value ?? new OkfOptions { Enabled = false };
        var reusedKnowledge = false;
        IReadOnlyList<string> reusedLimitations = [];
        if (reuseSource && previousSnapshotFolder is not null && okfOptions.Enabled)
        {
            var manifest = Path.Combine(previousSnapshotFolder, CodeSnapshotPreparation.FileName);
            if (File.Exists(manifest) && new FileInfo(manifest).Length <= 16384 &&
                JsonSerializer.Deserialize<CodeSnapshotPreparation>(await File.ReadAllTextAsync(manifest, token)) is { } previous &&
                previous.KnowledgeProfile == CodeSnapshotPreparation.KnowledgeSettingsProfile(okfOptions) &&
                previous.KnowledgeStatus is "prepared" or "existing")
            {
                var existing = OkfLocalBundle.ReadExisting(Path.Combine(previousSnapshotFolder, "okf"), okfOptions, commit, token);
                if (existing.Status == "ready")
                {
                    var target = Path.Combine(files.Folder(repository), "okf");
                    Directory.CreateDirectory(target);
                    foreach (var document in existing.Documents)
                    {
                        token.ThrowIfCancellationRequested();
                        var destination = Path.Combine(target, (document.Id + ".md").Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await File.WriteAllTextAsync(destination, document.RawMarkdown, new UTF8Encoding(false), token);
                    }
                    reusedKnowledge = true;
                    reusedLimitations = previous.KnowledgeLimitations;
                }
            }
        }
        ILLMService? llm = null;
        if (okfOptions.Enabled && okfOptions.EnableLlmEnrichment && !reusedKnowledge)
        {
            try { llm = llmFactory?.Invoke(); }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger?.LogWarning("Knowledge generation provider unavailable ({FailureType}); deterministic knowledge remains available.", exception.GetType().Name);
            }
        }
        var okf = await OkfBundle.PrepareAsync(metadata, commit, files.Database(repository), files.Source(repository),
            files.Folder(repository), okfOptions, llm is null ? null : new BoundedKnowledgeLlm(llm, okfOptions.LlmTimeoutSeconds, token), token);
        if (reusedKnowledge) okf = okf with { Limitations = reusedLimitations };
        if (okfOptions.Enabled && okfOptions.EnableLlmEnrichment && llm is null && !reusedKnowledge)
            okf = okf with { Status = "prepared-with-errors", Errors = [.. okf.Errors, "Knowledge generation provider unavailable."] };
        CodeSemanticBuildResult? vectors = null;
        if (semantic is not null)
        {
            try
            {
                vectors = await semantic.BuildAsync(files.Database(repository), files.Source(repository), files.Folder(repository),
                    commit, previousSnapshotFolder, token, checkpointSnapshotFolder: files.SemanticCheckpoint(repository));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await PreserveSemanticCheckpointAsync(repository);
                throw;
            }
        }
        var complete = okf.Errors.Count == 0 && (okf.Status is "prepared" or "existing" or "disabled" or "empty")
            && (vectors is null || vectors.Status is "disabled" or "ready" ||
                vectors.Status == "partial" && vectors.TargetReached && vectors.ErrorType is null);
        if (!complete && (okf.Errors.Count > 0 || vectors?.ErrorType is not null)) logger?.LogWarning("Optional code snapshot preparation incomplete for {RepositoryId}: knowledge={KnowledgeStatus}, semantic={SemanticStatus}, knowledgeErrors={ErrorCount}. Check the generation preparation manifest.",
            repository.Id, okf.Status, vectors?.Status ?? "disabled", okf.Errors.Count);
        var preparation = new CodeSnapshotPreparation(CodeSnapshotPreparation.Profile(knowledge?.Value, semantic), complete,
            okf.Status, vectors?.Status ?? "disabled")
        {
            PreparationVersion = CodeSnapshotPreparation.Version,
            KnowledgeProfile = CodeSnapshotPreparation.KnowledgeSettingsProfile(okfOptions),
            KnowledgeErrors = okf.Errors.Take(4).Select(error => SourceText.Redact(error)).Select(error => error[..Math.Min(error.Length, 256)]).ToArray(),
            KnowledgeLimitations = okf.Limitations.Take(4).Select(limit => SourceText.Redact(limit)).Select(limit => limit[..Math.Min(limit.Length, 256)]).ToArray(),
            SemanticIndexedChunks = vectors?.IndexedChunks ?? 0,
            SemanticEligibleChunks = vectors?.EligibleChunks ?? 0,
            SemanticTargetChunks = vectors?.TargetChunks ?? 0,
            SemanticTargetReached = vectors?.TargetReached ?? false,
            SemanticSelectionPolicy = vectors?.SelectionPolicy ?? "",
            SemanticErrorType = vectors?.ErrorType,
            NextSemanticRefreshAt = vectors is { TargetReached: false, ErrorType: null } && semantic!.Enabled &&
                semantic.Options.MaxNewEmbeddingsPerBuild > 0
                ? DateTimeOffset.UtcNow.AddSeconds(semantic.Options.BatchCooldownSeconds) : null
        };
        await File.WriteAllTextAsync(Path.Combine(files.Folder(repository), CodeSnapshotPreparation.FileName), JsonSerializer.Serialize(preparation), token);
        return summary;
    }

    private async Task PreserveSemanticCheckpointAsync(CodeRepository repository)
    {
        var source = Path.Combine(files.Folder(repository), CodeSemanticIndex.ArtifactName);
        if (!File.Exists(source)) return;
        try
        {
            var coverage = semantic!.ReadCoverage(files.Folder(repository));
            if (coverage is not { IndexedChunks: > 0 }) return;
            var folder = files.SemanticCheckpoint(repository);
            var previous = semantic.ReadCoverage(folder);
            if (previous is not null && previous.IndexedChunks >= coverage.IndexedChunks) return;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, CodeSemanticIndex.ArtifactName);
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, CancellationToken.None);
                File.Move(staging, target, overwrite: true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            logger?.LogInformation("Semantic checkpoint retained for {RepositoryId}; {IndexedChunks} vectors can resume after cancellation.",
                repository.Id, coverage.IndexedChunks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Semantic checkpoint preservation failed for {RepositoryId} ({FailureType}).", repository.Id, exception.GetType().Name);
        }
    }

    private static async Task CopySourceAsync(string source, string destination, int maxMegabytes, CancellationToken token)
    {
        var pending = new Stack<(string Source, string Destination, int Depth)>();
        pending.Push((source, destination, 0));
        long bytes = 0;
        var entries = 0;
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (folder.Depth > 128) throw new InvalidDataException("Cached source tree is too deep.");
            Directory.CreateDirectory(folder.Destination);
            foreach (var entry in new DirectoryInfo(folder.Source).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 50000 || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Cached source exceeds file limits or contains a symbolic link.");
                var target = Path.Combine(folder.Destination, entry.Name);
                if (entry is DirectoryInfo directory) pending.Push((directory.FullName, target, folder.Depth + 1));
                else if (entry is FileInfo file)
                {
                    bytes += file.Length;
                    if (bytes > maxMegabytes * 1024L * 1024) throw new InvalidDataException("Cached source exceeds the extracted-byte limit.");
                    await using var input = file.OpenRead();
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token);
                }
            }
        }
    }

    private sealed class BoundedKnowledgeLlm(ILLMService inner, int timeoutSeconds, CancellationToken token) : ILLMService
    {
        public async Task<string> GetResponseAsync(string prompt)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            return await inner.GetBoundedResponseAsync(SourceText.Redact(prompt), 1500, deadline.Token);
        }
    }
}

public sealed record CodeSnapshotPreparation(string SettingsProfile, bool Complete, string KnowledgeStatus, string SemanticStatus)
{
    public const int Version = 5;
    public int PreparationVersion { get; init; }
    public string KnowledgeProfile { get; init; } = "";
    public IReadOnlyList<string> KnowledgeErrors { get; init; } = [];
    public IReadOnlyList<string> KnowledgeLimitations { get; init; } = [];
    public int SemanticIndexedChunks { get; init; }
    public int SemanticEligibleChunks { get; init; }
    public int SemanticTargetChunks { get; init; }
    public bool SemanticTargetReached { get; init; }
    public string SemanticSelectionPolicy { get; init; } = "";
    public string? SemanticErrorType { get; init; }
    public DateTimeOffset? NextSemanticRefreshAt { get; init; }
    public const string FileName = "preparation.json";
    public static string KnowledgeSettingsProfile(OkfOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = Version, knowledge = options }))));
    public static bool NeedsUpgrade(string folder, ILogger? logger = null, bool semanticEnabled = false,
        string? semanticPolicy = null, DateTimeOffset? now = null)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return true;
        try
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Code snapshot preparation manifest exceeds its limit.");
            var state = JsonSerializer.Deserialize<CodeSnapshotPreparation>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Code snapshot preparation manifest is empty.");
            return state.PreparationVersion < Version ||
                semanticEnabled && (state.SemanticStatus == "disabled" ||
                    state.SemanticStatus == "ready" && !File.Exists(Path.Combine(folder, CodeSemanticIndex.ArtifactName)) ||
                    semanticPolicy is not null && state.SemanticSelectionPolicy != semanticPolicy ||
                    state.SemanticErrorType is null && !state.SemanticTargetReached &&
                    state.NextSemanticRefreshAt is { } due && due <= (now ?? DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Code preparation manifest unavailable ({FailureType}); a background upgrade is required.", exception.GetType().Name);
            return true;
        }
    }
    public static double SemanticProgress(string folder, ILogger logger)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return 0;
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Code preparation manifest exceeds size limit.");
            var state = JsonSerializer.Deserialize<CodeSnapshotPreparation>(File.ReadAllText(path));
            return state is { SemanticTargetChunks: > 0 } ? (double)state.SemanticIndexedChunks / state.SemanticTargetChunks : 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Semantic progress metadata unavailable ({FailureType}); repository will be checked.", exception.GetType().Name);
            return 0;
        }
    }
    public static string Profile(OkfOptions? knowledge, CodeSemanticIndex? semantic) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = Version, knowledge, semantic = semantic?.Options, embeddingProfile = semantic?.Profile
        }))));
    public static bool NeedsRefresh(string folder, OkfOptions? knowledge, CodeSemanticIndex? semantic)
    {
        if (knowledge?.Enabled != true && semantic?.Enabled != true) return false;
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) return true;
        try
        {
            var state = JsonSerializer.Deserialize<CodeSnapshotPreparation>(File.ReadAllText(path));
            return state is null || !state.Complete || state.SettingsProfile != Profile(knowledge, semantic)
                || knowledge?.Enabled == true && state.KnowledgeStatus != "empty" && !File.Exists(Path.Combine(folder, "okf", "index.md"))
                || semantic?.Enabled == true && !File.Exists(Path.Combine(folder, CodeSemanticIndex.ArtifactName));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return true; }
    }
}

public sealed class CodeIndexCoordinator(ICodeRepositoryStore store, IGitHubCodeClient github, IManagedGitHubAccess access,
    ICodeSnapshotBuilder builder, CodeIndexFiles files, IOptions<CodeRAGOptions> options, TimeProvider clock,
    CodeSemanticIndex? semantic = null, IOptions<OkfOptions>? knowledge = null, ILogger<CodeIndexCoordinator>? logger = null,
    ICodeSnapshotWarmupQueue? warmupQueue = null)
{
    public async Task RefreshAsync(CodeRepository original, CancellationToken token)
    {
        if (!original.Enabled) return;
        var refreshGate = files.RefreshGate(original.Id);
        if (!await refreshGate.WaitAsync(0, token)) return;
        try { await RefreshOwnedAsync(original, token); }
        finally { refreshGate.Release(); }
    }

    private async Task RefreshOwnedAsync(CodeRepository original, CancellationToken token)
    {
        var working = original with { Revision = original.Revision + 1, Status = "indexing", Message = "" };
        if (!await store.UpdateAsync(working, original.Revision, token)) return;
        try
        {
            var connection = await access.GetIdentityAsync(token);
            var commit = await github.CommitAsync(working, token);
            var now = clock.GetUtcNow();
            var result = working with { LastCheckedAt = now, NextCheckAt = now.AddMinutes(options.Value.RefreshMinutes), Status = "ready" };
            var preparedThisRun = false;
            CodeSnapshotPreparation? preparation = null;
            if (commit != working.IndexedCommit || !files.Ready(working) ||
                CodeSnapshotPreparation.NeedsRefresh(files.Folder(working), knowledge?.Value, semantic))
            {
                var staged = working with { Generation = Guid.NewGuid().ToString("N") };
                using var stagedLease = files.Lease(staged, staging: true);
                using var previousLease = working.Generation.Length > 0 ? files.Lease(working) : null;
                var summary = await builder.BuildAsync(staged, commit, token, previousLease is null ? null : files.Folder(working));
                preparedThisRun = true;
                var latestHead = await github.CommitAsync(working, token);
                var currentConnection = await access.GetIdentityAsync(token);
                if (currentConnection != connection) throw new CodeAccessException();
                result = result with
                {
                    Generation = staged.Generation, IndexedCommit = commit, LastIndexedAt = clock.GetUtcNow(),
                    FileCount = summary.FileCount, SymbolCount = summary.SymbolCount
                };
                if (latestHead != commit)
                    result = result with { Status = "queued", NextCheckAt = clock.GetUtcNow(),
                        Message = "Branch advanced during indexing; serving the completed pinned snapshot while another refresh is queued." };
                var preparationPath = Path.Combine(files.Folder(staged), CodeSnapshotPreparation.FileName);
                if (File.Exists(preparationPath) && JsonSerializer.Deserialize<CodeSnapshotPreparation>(await File.ReadAllTextAsync(preparationPath, token)) is { } prepared)
                {
                    preparation = prepared;
                    if (prepared.NextSemanticRefreshAt is { } continuation && continuation < result.NextCheckAt)
                        result = result with { NextCheckAt = continuation };
                    if (prepared.SemanticTargetChunks > 0 && !prepared.SemanticTargetReached && prepared.SemanticErrorType is null)
                        result = result with { Message = $"Index ready; semantic coverage {prepared.SemanticIndexedChunks}/{prepared.SemanticTargetChunks} ({prepared.SemanticEligibleChunks} eligible) continues in background." };
                    else if (!prepared.Complete)
                        result = result with { Message = "Index ready; optional project knowledge or semantic preparation is incomplete." };
                    else if (prepared.KnowledgeLimitations.Count > 0)
                        result = result with { Message = "Index ready; project knowledge covers a bounded source selection." };
                }
            }
            if (await access.GetIdentityAsync(token) != connection) throw new CodeAccessException();
            if (await store.UpdateAsync(result with { Revision = working.Revision + 1 }, working.Revision, token))
            {
                files.Prune(result);
                if (preparedThisRun && preparation is { SemanticErrorType: null }) files.ClearSemanticCheckpoint(result);
                if (preparedThisRun && result.Generation.Length > 0
                    && (result.Status is "ready" or "queued")
                    && warmupQueue?.TryEnqueue(result.Id, result.Generation) == false)
                    logger?.LogDebug("Published snapshot cache warmup was skipped for repository {RepositoryId}; lazy loading remains available.", result.Id);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning("Code snapshot refresh failed for {RepositoryId} ({FailureType}); the last complete source snapshot is retained.",
                original.Id, exception.GetType().Name);
            var nextCheck = clock.GetUtcNow().AddMinutes(options.Value.RefreshMinutes);
            if (exception is CodeAccessException { RetryAt: { } retryAt } && retryAt > nextCheck) nextCheck = retryAt;
            var failure = working with
            {
                Revision = working.Revision + 1,
                Status = exception is CodeAccessException ? "access-required" : "error",
                Message = exception is CodeAccessException ? "Indexing authorization needs attention." : "Refresh failed; the last successful index is retained.",
                LastCheckedAt = clock.GetUtcNow(), NextCheckAt = nextCheck
            };
            await store.UpdateAsync(failure, working.Revision, token);
        }
    }
}

public sealed class CodeIndexWorker(IServiceScopeFactory scopes, IOptions<CodeRAGOptions> options,
    TimeProvider clock, ILogger<CodeIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var failures = 0;
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.NextDouble() * options.Value.StartupJitterSeconds), clock, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ICodeRepositoryStore>();
                var files = scope.ServiceProvider.GetRequiredService<CodeIndexFiles>();
                await files.CleanupAsync(store, stoppingToken);
                var due = (await store.RepositoriesAsync(stoppingToken))
                    .Where(repository => repository.Enabled && (repository.NextCheckAt <= clock.GetUtcNow() || repository.Status == "indexing" ||
                        repository.Status == "ready" && repository.Generation.Length > 0 &&
                        (options.Value.Knowledge.Enabled || options.Value.Semantic.Enabled) &&
                        CodeSnapshotPreparation.NeedsUpgrade(files.Folder(repository), logger, options.Value.Semantic.Enabled,
                            CodeSemanticIndex.PolicySignature(options.Value.Semantic), clock.GetUtcNow())))
                    .OrderBy(repository => options.Value.Semantic.Enabled ? CodeSnapshotPreparation.SemanticProgress(files.Folder(repository), logger) : 0)
                    .ThenBy(repository => repository.NextCheckAt).ThenBy(repository => repository.Id, StringComparer.Ordinal)
                    .DistinctBy(repository => repository.Id).Take(options.Value.BackgroundBatchSize).ToArray();
                await Parallel.ForEachAsync(due, new ParallelOptions
                    { MaxDegreeOfParallelism = options.Value.BackgroundConcurrency, CancellationToken = stoppingToken }, async (repository, workerToken) =>
                {
                    using var repositoryScope = scopes.CreateScope();
                    var coordinator = repositoryScope.ServiceProvider.GetRequiredService<CodeIndexCoordinator>();
                    var repositoryStore = repositoryScope.ServiceProvider.GetRequiredService<ICodeRepositoryStore>();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
                    deadline.CancelAfter(TimeSpan.FromMinutes(15));
                    try { await coordinator.RefreshAsync(repository, deadline.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        var current = (await repositoryStore.RepositoriesAsync(stoppingToken)).FirstOrDefault(entry => entry.Id == repository.Id);
                        if (current?.Status == "indexing" && current.Revision == repository.Revision + 1)
                            await repositoryStore.UpdateAsync(current with { Revision = current.Revision + 1, Status = "error", Message = "Indexing timed out.",
                                NextCheckAt = clock.GetUtcNow().AddMinutes(options.Value.RefreshMinutes) }, current.Revision, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Code refresh job failed for {RepositoryId} ({FailureType}); other repositories continue.",
                            repository.Id, exception.GetType().Name);
                    }
                });
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failures = Math.Min(5, failures + 1);
                logger.LogWarning("Code repository scheduler unavailable ({FailureType}). Check CodeRAG database setup and storage permissions.", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, options.Value.BackgroundCheckSeconds * Math.Pow(2, failures))), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}