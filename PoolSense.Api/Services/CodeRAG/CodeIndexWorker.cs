using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeIndexFiles(IHostEnvironment environment, IOptions<CodeRAGOptions> options)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string BasePath { get; } = Path.GetFullPath(options.Value.StoragePath, environment.ContentRootPath);
    public string Folder(CodeRepository repository) => Path.Combine(BasePath, repository.Id, repository.Generation);
    public string Source(CodeRepository repository) => Path.Combine(Folder(repository), "source");
    public string Database(CodeRepository repository) => Path.Combine(Folder(repository), "index.sqlite3");
    public bool Ready(CodeRepository repository) => repository.Generation.Length > 0
        && GraphStore.IsReady(Database(repository), [Source(repository)]);

    public void Prune(CodeRepository repository)
    {
        var parent = Path.Combine(BasePath, repository.Id);
        if (!Directory.Exists(parent)) return;
        foreach (var folder in Directory.GetDirectories(parent))
            if (Path.GetFileName(folder) != repository.Generation) Directory.Delete(folder, true);
    }

    public async Task CleanupAsync(ICodeRepositoryStore store, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (!Directory.Exists(BasePath)) return;
            var repositories = await store.RepositoriesAsync(token);
            foreach (var folder in Directory.GetDirectories(BasePath))
            {
                var id = Path.GetFileName(folder);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var repository = repositories.FirstOrDefault(entry => entry.Id == id);
                if (repository is null) Directory.Delete(folder, true);
                else Prune(repository);
            }
        }
        finally { Gate.Release(); }
    }
}

public interface ICodeSnapshotBuilder
{
    Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token);
}

public sealed class CodeSnapshotBuilder(IGitHubCodeClient github, CodeIndexFiles files) : ICodeSnapshotBuilder
{
    public async Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token)
    {
        await github.DownloadAsync(repository.IndexingUser, repository, commit, files.Source(repository), token);
        return (await new RepositoryIndexer().IndexAsync(files.Source(repository), files.Database(repository), true, cancellationToken: token)).Summary;
    }
}

public sealed class CodeIndexCoordinator(ICodeRepositoryStore store, IGitHubCodeClient github,
    ICodeSnapshotBuilder builder, CodeIndexFiles files, IOptions<CodeRAGOptions> options, TimeProvider clock)
{
    public async Task RefreshAsync(CodeRepository original, CancellationToken token)
    {
        if (!original.Enabled) return;
        var working = original with { Revision = original.Revision + 1, Status = "indexing", Message = "" };
        if (!await store.UpdateAsync(working, original.Revision, token)) return;
        try
        {
            var connection = await store.ConnectionAsync(working.IndexingUser, token) ?? throw new CodeAccessException();
            var commit = await github.CommitAsync(working.IndexingUser, working, token);
            var now = clock.GetUtcNow();
            var result = working with { LastCheckedAt = now, NextCheckAt = now.AddMinutes(options.Value.RefreshMinutes), Status = "ready" };
            if (commit != working.IndexedCommit || !files.Ready(working))
            {
                var staged = working with { Generation = Guid.NewGuid().ToString("N") };
                var summary = await builder.BuildAsync(staged, commit, token);
                await github.CommitAsync(working.IndexingUser, working, token);
                var currentConnection = await store.ConnectionAsync(working.IndexingUser, token);
                if (currentConnection != connection) throw new CodeAccessException();
                result = result with
                {
                    Generation = staged.Generation, IndexedCommit = commit, LastIndexedAt = clock.GetUtcNow(),
                    FileCount = summary.FileCount, SymbolCount = summary.SymbolCount
                };
            }
            await files.Gate.WaitAsync(token);
            try
            {
                if (await store.ConnectionAsync(working.IndexingUser, token) != connection) throw new CodeAccessException();
                if (await store.UpdateAsync(result with { Revision = working.Revision + 1 }, working.Revision, token))
                    files.Prune(result);
            }
            finally { files.Gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ICodeRepositoryStore>();
                var coordinator = scope.ServiceProvider.GetRequiredService<CodeIndexCoordinator>();
                await scope.ServiceProvider.GetRequiredService<CodeIndexFiles>().CleanupAsync(store, stoppingToken);
                foreach (var repository in await store.RepositoriesAsync(stoppingToken))
                {
                    if (!repository.Enabled || repository.NextCheckAt > clock.GetUtcNow() && repository.Status != "indexing") continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromMinutes(15));
                    try { await coordinator.RefreshAsync(repository, deadline.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        var current = (await store.RepositoriesAsync(stoppingToken)).FirstOrDefault(entry => entry.Id == repository.Id);
                        if (current?.Status == "indexing" && current.Revision == repository.Revision + 1)
                            await store.UpdateAsync(current with { Revision = current.Revision + 1, Status = "error", Message = "Indexing timed out.",
                                NextCheckAt = clock.GetUtcNow().AddMinutes(options.Value.RefreshMinutes) }, current.Revision, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Code repository scheduler unavailable ({FailureType}). Check CodeRAG database setup and storage permissions.", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}