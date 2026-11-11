using System.Diagnostics;
using PoolSense.Api.Services.CodeRAG;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class CodeQueryEmbeddingCacheTests
{
    private readonly ITestOutputHelper output;

    public CodeQueryEmbeddingCacheTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task ExactQueryAndScopeReuseDefensivelyCopiedVector()
    {
        var cache = new CodeQueryEmbeddingCache();
        var calls = 0;
        Task<float[]> Factory(CancellationToken _) { calls++; return Task.FromResult(new[] { 1f, 0f }); }

        var first = await cache.GetOrCreateAsync("lower water", "profile-a", "user-a", 2, Factory);
        first[0] = 9;
        var second = await cache.GetOrCreateAsync("lower water", "profile-a", "user-a", 2, Factory);
        await cache.GetOrCreateAsync("LOWER water", "profile-a", "user-a", 2, Factory);
        await cache.GetOrCreateAsync("lower water", "profile-a", "user-b", 2, Factory);
        await cache.GetOrCreateAsync("lower water", "profile-b", "user-a", 2, Factory);

        Assert.Equal(new[] { 1f, 0f }, second);
        Assert.Equal(4, calls);
        Assert.Equal(1, cache.Metrics.Hits);
        Assert.Equal(4, cache.Metrics.ProviderLoads);
    }

    [Fact]
    public async Task ConcurrentWaitersShareOneLoadAndCanceledWaiterDoesNotCancelIt()
    {
        var cache = new CodeQueryEmbeddingCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<float[]> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return await complete.Task.WaitAsync(token);
        }

        using var canceled = new CancellationTokenSource();
        var canceledWaiter = cache.GetOrCreateAsync("query", "p", "user", 2, Factory, canceled.Token);
        var healthyWaiter = cache.GetOrCreateAsync("query", "p", "user", 2, Factory);
        await started.Task;
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        complete.SetResult([0, 1]);

        Assert.Equal(new[] { 0f, 1f }, await healthyWaiter);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedAndInvalidLoadsAreNotCached()
    {
        var cache = new CodeQueryEmbeddingCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync(
            "q", "p", "u", 2, _ => throw new InvalidOperationException("temporary")));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrCreateAsync(
            "q", "p", "u", 2, _ => Task.FromResult(new[] { float.NaN, 0f })));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrCreateAsync(
            "wrong-dimensions", "p", "u", 2, _ => Task.FromResult(new[] { 1f })));

        var calls = 0;
        var recovered = await cache.GetOrCreateAsync("q", "p", "u", 2, _ =>
        {
            calls++;
            return Task.FromResult(new[] { 1f, 1f });
        });

        Assert.Equal(new[] { 1f, 1f }, recovered);
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Metrics.Entries);
    }

    [Fact]
    public async Task ExpiredEntriesReloadAndEntryCountRemainsBounded()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = new CodeQueryEmbeddingCache(clock);
        var calls = 0;
        Task<float[]> Provider(CancellationToken _) { calls++; return Task.FromResult(new[] { 1f }); }

        await cache.GetOrCreateAsync("same", "p", "u", 1, Provider);
        clock.Advance(TimeSpan.FromMinutes(11));
        await cache.GetOrCreateAsync("same", "p", "u", 1, Provider);
        for (var i = 0; i < 260; i++)
            await cache.GetOrCreateAsync($"query-{i}", "p", "u", 1, Provider);

        Assert.Equal(262, calls);
        Assert.Equal(256, cache.Metrics.Entries);
        Assert.InRange(cache.Metrics.Bytes, 1, 32L * 1024 * 1024);
    }

    [Fact]
    public async Task ColdWarmBenchmark_ReportsSimulatedProviderDelaySeparately()
    {
        var cache = new CodeQueryEmbeddingCache();
        var calls = 0;
        async Task<float[]> Provider(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(25, token);
            return Enumerable.Repeat(0.25f, 1536).ToArray();
        }

        var timer = Stopwatch.StartNew();
        await cache.GetOrCreateAsync("benchmark question", "profile", "user", 1536, Provider);
        var coldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await cache.GetOrCreateAsync("benchmark question", "profile", "user", 1536, Provider);
        var warmMs = timer.Elapsed.TotalMilliseconds;

        Assert.Equal(1, calls);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            benchmark = "exact-query-embedding",
            coldMilliseconds = Math.Round(coldMs, 3),
            warmMilliseconds = Math.Round(warmMs, 3),
            simulatedProviderDelayMilliseconds = 25,
            providerCallsAvoided = 1,
            cachedBytes = cache.Metrics.Bytes,
            entryCap = 256,
            byteCap = 32 * 1024 * 1024
        }));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
