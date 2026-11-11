using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PoolSense.Api.Data;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class RetrievalCacheWarmupOptions
{
    public const string SectionName = "RetrievalCacheWarmup";
    public bool Enabled { get; set; } = true;
    public int MaxStartupSnapshots { get; set; } = 8;
    public int MaxConcurrency { get; set; } = 2;
    public int QueueCapacity { get; set; } = 16;
}

public sealed record CodeSnapshotWarmupRequest(string RepositoryId, string Generation);

public interface ICodeSnapshotWarmupQueue
{
    bool TryEnqueue(string repositoryId, string generation);
    ValueTask<CodeSnapshotWarmupRequest> ReadAsync(CancellationToken token);
}

public sealed class CodeSnapshotWarmupQueue : ICodeSnapshotWarmupQueue
{
    private readonly object sync = new();
    private readonly Channel<string> channel;
    private readonly Dictionary<string, CodeSnapshotWarmupRequest> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly RetrievalCacheWarmupOptions options;

    public CodeSnapshotWarmupQueue(IOptions<RetrievalCacheWarmupOptions> options)
    {
        this.options = options.Value;
        channel = Channel.CreateBounded<string>(new BoundedChannelOptions(this.options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public bool TryEnqueue(string repositoryId, string generation)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(generation))
            return false;

        lock (sync)
        {
            if (queued.Contains(repositoryId))
            {
                pending[repositoryId] = new(repositoryId, generation);
                return true;
            }
            if (!channel.Writer.TryWrite(repositoryId)) return false;
            queued.Add(repositoryId);
            pending[repositoryId] = new(repositoryId, generation);
            return true;
        }
    }

    public async ValueTask<CodeSnapshotWarmupRequest> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            var repositoryId = await channel.Reader.ReadAsync(token);
            lock (sync)
            {
                queued.Remove(repositoryId);
                if (pending.Remove(repositoryId, out var request)) return request;
            }
        }
    }
}

public sealed class RetrievalCacheWarmupService(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ICodeSnapshotWarmupQueue queue,
    IOptions<RetrievalCacheWarmupOptions> warmupOptions,
    IOptions<CodeRAGOptions> codeOptions,
    ILogger<RetrievalCacheWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await WaitForApplicationStartedAsync(lifetime, stoppingToken);
            var settings = warmupOptions.Value;
            if (!settings.Enabled)
            {
                logger.LogInformation("Retrieval cache warmup is disabled.");
                return;
            }

            var timer = Stopwatch.StartNew();
            var poolTask = WarmPoolHistoryAsync(stoppingToken);
            var snapshotsTask = WarmStartupSnapshotsAsync(settings, stoppingToken);
            await Task.WhenAll(poolTask, snapshotsTask);
            var pool = await poolTask;
            var snapshots = await snapshotsTask;
            timer.Stop();
            logger.LogInformation(
                "Startup cache warmup completed: selected {SelectedRepositories} repositories, warmed {WarmedRepositories}, semantic snapshots {SemanticSnapshots} ({SemanticBytes} estimated bytes), OKF catalogs {OkfCatalogs} ({OkfBytes} bytes read), Pool history rows {PoolRows} ({PoolVectorBytes} vector bytes), {ElapsedMilliseconds} ms.",
                snapshots.Selected, snapshots.Warmed, snapshots.SemanticCount, snapshots.SemanticBytes,
                snapshots.OkfCount, snapshots.OkfBytes, pool.Rows, pool.VectorBytes, timer.ElapsedMilliseconds);

            var workers = Enumerable.Range(0, settings.MaxConcurrency)
                .Select(_ => ConsumePublishedSnapshotsAsync(stoppingToken)).ToArray();
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<PoolWarmupResult> WarmPoolHistoryAsync(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITicketKnowledgeEmbeddingStore>();
            var rows = await store.GetTicketKnowledgeAsync(token);
            var vectorBytes = rows.Sum(row => (long)row.Embedding.Length * sizeof(float));
            timer.Stop();
            return new(rows.Count, vectorBytes, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            timer.Stop();
            logger.LogWarning("Pool-history cache warmup failed ({FailureType}); the existing lazy load remains available.",
                exception.GetType().Name);
            return new(0, 0, timer.ElapsedMilliseconds);
        }
    }

    private async Task<SnapshotWarmupSummary> WarmStartupSnapshotsAsync(RetrievalCacheWarmupOptions settings,
        CancellationToken token)
    {
        if (!codeOptions.Value.Enabled) return new(0, 0, 0, 0, 0, 0);
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ICodeRepositoryStore>();
            var repositories = (await store.RepositoriesAsync(token))
                .Where(repository => repository.Enabled
                    && repository.Status is "ready" or "queued"
                    && !string.IsNullOrWhiteSpace(repository.Generation)
                    && !string.IsNullOrWhiteSpace(repository.IndexedCommit))
                .OrderBy(repository => repository.LastIndexedAt ?? DateTimeOffset.MinValue)
                .ThenBy(repository => repository.Id, StringComparer.Ordinal)
                .Take(settings.MaxStartupSnapshots).ToArray();

            var results = new List<RepositoryWarmupResult>(repositories.Length);
            foreach (var repository in repositories)
            {
                token.ThrowIfCancellationRequested();
                results.Add(await WarmRepositoryAsync(
                    new(repository.Id, repository.Generation), token));
            }

            return Summarize(repositories.Length, results);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Startup code-cache warmup could not enumerate ready snapshots ({FailureType}); lazy loading remains available.",
                exception.GetType().Name);
            return new(0, 0, 0, 0, 0, 0);
        }
    }

    private async Task ConsumePublishedSnapshotsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var request = await queue.ReadAsync(token);
            await WarmRepositoryAsync(request, token);
        }
    }

    private async Task<RepositoryWarmupResult> WarmRepositoryAsync(CodeSnapshotWarmupRequest request,
        CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICodeRepositoryStore>();
            var repository = (await store.RepositoriesAsync(token)).FirstOrDefault(candidate =>
                candidate.Id == request.RepositoryId);
            if (repository is null || !repository.Enabled
                || repository.Generation != request.Generation
                || repository.Status is not ("ready" or "queued")
                || string.IsNullOrWhiteSpace(repository.IndexedCommit))
                return RepositoryWarmupResult.Skipped;

            var files = scope.ServiceProvider.GetRequiredService<CodeIndexFiles>();
            using var lease = files.Lease(repository);
            if (lease is null) return RepositoryWarmupResult.Skipped;
            var latest = (await store.RepositoriesAsync(token)).FirstOrDefault(candidate =>
                candidate.Id == request.RepositoryId);
            if (latest is null || !latest.Enabled || latest.Generation != request.Generation
                || latest.Status is not ("ready" or "queued")
                || latest.IndexedCommit != repository.IndexedCommit || !files.Ready(latest))
                return RepositoryWarmupResult.Skipped;
            repository = latest;

            var folder = files.Folder(repository);
            var semantic = scope.ServiceProvider.GetRequiredService<CodeSemanticIndex>();
            var semanticResult = File.Exists(Path.Combine(folder, CodeSemanticIndex.ArtifactName))
                ? semantic.WarmSnapshot(folder, repository.IndexedCommit, token)
                : null;

            var knowledgeOptions = scope.ServiceProvider.GetRequiredService<IOptions<OkfOptions>>().Value;
            var okfPath = Path.Combine(folder, "okf");
            var okfResult = knowledgeOptions.Enabled && File.Exists(Path.Combine(okfPath, "index.md"))
                ? OkfBundle.WarmCatalog(okfPath, repository.IndexedCommit, knowledgeOptions, token)
                : null;

            timer.Stop();
            var semanticWarmed = semanticResult is { Status: not "failed" };
            var okfWarmed = okfResult is { Status: not ("missing" or "invalid" or "stale" or "disabled") };
            var result = new RepositoryWarmupResult(
                semanticWarmed || okfWarmed ? 1 : 0,
                semanticWarmed ? 1 : 0,
                semanticResult?.EstimatedBytes ?? 0,
                okfWarmed ? 1 : 0,
                okfResult?.CatalogBytesRead ?? 0,
                timer.ElapsedMilliseconds);
            if (semanticWarmed || okfWarmed)
                logger.LogInformation(
                    "Warmed published cache generation for repository {RepositoryId}: semantic status {SemanticStatus}, {SemanticVectors} vectors, OKF status {OkfStatus}, {ElapsedMilliseconds} ms.",
                    repository.Id, semanticResult?.Status ?? "unavailable", semanticResult?.Vectors ?? 0,
                    okfResult?.Status ?? "unavailable", timer.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            timer.Stop();
            logger.LogWarning("Cache warmup failed for repository {RepositoryId} ({FailureType}); lazy loading remains available.",
                request.RepositoryId, exception.GetType().Name);
            return RepositoryWarmupResult.Skipped;
        }
    }

    private static SnapshotWarmupSummary Summarize(int selected, IReadOnlyList<RepositoryWarmupResult> results) =>
        new(selected, results.Sum(result => result.RepositoriesWarmed),
            results.Sum(result => result.SemanticCount), results.Sum(result => result.SemanticBytes),
            results.Sum(result => result.OkfCount), results.Sum(result => result.OkfBytes));

    private static async Task WaitForApplicationStartedAsync(IHostApplicationLifetime lifetime, CancellationToken token)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var canceledRegistration = token.Register(() => started.TrySetCanceled(token));
        await started.Task;
    }

    private sealed record PoolWarmupResult(int Rows, long VectorBytes, long ElapsedMilliseconds);
    private sealed record SnapshotWarmupSummary(int Selected, int Warmed, int SemanticCount, long SemanticBytes,
        int OkfCount, long OkfBytes);
    private sealed record RepositoryWarmupResult(int RepositoriesWarmed, int SemanticCount, long SemanticBytes,
        int OkfCount, long OkfBytes, long ElapsedMilliseconds)
    {
        public static RepositoryWarmupResult Skipped { get; } = new(0, 0, 0, 0, 0, 0);
    }
}
