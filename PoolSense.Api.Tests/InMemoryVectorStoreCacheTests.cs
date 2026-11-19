using PoolSense.Api.Data;
using PoolSense.Api.Models;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class InMemoryVectorStoreCacheTests
{
    [Fact]
    public async Task WarmedCacheUsesExistingLoadPathAndRespectsUpdatesAndInvalidation()
    {
        var cache = new InMemoryVectorStoreCache();
        var loads = 0;
        Task<IReadOnlyList<TicketKnowledge>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<TicketKnowledge>>(
                [new TicketKnowledge { Id = 7, TicketId = "first", Embedding = [1, 0] }]);
        }

        var warmed = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        var reused = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        Assert.Equal(1, loads);
        Assert.Same(warmed, reused);

        cache.AddOrReplace(new TicketKnowledge { Id = 7, TicketId = "updated", Embedding = [0, 1] });
        var updated = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        Assert.Equal("updated", Assert.Single(updated).TicketId);
        Assert.Equal(1, loads);

        cache.Invalidate();
        var reloaded = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        Assert.Equal(2, loads);
        Assert.Equal("first", Assert.Single(reloaded).TicketId);
    }
}
