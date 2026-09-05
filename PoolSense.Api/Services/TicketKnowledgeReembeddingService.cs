using System.Collections.Concurrent;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public interface ITicketKnowledgeReembeddingService
{
    /// <summary>Starts the one-time re-embed job if one isn't already running. Returns false if a job is already in progress.</summary>
    bool TryStart();

    TicketKnowledgeReembedJobSnapshot GetStatus();
}

public sealed class TicketKnowledgeReembedJobSnapshot
{
    public bool IsRunning { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<string> FailedTicketIds { get; init; } = [];
}

/// <summary>
/// Tracks progress of the singleton, in-memory re-embed job so it survives across scoped requests.
/// </summary>
public sealed class TicketKnowledgeReembedJobStatus
{
    private readonly object _lock = new();
    private bool _isRunning;
    private int _processed;
    private int _total;
    private int _succeeded;
    private int _failed;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private string? _lastError;
    private IReadOnlyList<string> _failedTicketIds = [];

    public bool TryStart()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return false;
            }

            _isRunning = true;
            _processed = 0;
            _total = 0;
            _succeeded = 0;
            _failed = 0;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _completedAtUtc = null;
            _lastError = null;
            _failedTicketIds = [];
            return true;
        }
    }

    public void SetTotal(int total)
    {
        lock (_lock)
        {
            _total = total;
        }
    }

    public void ReportProgress(int processed, int succeeded, int failed)
    {
        lock (_lock)
        {
            _processed = processed;
            _succeeded = succeeded;
            _failed = failed;
        }
    }

    public void Complete(IReadOnlyList<string> failedTicketIds, string? error = null)
    {
        lock (_lock)
        {
            _isRunning = false;
            _completedAtUtc = DateTimeOffset.UtcNow;
            _failedTicketIds = failedTicketIds;
            _lastError = error;
        }
    }

    public TicketKnowledgeReembedJobSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TicketKnowledgeReembedJobSnapshot
            {
                IsRunning = _isRunning,
                Processed = _processed,
                Total = _total,
                Succeeded = _succeeded,
                Failed = _failed,
                StartedAtUtc = _startedAtUtc,
                CompletedAtUtc = _completedAtUtc,
                LastError = _lastError,
                FailedTicketIds = _failedTicketIds
            };
        }
    }
}

/// <summary>
/// One-time maintenance job that regenerates every stored ticket_knowledge embedding with the
/// currently configured Nyra:EmbeddingModel, fixing similarity search after an embedding model change.
/// Runs in the background via its own DI scope so the triggering HTTP request returns immediately.
/// </summary>
public sealed class TicketKnowledgeReembeddingService : ITicketKnowledgeReembeddingService
{
    private const int MaxDegreeOfParallelism = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TicketKnowledgeReembedJobStatus _status;
    private readonly ILogger<TicketKnowledgeReembeddingService> _logger;

    public TicketKnowledgeReembeddingService(
        IServiceScopeFactory scopeFactory,
        TicketKnowledgeReembedJobStatus status,
        ILogger<TicketKnowledgeReembeddingService> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _logger = logger;
    }

    public bool TryStart()
    {
        if (!_status.TryStart())
        {
            return false;
        }

        _ = Task.Run(RunAsync);
        return true;
    }

    public TicketKnowledgeReembedJobSnapshot GetStatus() => _status.GetSnapshot();

    private async Task RunAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var embeddingStore = scope.ServiceProvider.GetRequiredService<ITicketKnowledgeEmbeddingStore>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var cacheInvalidator = scope.ServiceProvider.GetRequiredService<IVectorStoreCacheInvalidator>();

        try
        {
            var allKnowledge = await embeddingStore.GetTicketKnowledgeAsync();
            _status.SetTotal(allKnowledge.Count);
            _logger.LogInformation("Starting ticket knowledge re-embed job for {Total} row(s).", allKnowledge.Count);

            var succeeded = 0;
            var failed = 0;
            var processed = 0;
            var failedTicketIds = new ConcurrentBag<string>();

            using var throttle = new SemaphoreSlim(MaxDegreeOfParallelism);
            var tasks = allKnowledge.Select(async ticket =>
            {
                await throttle.WaitAsync();
                try
                {
                    var embeddingText = BuildEmbeddingText(ticket);
                    var embedding = await embeddingService.GenerateEmbedding(embeddingText);
                    await embeddingStore.UpdateEmbeddingAsync(ticket.Id, embedding);
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-embed ticket knowledge {TicketId} (row id {Id}).", ticket.TicketId, ticket.Id);
                    failedTicketIds.Add(ticket.TicketId);
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    throttle.Release();
                    var current = Interlocked.Increment(ref processed);
                    _status.ReportProgress(current, succeeded, failed);
                    if (current % 250 == 0 || current == allKnowledge.Count)
                    {
                        _logger.LogInformation(
                            "Ticket knowledge re-embed progress: {Processed}/{Total} (succeeded: {Succeeded}, failed: {Failed}).",
                            current, allKnowledge.Count, succeeded, failed);
                    }
                }
            });

            await Task.WhenAll(tasks);

            cacheInvalidator.Invalidate();
            _status.Complete(failedTicketIds.ToList());
            _logger.LogInformation(
                "Ticket knowledge re-embed job complete. Total: {Total}, Succeeded: {Succeeded}, Failed: {Failed}.",
                allKnowledge.Count, succeeded, failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket knowledge re-embed job failed unexpectedly.");
            _status.Complete([], ex.Message);
        }
    }

    private static string BuildEmbeddingText(TicketKnowledge ticket)
    {
        return $"""
            Problem: {ticket.Problem}
            Root Cause: {ticket.RootCause}
            Resolution: {ticket.Resolution}
            Keywords: {string.Join(" | ", ticket.Keywords)}
            """;
    }
}
