using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeQueryEmbeddingCache(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaximumEntries = 256;
    private const long MaximumBytes = 32L * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> flights = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long bytes;
    private long hits;
    private long providerLoads;

    public (long Hits, long ProviderLoads, long Bytes, int Entries) Metrics
    {
        get { lock (sync) return (hits, providerLoads, bytes, entries.Count); }
    }

    public async Task<float[]> GetOrCreateAsync(
        string query,
        string profile,
        string cacheScope,
        int dimensions,
        Func<CancellationToken, Task<float[]>> factory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildKey(query, profile, cacheScope, dimensions);
        lock (sync)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                if (cached.ExpiresAt > clock.GetUtcNow())
                {
                    cached.LastAccess = clock.GetUtcNow();
                    hits++;
                    return cached.Vector.ToArray();
                }
                Remove(key, cached);
            }
        }

        var flight = flights.GetOrAdd(key, _ => new Lazy<Task<CacheEntry>>(
            () => LoadAndRemoveAsync(key, factory, dimensions), LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.Vector.ToArray();
    }

    private async Task<CacheEntry> LoadAndRemoveAsync(
        string key,
        Func<CancellationToken, Task<float[]>> factory,
        int dimensions)
    {
        try
        {
            lock (sync)
            {
                if (entries.TryGetValue(key, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
                {
                    hits++;
                    return cached;
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var vector = await factory(timeout.Token).ConfigureAwait(false);
            if (vector.Length != dimensions || vector.Length == 0
                || vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0))
                throw new InvalidDataException("Invalid query embedding.");

            var copy = vector.ToArray();
            var entry = new CacheEntry(copy, clock.GetUtcNow() + Lifetime, clock.GetUtcNow());
            lock (sync)
            {
                providerLoads++;
                var size = (long)copy.Length * sizeof(float);
                if (size <= MaximumBytes)
                {
                    if (entries.Remove(key, out var previous))
                        bytes -= (long)previous.Vector.Length * sizeof(float);
                    entries[key] = entry;
                    bytes += size;
                    while (entries.Count > MaximumEntries || bytes > MaximumBytes)
                    {
                        var oldest = entries.MinBy(pair => pair.Value.LastAccess);
                        if (oldest.Value is null) break;
                        Remove(oldest.Key, oldest.Value);
                    }
                }
            }
            return entry;
        }
        finally
        {
            flights.TryRemove(key, out _);
        }
    }

    private void Remove(string key, CacheEntry entry)
    {
        if (entries.Remove(key))
            bytes -= (long)entry.Vector.Length * sizeof(float);
    }

    private static string BuildKey(string query, string profile, string cacheScope, int dimensions)
    {
        var identity = JsonSerializer.Serialize(new
        {
            QueryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))),
            Profile = profile,
            ScopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheScope))),
            Dimensions = dimensions
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private sealed class CacheEntry(float[] vector, DateTimeOffset expiresAt, DateTimeOffset lastAccess)
    {
        public float[] Vector { get; } = vector;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
    }
}
