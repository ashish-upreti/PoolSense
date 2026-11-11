using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class RetrievalCacheWarmupTests
{
    [Fact]
    public async Task HostedWarmupStartDoesNotWaitForApplicationStartedOrCacheLoads()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var lifetime = new TestApplicationLifetime();
        var options = new RetrievalCacheWarmupOptions { Enabled = true };
        var warmup = new RetrievalCacheWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            new CodeSnapshotWarmupQueue(Microsoft.Extensions.Options.Options.Create(options)),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions()),
            NullLogger<RetrievalCacheWarmupService>.Instance);

        await warmup.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.NotNull(warmup.ExecuteTask);
        Assert.False(warmup.ExecuteTask.IsCompleted);
        await warmup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SnapshotQueueCoalescesByRepositoryAndRejectsOverflowWithoutBlocking()
    {
        var queue = new CodeSnapshotWarmupQueue(Microsoft.Extensions.Options.Options.Create(new RetrievalCacheWarmupOptions
        {
            QueueCapacity = 1
        }));

        Assert.True(queue.TryEnqueue("repo-a", "generation-old"));
        Assert.True(queue.TryEnqueue("repo-a", "generation-new"));
        Assert.False(queue.TryEnqueue("repo-b", "generation-b"));

        var request = await queue.ReadAsync(CancellationToken.None);
        Assert.Equal("repo-a", request.RepositoryId);
        Assert.Equal("generation-new", request.Generation);
        Assert.True(queue.TryEnqueue("repo-b", "generation-b"));
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
