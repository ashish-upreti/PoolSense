using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public sealed class InMemoryVectorStoreCache : IVectorStoreCacheInvalidator
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _syncRoot = new();
    private IReadOnlyList<TicketKnowledge>? _ticketKnowledge;
    private long _version;
    private long _loadedVersion = -1;

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
    }

    public async Task<IReadOnlyList<TicketKnowledge>> GetOrLoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<TicketKnowledge>>> loadAsync,
        CancellationToken cancellationToken)
    {
        var currentVersion = Interlocked.Read(ref _version);
        var cachedKnowledge = _ticketKnowledge;
        if (cachedKnowledge is not null && _loadedVersion == currentVersion)
        {
            return cachedKnowledge;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            currentVersion = Interlocked.Read(ref _version);
            if (_ticketKnowledge is not null && _loadedVersion == currentVersion)
            {
                return _ticketKnowledge;
            }

            var loadedKnowledge = await loadAsync(cancellationToken);
            lock (_syncRoot)
            {
                _ticketKnowledge = loadedKnowledge.ToList();
                _loadedVersion = currentVersion;
                return _ticketKnowledge;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void AddOrReplace(TicketKnowledge ticketKnowledge)
    {
        ArgumentNullException.ThrowIfNull(ticketKnowledge);

        lock (_syncRoot)
        {
            if (_ticketKnowledge is null)
            {
                return;
            }

            var updatedKnowledge = _ticketKnowledge.ToList();
            var index = ticketKnowledge.Id > 0
                ? updatedKnowledge.FindIndex(item => item.Id == ticketKnowledge.Id)
                : -1;

            if (index >= 0)
            {
                updatedKnowledge[index] = ticketKnowledge;
            }
            else
            {
                updatedKnowledge.Add(ticketKnowledge);
            }

            _ticketKnowledge = updatedKnowledge;
        }
    }
}