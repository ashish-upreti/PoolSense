using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeSemanticOptions
{
    public bool Enabled { get; set; }
    public bool AdaptiveCoverageEnabled { get; set; }
    public int MaxChunks { get; set; } = 64;
    public int MaxNewEmbeddingsPerBuild { get; set; } = 64;
    public int EmbeddingBatchSize { get; set; } = 8;
    public int MaxTextCharacters { get; set; } = 2000;
    public int Dimensions { get; set; } = 1536;
    public int MaxManifestBytes { get; set; } = 8 * 1024 * 1024;
    public int SmallRepositoryTarget { get; set; } = 128;
    public int MediumRepositoryTarget { get; set; } = 512;
    public int LargeRepositoryTarget { get; set; } = 768;
    public int SmallRepositoryThreshold { get; set; } = 512;
    public int MediumRepositoryThreshold { get; set; } = 4096;
    public int BatchCooldownSeconds { get; set; } = 300;
    public int CandidateLimit { get; set; } = 8192;
    public int CandidateBytes { get; set; } = 16 * 1024 * 1024;
}

public sealed record CodeQueryEmbedding(string Query, string Profile, float[] Vector);
public sealed record CodeSemanticQueryResult(string Status, CodeQueryEmbedding? Embedding = null, string? ErrorType = null);
public sealed record CodeSemanticMatch(string SymbolId, double Score);
public sealed record CodeSemanticCoverage(int IndexedChunks, int EligibleChunks, int TargetChunks,
    bool TargetReached, string SelectionPolicy)
{
    public bool ScanCapped { get; init; }
    public bool CoverageCapped { get; init; }
    public bool LegacyRecoveryPending { get; init; }
    public int UsefulChunks { get; init; }
}
public sealed record CodeSemanticSearchResult(string Status, IReadOnlyList<CodeSemanticMatch> Matches, string? ErrorType = null)
{
    public CodeSemanticCoverage? Coverage { get; init; }
}
public sealed record CodeSemanticWarmupResult(string Status, int Vectors, long EstimatedBytes, bool CacheHit);
public sealed record CodeSemanticBuildResult(string Status, int IndexedChunks, int EligibleChunks,
    int NewEmbeddings, int ReusedEmbeddings, string? ErrorType = null)
{
    public int TargetChunks { get; init; }
    public bool TargetReached { get; init; }
    public string SelectionPolicy { get; init; } = "";
    public bool ScanCapped { get; init; }
    public bool CoverageCapped { get; init; }
    public bool LegacyRecoveryPending { get; init; }
    public int UsefulChunks { get; init; }
}

/// <summary>Bounded offline vectors; request-time search only reads the immutable snapshot artifact.</summary>
public sealed class CodeSemanticIndex
{
    public const string ArtifactName = "semantic.json";
    private const int FormatVersion = 3;
    private const int MaxRawChunkCharacters = 64 * 1024;
    private const long SnapshotCacheByteLimit = 64L * 1024 * 1024;
    private const int SnapshotCacheEntryLimit = 8;
    private static readonly object SnapshotCacheLock = new();
    private static readonly List<SnapshotCacheItem> SnapshotCache = [];
    private static readonly SemaphoreSlim[] SnapshotLoadGates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static long snapshotCacheBytes;
    private static long snapshotCacheHits;
    private static long snapshotCacheLoads;
    private readonly IEmbeddingService embeddings;
    private readonly ILogger<CodeSemanticIndex> logger;
    private readonly CodeQueryEmbeddingCache queryCache;

    public CodeSemanticOptions Options { get; }
    public string Profile
    {
        get
        {
            try { return embeddings.Profile; }
            catch (Exception ex) { Warn("Profile", ex); return string.Empty; }
        }
    }
    public bool Enabled => Options.Enabled;
    public static (long Hits, long Loads, long Bytes, int Entries) SnapshotCacheMetrics
    {
        get { lock (SnapshotCacheLock) return (snapshotCacheHits, snapshotCacheLoads, snapshotCacheBytes, SnapshotCache.Count); }
    }

    public static bool IsSnapshotCached(string folder)
    {
        var fullPath = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (SnapshotCacheLock)
            return SnapshotCache.Any(item => string.Equals(item.Folder, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public static void EvictSnapshotCache(string folder)
    {
        var fullPath = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (SnapshotCacheLock)
        {
            foreach (var item in SnapshotCache.Where(item => string.Equals(item.Folder, fullPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                RemoveSnapshotCacheItem(item);
        }
    }

    public CodeSemanticIndex(IEmbeddingService embeddings, IOptions<CodeSemanticOptions> options,
        ILogger<CodeSemanticIndex> logger, CodeQueryEmbeddingCache? queryCache = null)
    {
        this.embeddings = embeddings;
        this.logger = logger;
        this.queryCache = queryCache ?? new CodeQueryEmbeddingCache();
        var value = options.Value;
        Options = new()
        {
            Enabled = value.Enabled,
            AdaptiveCoverageEnabled = value.AdaptiveCoverageEnabled,
            MaxChunks = Math.Clamp(value.MaxChunks, 1, value.AdaptiveCoverageEnabled ? 768 : 64),
            MaxNewEmbeddingsPerBuild = Math.Clamp(value.MaxNewEmbeddingsPerBuild, 0, 64),
            EmbeddingBatchSize = Math.Clamp(value.EmbeddingBatchSize, 1, 16),
            MaxTextCharacters = Math.Clamp(value.MaxTextCharacters, 1, 8000),
            Dimensions = Math.Clamp(value.Dimensions, 1, 1536),
            MaxManifestBytes = Math.Clamp(value.MaxManifestBytes, 1024, 8 * 1024 * 1024),
            SmallRepositoryTarget = Math.Clamp(value.SmallRepositoryTarget, 1, 768),
            MediumRepositoryTarget = Math.Clamp(value.MediumRepositoryTarget, 1, 768),
            LargeRepositoryTarget = Math.Clamp(value.LargeRepositoryTarget, 1, 768),
            SmallRepositoryThreshold = Math.Clamp(value.SmallRepositoryThreshold, 1, 8192),
            MediumRepositoryThreshold = Math.Clamp(value.MediumRepositoryThreshold, Math.Clamp(value.SmallRepositoryThreshold, 1, 8192), 8192),
            BatchCooldownSeconds = Math.Clamp(value.BatchCooldownSeconds, 60, 3600),
            CandidateLimit = Math.Clamp(value.CandidateLimit, 1, 8192),
            CandidateBytes = Math.Clamp(value.CandidateBytes, 1024, 16 * 1024 * 1024)
        };
    }

    public static string PolicySignature(CodeSemanticOptions options) => Hash(
        $"balanced-source-v3-retention-recovery|{options.AdaptiveCoverageEnabled}|{options.MaxChunks}|{options.SmallRepositoryTarget}|{options.MediumRepositoryTarget}|{options.LargeRepositoryTarget}|{options.SmallRepositoryThreshold}|{options.MediumRepositoryThreshold}|{options.CandidateLimit}|{options.CandidateBytes}|{options.MaxTextCharacters}|{options.Dimensions}|{options.MaxManifestBytes}");

    public string Policy => PolicySignature(Options);

    public async Task<CodeSemanticBuildResult> BuildAsync(string database, string sourceRoot, string snapshotFolder,
        string commit, string? previousSnapshotFolder = null, CancellationToken token = default,
        string? checkpointSnapshotFolder = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Enabled) return new("disabled", 0, 0, 0, 0);
        var profile = Profile;
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 1024)
        {
            Warn("Build", "MissingProfile");
            return new("unavailable", 0, 0, 0, 0, "MissingProfile");
        }
        var entries = new List<Entry>();
        var eligible = 0;
        var generated = 0;
        var reused = 0;
        Manifest? manifest = null;
        try
        {
            if (string.IsNullOrWhiteSpace(commit) || commit.Length > 256)
                throw new InvalidDataException("Invalid commit metadata.");
            // Existing artifacts belong to immutable snapshots, not staging builds.
            if (File.Exists(Path.Combine(snapshotFolder, ArtifactName)))
                throw new InvalidOperationException("Snapshot already contains semantic metadata.");
            if (checkpointSnapshotFolder is not null
                && (SameFolder(checkpointSnapshotFolder, snapshotFolder)
                    || (previousSnapshotFolder is not null && SameFolder(checkpointSnapshotFolder, previousSnapshotFolder))))
                throw new InvalidOperationException("Private checkpoint must not overwrite a snapshot.");
            var rootHash = Hash(GraphStore.RootKey(sourceRoot));
            var cached = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var priorEntries = new List<Entry>();
            var legacyCache = false;
            var recoverLegacyPrefix = false;
            foreach (var cacheFolder in new[] { previousSnapshotFolder, checkpointSnapshotFolder }.Distinct())
            {
                if (cacheFolder is null) continue;
                try
                {
                    var previous = Read(cacheFolder, profile, null, token);
                    if (previous is not null)
                    {
                        legacyCache |= previous.Version == 2;
                        // Pre-retention V3 can lack vectors for the original same-head prefix.
                        // Restore eligibility, not old vector bytes; missing vectors use the ordinary batch budget.
                        recoverLegacyPrefix |= previous.Version == 3 && previous.Commit == commit
                            && (previous.RetentionPolicyVersion == 0 || previous.LegacyRecoveryPending);
                        foreach (var entry in previous.Entries)
                        {
                            cached.TryAdd(entry.ContentHash, entry.Vector);
                            priorEntries.Add(entry);
                        }
                        HydrateLocators(cacheFolder, previous, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { Warn("PreviousArtifact", ex); }
            }
            // Snapshot source paths change every refresh; cache identity remains content plus profile.
            using var db = GraphStore.Open(database, readOnly: true);
            if (!Options.AdaptiveCoverageEnabled)
                eligible = Convert.ToInt32(GraphStore.Scalar(db,
                    "SELECT min(count(*),2147483647) FROM symbols WHERE repo_root=@p0 AND source<>''", GraphStore.RootKey(sourceRoot)));
            // RepositoryIndexer hashes symbol IDs. Walk that index, not alphabetic paths or an unbounded SQL sort.
            using var command = GraphStore.Command(db, Options.AdaptiveCoverageEnabled ? """
                SELECT substr(id,1,1025),substr(qualified_name,1,65536),substr(docstring,1,65536),
                    substr(source,1,65536),substr(file_path,1,4097),
                    length(qualified_name)+length(docstring)+length(source),start_line
                FROM symbols INDEXED BY sqlite_autoindex_symbols_1 WHERE repo_root=@p0 AND source<>''
                ORDER BY id LIMIT @p2
                """ : """
                SELECT id,qualified_name,docstring,source,file_path,start_line
                FROM symbols WHERE repo_root=@p0 AND source<>''
                AND length(qualified_name)+length(docstring)+length(source)<=@p1
                ORDER BY file_path,start_line,id LIMIT @p2
                """, GraphStore.RootKey(sourceRoot), MaxRawChunkCharacters,
                Options.AdaptiveCoverageEnabled ? Options.CandidateLimit + 1 : Options.MaxChunks);
            var chunks = new List<CodeSemanticSelection.Candidate>();
            var scanCapped = false;
            var scanned = 0;
            long candidateBytes = 0;
            var unique = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var verifiedHashes = new HashSet<string>(StringComparer.Ordinal);
            if (Options.AdaptiveCoverageEnabled && (cached.Count > 0 || recoverLegacyPrefix))
            {
                // Verify old content before applying NEW-candidate noise and sampling policies.
                // Locators are root-independent; old artifacts can recover them from their immutable database.
                foreach (var prior in priorEntries.DistinctBy(entry =>
                    (entry.FilePath, entry.StartLine, Id: entry.FilePath is null ? entry.SymbolId : null)))
                {
                    var predicate = prior.FilePath is null
                        ? "repo_root=@p0 AND id=@p3" : "repo_root=@p0 AND file_path=@p1 AND start_line=@p2";
                    using var retained = GraphStore.Command(db, $"""
                        SELECT substr(id,1,1025),substr(qualified_name,1,65536),substr(docstring,1,65536),
                            substr(source,1,65536),substr(file_path,1,4097),
                            length(qualified_name)+length(docstring)+length(source),start_line
                        FROM symbols WHERE {predicate} AND source<>''
                        LIMIT @p4
                        """, GraphStore.RootKey(sourceRoot), prior.FilePath, prior.StartLine, prior.SymbolId,
                        Options.CandidateLimit + 1);
                    using var retainedReader = retained.ExecuteReader();
                    while (retainedReader.Read())
                    {
                        if (!Accept(retainedReader))
                            throw new RetentionBudgetException();
                    }
                }
                if (legacyCache || recoverLegacyPrefix)
                {
                    // The original policy selected these 64 bounded source spans. V2 reuses
                    // their cached vectors; same-head early-V3 migration can repair missing eligibility.
                    using var legacy = GraphStore.Command(db, """
                        SELECT id,qualified_name,docstring,source,file_path,
                            length(qualified_name)+length(docstring)+length(source),start_line
                        FROM symbols WHERE repo_root=@p0 AND source<>''
                            AND length(qualified_name)+length(docstring)+length(source)<=@p1
                        ORDER BY file_path,start_line,id LIMIT 64
                        """, GraphStore.RootKey(sourceRoot), MaxRawChunkCharacters);
                    using var legacyReader = legacy.ExecuteReader();
                    while (legacyReader.Read())
                        if (!Accept(legacyReader, recoverLegacyPrefix)) throw new RetentionBudgetException();
                }
            }
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!Accept(reader)) { scanCapped = true; break; }
                }
            }
            bool Accept(Microsoft.Data.Sqlite.SqliteDataReader reader, bool recovery = false)
            {
                token.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                var firstSeen = seenIds.Add(id);
                if (!firstSeen)
                {
                    if (!recovery) return true;
                    var existing = chunks.FindIndex(candidate => candidate.Id == id);
                    if (existing >= 0)
                    {
                        chunks[existing] = chunks[existing] with { RecoveryPriority = true };
                        return true;
                    }
                }
                if (Options.AdaptiveCoverageEnabled && firstSeen && ++scanned > Options.CandidateLimit) return false;
                var path = reader.GetString(4);
                var raw = $"{reader.GetString(1)}\n{reader.GetString(2)}\n{reader.GetString(3)}";
                string? sourceHash = null;
                if (Options.AdaptiveCoverageEnabled)
                {
                    if (firstSeen)
                        candidateBytes += Encoding.UTF8.GetByteCount(raw) + Encoding.UTF8.GetByteCount(path)
                            + Encoding.UTF8.GetByteCount(id);
                    if (candidateBytes > Options.CandidateBytes) return false;
                    if (reader.GetInt64(5) > MaxRawChunkCharacters) return true;
                }
                if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || path.Length > 4096) return true;
                var text = SourceText.Redact(raw);
                text = text[..Math.Min(text.Length, Options.MaxTextCharacters)];
                var hash = Hash(profile + "\n" + text);
                var previousProfile = embeddings.PreviousProfile;
                var previousHash = string.IsNullOrWhiteSpace(previousProfile) || previousProfile == profile
                    ? null : Hash(previousProfile + "\n" + text);
                if (!cached.ContainsKey(hash) && previousHash is not null
                    && cached.TryGetValue(previousHash, out var previousVector))
                    cached.Add(hash, previousVector);
                var useful = CodeSemanticSelection.Useful(path, reader.GetString(3));
                if (Options.AdaptiveCoverageEnabled)
                {
                    if (cached.ContainsKey(hash))
                        verifiedHashes.Add(previousHash is not null && cached.ContainsKey(previousHash) ? previousHash : hash);
                    if (!recovery && !cached.ContainsKey(hash) && !useful) return true;
                    sourceHash = Hash(CodeSemanticSelection.UniqueSource(SourceText.Redact(reader.GetString(3))));
                }
                var candidate = new CodeSemanticSelection.Candidate(id, text, hash, path,
                    reader.GetInt32(Options.AdaptiveCoverageEnabled ? 6 : 5), recovery, useful);
                if (sourceHash is not null && unique.TryGetValue(sourceHash, out var duplicate))
                {
                    if (cached.ContainsKey(hash) && !cached.ContainsKey(chunks[duplicate].Hash))
                        chunks[duplicate] = candidate with
                        {
                            RecoveryPriority = recovery || chunks[duplicate].RecoveryPriority,
                            UsefulForSelection = useful || chunks[duplicate].UsefulForSelection
                        };
                    else
                        chunks[duplicate] = chunks[duplicate] with
                        {
                            RecoveryPriority = recovery || chunks[duplicate].RecoveryPriority,
                            UsefulForSelection = useful || chunks[duplicate].UsefulForSelection
                        };
                    return true;
                }
                if (scanCapped && priorEntries.Any(entry => entry.FilePath is null && !verifiedHashes.Contains(entry.ContentHash)))
                    throw new RetentionBudgetException();
                if (sourceHash is not null) unique.Add(sourceHash, chunks.Count);
                chunks.Add(candidate);
                return true;
            }
            if (Options.AdaptiveCoverageEnabled) eligible = chunks.Count;
            var usefulChunks = chunks.Count(candidate => candidate.UsefulForSelection);
            var recoveryIds = chunks.Where(candidate => candidate.RecoveryPriority)
                .Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            var target = Options.AdaptiveCoverageEnabled
                ? Math.Min(Options.MaxChunks, Math.Min(eligible, usefulChunks <= Options.SmallRepositoryThreshold
                    ? Options.SmallRepositoryTarget : usefulChunks <= Options.MediumRepositoryThreshold
                        ? Options.MediumRepositoryTarget : Options.LargeRepositoryTarget))
                : Math.Min(Options.MaxChunks, eligible);
            if (Options.AdaptiveCoverageEnabled)
                target = Math.Max(target, Math.Min(Options.MaxChunks,
                    chunks.Count(chunk => cached.ContainsKey(chunk.Hash) || chunk.RecoveryPriority)));
            if (Options.AdaptiveCoverageEnabled)
                chunks = CodeSemanticSelection.Select(chunks, cached.Keys, target);
            foreach (var chunk in chunks)
            {
                if (!cached.TryGetValue(chunk.Hash, out var vector)) continue;
                entries.Add(EntryFor(chunk, vector));
                reused++;
            }
            manifest = new Manifest
            {
                Profile = profile, Dimensions = Options.Dimensions, Commit = commit, RootHash = rootHash,
                EligibleChunks = eligible, Entries = entries, Status = "partial", TargetChunks = target,
                SelectionPolicy = Policy, ScanCapped = scanCapped, CoverageCapped = scanCapped || eligible > target,
                RetentionPolicyVersion = 1, UsefulChunks = usefulChunks, RecoveryRequiredIds = recoveryIds
            };
            await SaveAsync(snapshotFolder, manifest, token, checkpointSnapshotFolder);
            string? error = null;
            var missing = chunks.Where(chunk => !cached.ContainsKey(chunk.Hash)).ToArray();
            var position = 0;
            var batchProvider = embeddings as IEmbeddingBatchService;
            while (position < missing.Length)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var chunk = missing[position];
                    if (cached.TryGetValue(chunk.Hash, out var sameTextVector))
                    {
                        entries.Add(EntryFor(chunk, sameTextVector));
                        try { await SaveAsync(snapshotFolder, manifest, token, checkpointSnapshotFolder); }
                        catch { entries.RemoveAt(entries.Count - 1); throw; }
                        reused++;
                        position++;
                        continue;
                    }
                    if (generated >= Options.MaxNewEmbeddingsPerBuild) break;
                    if (Profile != profile)
                    {
                        error = "ProfileChanged";
                        Warn("Build", error);
                        break;
                    }
                    var batch = new List<CodeSemanticSelection.Candidate>();
                    var capacity = Math.Min(batchProvider is null ? 1 : Options.EmbeddingBatchSize, Options.MaxNewEmbeddingsPerBuild - generated);
                    var checkpointCount = entries.Count;
                    while (position < missing.Length && batch.Count < capacity)
                    {
                        var next = missing[position];
                        if (cached.ContainsKey(next.Hash) || batch.Any(item => item.Hash == next.Hash)) break;
                        // Reserve exact fixed-width float32 space before paying for the batch.
                        entries.Add(EntryFor(next, new float[Options.Dimensions]));
                        if (Serialized(manifest).Length + 256 > Options.MaxManifestBytes)
                        {
                            entries.RemoveAt(entries.Count - 1);
                            error = "ManifestSizeLimit";
                            break;
                        }
                        batch.Add(next);
                        position++;
                    }
                    entries.RemoveRange(checkpointCount, entries.Count - checkpointCount);
                    if (batch.Count == 0) break;
                    IReadOnlyList<float[]> vectors = batchProvider is null
                        ? [await embeddings.GenerateEmbedding(batch[0].Text, token)]
                        : await batchProvider.GenerateEmbeddingsAsync(batch.Select(item => item.Text).ToArray(), token);
                    token.ThrowIfCancellationRequested();
                    if (Profile != profile)
                    {
                        error = "ProfileChanged";
                        Warn("Build", error);
                        break;
                    }
                    if (vectors.Count != batch.Count || vectors.Any(vector => !ValidVector(vector)))
                        throw new InvalidDataException("Invalid embedding batch.");
                    for (var index = 0; index < batch.Count; index++) entries.Add(EntryFor(batch[index], vectors[index]));
                    try { await SaveAsync(snapshotFolder, manifest, token, checkpointSnapshotFolder); }
                    catch { entries.RemoveRange(checkpointCount, entries.Count - checkpointCount); throw; }
                    for (var index = 0; index < batch.Count; index++) cached.TryAdd(batch[index].Hash, vectors[index]);
                    generated += batch.Count;
                    if (error is not null) break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = ex.GetType().Name; Warn("Build", ex); break; }
            }
            manifest.Status = error == "ProfileChanged" ? "partial"
                : entries.Count == eligible && !scanCapped && !RecoveryPending(manifest) ? "ready"
                    : entries.Count > 0 ? "partial" : error is null ? "partial" : "failed";
            manifest.ErrorType = error;
            await SaveAsync(snapshotFolder, manifest, token, checkpointSnapshotFolder);
            return Result(manifest.Status, error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Warn("Build", ex);
            return Result("failed", ex.GetType().Name);
        }

        CodeSemanticBuildResult Result(string status, string? error) => new(status, entries.Count, eligible, generated, reused, error)
        {
            TargetChunks = manifest?.TargetChunks ?? 0,
            TargetReached = manifest is not null && entries.Count >= manifest.TargetChunks && !RecoveryPending(manifest),
            SelectionPolicy = Policy, ScanCapped = manifest?.ScanCapped ?? false, CoverageCapped = manifest?.CoverageCapped ?? false,
            LegacyRecoveryPending = manifest is not null && RecoveryPending(manifest), UsefulChunks = manifest?.UsefulChunks ?? 0
        };
    }

    public async Task<CodeSemanticQueryResult> CreateQueryAsync(string query, CodeQueryEmbedding? reusable = null,
        CancellationToken token = default, string cacheScope = "local")
    {
        token.ThrowIfCancellationRequested();
        if (!Enabled) return new("disabled");
        var profile = Profile;
        if (string.IsNullOrWhiteSpace(profile))
        {
            Warn("Query", "MissingProfile");
            return new("unavailable", ErrorType: "MissingProfile");
        }
        if (string.IsNullOrWhiteSpace(query) || query.Length > Options.MaxTextCharacters)
            return new("unavailable", ErrorType: "QueryLength");
        if (reusable is not null && reusable.Query == query && reusable.Profile == profile && ValidVector(reusable.Vector))
            return new("ready", reusable);
        try
        {
            if (Profile != profile)
            {
                Warn("Query", "ProfileChanged");
                return new("unavailable", ErrorType: "ProfileChanged");
            }
            var vector = await queryCache.GetOrCreateAsync(query, embeddings.QueryCacheProfile, cacheScope, Options.Dimensions,
                ct => embeddings.GenerateEmbedding(query, ct), token);
            token.ThrowIfCancellationRequested();
            if (Profile != profile)
            {
                Warn("Query", "ProfileChanged");
                return new("unavailable", ErrorType: "ProfileChanged");
            }
            if (!ValidVector(vector)) throw new InvalidDataException("Invalid query embedding.");
            return new("ready", new(query, profile, vector));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Warn("Query", ex); return new("failed", ErrorType: ex.GetType().Name); }
    }

    public CodeSemanticSearchResult Search(string snapshotFolder, CodeQueryEmbedding queryVector,
        CancellationToken token = default, string? expectedCommit = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Enabled) return new("disabled", []);
        var profile = Profile;
        if (string.IsNullOrWhiteSpace(profile))
        {
            Warn("Search", "MissingProfile");
            return new("unavailable", [], "MissingProfile");
        }
        if (queryVector.Profile != profile || !ValidVector(queryVector.Vector))
            return new("incompatible", [], "QueryProfileOrVector");
        try
        {
            var manifest = ReadCached(snapshotFolder, profile, expectedCommit, token);
            if (manifest is null)
            {
                Warn("Search", "MissingArtifact");
                return new("unavailable", [], "MissingArtifact");
            }
            var matches = new List<CodeSemanticMatch>();
            var queryNorm = Norm(queryVector.Vector);
            foreach (var entry in manifest.Entries)
            {
                token.ThrowIfCancellationRequested();
                matches.Add(new(entry.SymbolId, Cosine(queryVector.Vector, entry.Vector, queryNorm, entry.VectorNorm)));
            }
            return new(manifest.Status, matches.OrderByDescending(match => match.Score)
                .ThenBy(match => match.SymbolId, StringComparer.Ordinal).ToArray(), manifest.ErrorType)
                { Coverage = Coverage(manifest) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Warn("Search", ex); return new("invalid", [], ex.GetType().Name); }
    }

    public CodeSemanticWarmupResult? WarmSnapshot(string snapshotFolder, string expectedCommit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Enabled || string.IsNullOrWhiteSpace(Profile)) return null;
        var manifest = ReadCached(snapshotFolder, Profile, expectedCommit, out var cacheHit, token);
        if (manifest is null || manifest.Status == "failed")
        {
            if (manifest is not null) EvictSnapshotCache(snapshotFolder);
            return null;
        }
        return new(manifest.Status, manifest.Entries.Count, EstimateManifestBytes(manifest), cacheHit);
    }

    public CodeSemanticCoverage? ReadCoverage(string folder, string? expectedCommit = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Enabled || string.IsNullOrWhiteSpace(Profile)) return null;
        try
        {
            var manifest = ReadCached(folder, Profile, expectedCommit, token);
            return manifest is null ? null : Coverage(manifest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Warn("Coverage", ex); return null; }
    }

    private static CodeSemanticCoverage Coverage(Manifest manifest) => new(
        manifest.Entries.Count, manifest.EligibleChunks, manifest.TargetChunks,
        manifest.Entries.Count >= manifest.TargetChunks && !manifest.LegacyRecoveryPending, manifest.SelectionPolicy)
        {
            ScanCapped = manifest.ScanCapped, CoverageCapped = manifest.CoverageCapped,
            LegacyRecoveryPending = manifest.LegacyRecoveryPending, UsefulChunks = manifest.UsefulChunks
        };

    private Manifest? ReadCached(string folder, string profile, string? commit, CancellationToken token) =>
        ReadCached(folder, profile, commit, out _, token);

    private Manifest? ReadCached(string folder, string profile, string? commit, out bool cacheHit, CancellationToken token)
    {
        cacheHit = false;
        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var artifact = Path.Combine(fullFolder, ArtifactName);
        var info = new FileInfo(artifact);
        if (!info.Exists) return null;
        var key = SnapshotCacheKey(fullFolder, profile, commit, info);

        if (TryGetSnapshotCache(key, out var cached))
        {
            cacheHit = true;
            return cached;
        }

        var gateIndex = (int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(artifact) % (uint)SnapshotLoadGates.Length);
        var loadGate = SnapshotLoadGates[gateIndex];
        loadGate.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            info.Refresh();
            if (!info.Exists) return null;
            key = SnapshotCacheKey(fullFolder, profile, commit, info);
            if (TryGetSnapshotCache(key, out cached))
            {
                cacheHit = true;
                return cached;
            }

            lock (SnapshotCacheLock)
                foreach (var stale in SnapshotCache.Where(item =>
                    string.Equals(item.ArtifactPath, artifact, StringComparison.OrdinalIgnoreCase)).ToArray())
                    RemoveSnapshotCacheItem(stale);

            var manifest = Read(fullFolder, profile, commit, token);
            if (manifest is null) return null;
            foreach (var entry in manifest.Entries)
                entry.VectorNorm = Norm(entry.Vector);

            var estimatedBytes = EstimateManifestBytes(manifest);
            info.Refresh();
            var currentKey = info.Exists ? SnapshotCacheKey(fullFolder, profile, commit, info) : "";
            lock (SnapshotCacheLock)
            {
                snapshotCacheLoads++;
                if (currentKey == key && estimatedBytes <= SnapshotCacheByteLimit)
                {
                    var item = new SnapshotCacheItem(key, fullFolder, artifact, manifest, estimatedBytes);
                    SnapshotCache.Add(item);
                    snapshotCacheBytes += estimatedBytes;
                    while (SnapshotCache.Count > SnapshotCacheEntryLimit || snapshotCacheBytes > SnapshotCacheByteLimit)
                        RemoveSnapshotCacheItem(SnapshotCache[0]);
                }
            }
            return manifest;
        }
        finally { loadGate.Release(); }
    }

    private static bool TryGetSnapshotCache(string key, out Manifest? manifest)
    {
        lock (SnapshotCacheLock)
        {
            var hit = SnapshotCache.FirstOrDefault(item => item.Key == key);
            if (hit is not null)
            {
                SnapshotCache.Remove(hit);
                SnapshotCache.Add(hit);
                snapshotCacheHits++;
                manifest = hit.Manifest;
                return true;
            }
            manifest = null;
            return false;
        }
    }

    private string SnapshotCacheKey(string fullFolder, string profile, string? commit, FileInfo info) =>
        JsonSerializer.Serialize(new
        {
            Folder = fullFolder,
            Profile = profile,
            Commit = commit,
            PreviousProfile = embeddings.PreviousProfile,
            Options.Dimensions,
            Options.MaxManifestBytes,
            info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            CreationUtcTicks = info.CreationTimeUtc.Ticks
        });

    private static long EstimateManifestBytes(Manifest manifest) => manifest.Entries.Sum(entry =>
        (long)entry.Vector.Length * sizeof(float) + entry.SymbolId.Length * sizeof(char)
        + (entry.FilePath?.Length ?? 0) * sizeof(char) + 128);

    private static void RemoveSnapshotCacheItem(SnapshotCacheItem item)
    {
        if (SnapshotCache.Remove(item))
            snapshotCacheBytes -= item.EstimatedBytes;
    }

    private Manifest? Read(string folder, string profile, string? commit, CancellationToken token)
    {
        var path = Path.Combine(folder, ArtifactName);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Options.MaxManifestBytes) throw new InvalidDataException("Artifact exceeds size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
        {
            var root = document.RootElement;
            var version = root.GetProperty("Version").GetInt32();
            var entries = root.GetProperty("Entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > (version == 2 ? 64 : 768))
                throw new InvalidDataException("Invalid entry count.");
            foreach (var entry in entries.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var vector = entry.GetProperty("Vector");
                if ((version == 2 && (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() > 1536))
                    || (version == 3 && vector.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("Invalid vector encoding.");
            }
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, CompactJson)
            ?? throw new InvalidDataException("Missing artifact.");
        if (manifest.Version is not (2 or FormatVersion)
            || manifest.Profile != profile && manifest.Profile != embeddings.PreviousProfile
            || manifest.Dimensions != Options.Dimensions
            || manifest.Profile.Length > 1024
            || string.IsNullOrWhiteSpace(manifest.Commit) || manifest.Commit.Length > 256
            || (commit is not null && manifest.Commit != commit)
            || !IsHash(manifest.RootHash) || manifest.Entries is null || manifest.Entries.Count > 768
            || manifest.EligibleChunks < manifest.Entries.Count
            || manifest.EligibleChunks < 0
            || manifest.RetentionPolicyVersion is < 0 or > 1
            || (manifest.RetentionPolicyVersion == 0 && (manifest.LegacyRecoveryPending || manifest.UsefulChunks != 0))
            || manifest.UsefulChunks < 0 || manifest.UsefulChunks > manifest.EligibleChunks
            || (manifest.ErrorType is not null && manifest.ErrorType.Length > 256)
            || (manifest.Version == 3 && (manifest.TargetChunks < 0 || manifest.TargetChunks > 768
                || manifest.TargetChunks > manifest.EligibleChunks || manifest.Entries.Count > manifest.TargetChunks
                || !IsHash(manifest.SelectionPolicy)
                || manifest.TargetReached != (manifest.Entries.Count >= manifest.TargetChunks && !manifest.LegacyRecoveryPending)
                || manifest.CoverageCapped != (manifest.ScanCapped || manifest.EligibleChunks > manifest.TargetChunks)))
            || manifest.Status is not ("ready" or "partial" or "failed")
            || (manifest.Status == "ready" && (manifest.EligibleChunks != manifest.Entries.Count
                || manifest.ScanCapped || manifest.LegacyRecoveryPending))
            || (manifest.Status == "failed" && manifest.Entries.Count != 0)
            || manifest.ContentHash != ContentHash(manifest))
            throw new InvalidDataException("Incompatible artifact metadata.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.SymbolId) || entry.SymbolId.Length > 1024
                || !ids.Add(entry.SymbolId) || !IsHash(entry.ContentHash) || !ValidVector(entry.Vector)
                || (entry.FilePath is not null && entry.FilePath.Length is < 1 or > 4096)
                || entry.StartLine < 0)
                throw new InvalidDataException("Invalid artifact entry.");
        }
        if (manifest.Version == 2)
        {
            manifest.TargetChunks = Math.Min(64, manifest.EligibleChunks);
            manifest.SelectionPolicy = "legacy-fixed-v2";
            manifest.CoverageCapped = manifest.EligibleChunks > manifest.TargetChunks;
        }
        return manifest;
    }

    private static bool SameFolder(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static Entry EntryFor(CodeSemanticSelection.Candidate candidate, float[] vector) =>
        new(candidate.Id, candidate.Hash, vector) { FilePath = candidate.Path, StartLine = candidate.StartLine };

    private void HydrateLocators(string folder, Manifest manifest, CancellationToken token)
    {
        if (manifest.Entries.All(entry => entry.FilePath is not null)) return;
        foreach (var path in Directory.EnumerateFiles(folder).Where(path =>
            Path.GetExtension(path) is ".sqlite3" or ".sqlite" or ".db").Take(4))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var db = GraphStore.Open(path, readOnly: true);
                var roots = GraphStore.Query(db, "SELECT root FROM repositories LIMIT 2", reader => reader.GetString(0));
                var root = roots.FirstOrDefault(root => Hash(root) == manifest.RootHash);
                if (root is null) continue;
                foreach (var entry in manifest.Entries.Where(entry => entry.FilePath is null))
                {
                    token.ThrowIfCancellationRequested();
                    using var command = GraphStore.Command(db,
                        "SELECT file_path,start_line FROM symbols WHERE id=@p0 AND repo_root=@p1", entry.SymbolId, root);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) continue;
                    var file = reader.GetString(0);
                    var line = reader.GetInt32(1);
                    if (file.Length is < 1 or > 4096 || line < 0) continue;
                    entry.FilePath = file;
                    entry.StartLine = line;
                }
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Warn("PreviousLocators", ex); }
        }
    }

    private async Task SaveAsync(string folder, Manifest manifest, CancellationToken token, string? checkpointFolder = null)
    {
        var bytes = Serialized(manifest);
        if (bytes.Length > Options.MaxManifestBytes) throw new InvalidDataException("Artifact exceeds size limit.");
        // The stable private copy survives pruning of an interrupted staging snapshot.
        // Serialize once; atomically replace each file, durable/private first.
        if (checkpointFolder is not null)
            await WriteCheckpointAsync(checkpointFolder, bytes, token);
        await WriteCheckpointAsync(folder, bytes, token);
    }

    private static async Task WriteCheckpointAsync(string folder, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, ArtifactName + "." + Guid.NewGuid().ToString("N") + ".staging");
        try
        {
            await File.WriteAllBytesAsync(staging, bytes, token);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(staging, Path.Combine(folder, ArtifactName), overwrite: true);
                    break;
                }
                // Windows scanners can briefly hold a checkpoint open. Retry the write, never the paid call.
                catch (Exception ex) when (attempt < 4 && (ex is IOException or UnauthorizedAccessException)
                    && ((ex.HResult & 0xffff) is 5 or 32 or 33))
                {
                    await Task.Delay(25 << attempt, token);
                }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private bool ValidVector(float[]? vector) => vector is not null && vector.Length == Options.Dimensions
        && vector.All(float.IsFinite) && vector.Any(value => value != 0);

    private static double Norm(float[] vector)
    {
        double squared = 0;
        for (var i = 0; i < vector.Length; i++)
            squared += (double)vector[i] * vector[i];
        return Math.Sqrt(squared);
    }

    private static double Cosine(float[] left, float[] right, double leftNorm, double rightNorm)
    {
        double dot = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
        }
        return Math.Clamp(dot / (leftNorm * rightNorm), -1, 1);
    }

    private void Warn(string operation, Exception ex) => Warn(operation, ex.GetType().Name);

    private void Warn(string operation, string failureType) =>
        logger.LogWarning("Code semantic {Operation} unavailable ({FailureType}); lexical and graph retrieval remain available.",
            operation, failureType);

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        MaxDepth = 16, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new VectorConverter() }
    };
    private static byte[] Serialized(Manifest manifest)
    {
        manifest.LegacyRecoveryPending = RecoveryPending(manifest);
        manifest.TargetReached = manifest.Entries.Count >= manifest.TargetChunks && !manifest.LegacyRecoveryPending;
        manifest.ContentHash = ContentHash(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, CompactJson);
    }

    private static bool RecoveryPending(Manifest manifest) => manifest.RecoveryRequiredIds.Count > 0
        ? manifest.RecoveryRequiredIds.Any(id => !manifest.Entries.Any(entry => entry.SymbolId == id))
        : manifest.LegacyRecoveryPending;

    private static string ContentHash(Manifest manifest) => manifest.Version == 2 ? Hash(JsonSerializer.Serialize(new
    {
        manifest.Version, manifest.Profile, manifest.Dimensions, manifest.Commit, manifest.RootHash,
        manifest.EligibleChunks, manifest.Status, manifest.ErrorType,
        Entries = manifest.Entries.Select(entry => new { entry.SymbolId, entry.ContentHash, entry.Vector })
    })) : manifest.RetentionPolicyVersion == 0 ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        manifest.Version, manifest.Profile, manifest.Dimensions, manifest.Commit, manifest.RootHash,
        manifest.EligibleChunks, manifest.Status, manifest.ErrorType, manifest.Entries,
        manifest.TargetChunks, manifest.TargetReached, manifest.SelectionPolicy, manifest.ScanCapped, manifest.CoverageCapped
    }, CompactJson))) : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        manifest.Version, manifest.Profile, manifest.Dimensions, manifest.Commit, manifest.RootHash,
        manifest.EligibleChunks, manifest.Status, manifest.ErrorType, manifest.Entries,
        manifest.TargetChunks, manifest.TargetReached, manifest.SelectionPolicy, manifest.ScanCapped, manifest.CoverageCapped,
        manifest.RetentionPolicyVersion, manifest.LegacyRecoveryPending, manifest.UsefulChunks
    }, CompactJson)));

    private sealed class VectorConverter : JsonConverter<float[]>
    {
        public override float[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var values = new List<float>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (values.Count == 1536 || !reader.TryGetSingle(out var value))
                        throw new InvalidDataException("Invalid vector.");
                    values.Add(value);
                }
                return values.ToArray();
            }
            if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 8192)
                throw new InvalidDataException("Invalid encoded vector.");
            var bytes = reader.GetBytesFromBase64();
            if (bytes.Length == 0 || bytes.Length % 4 != 0 || bytes.Length > 6144)
                throw new InvalidDataException("Invalid encoded vector length.");
            var vector = new float[bytes.Length / 4];
            for (var i = 0; i < vector.Length; i++)
                vector[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
            return vector;
        }

        public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
        {
            var bytes = new byte[value.Length * 4];
            for (var i = 0; i < value.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits(value[i]));
            writer.WriteBase64StringValue(bytes);
        }
    }

    private sealed class RetentionBudgetException : Exception { }
    private sealed record Entry(string SymbolId, string ContentHash, float[] Vector)
    {
        [JsonIgnore]
        public double VectorNorm { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FilePath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int StartLine { get; set; }
    }
    private sealed class Manifest
    {
        public Manifest() { }

        public int Version { get; set; } = FormatVersion;
        public string Profile { get; set; } = "";
        public int Dimensions { get; set; }
        public string Commit { get; set; } = "";
        public string RootHash { get; set; } = "";
        public int EligibleChunks { get; set; }
        public int TargetChunks { get; set; }
        public bool TargetReached { get; set; }
        public string SelectionPolicy { get; set; } = "";
        public bool ScanCapped { get; set; }
        public bool CoverageCapped { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int RetentionPolicyVersion { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LegacyRecoveryPending { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int UsefulChunks { get; set; }
        [JsonIgnore]
        public HashSet<string> RecoveryRequiredIds { get; set; } = [];
        public string Status { get; set; } = "";
        public string? ErrorType { get; set; }
        public string ContentHash { get; set; } = "";
        public List<Entry> Entries { get; set; } = [];
    }

    private sealed class SnapshotCacheItem(string key, string folder, string artifactPath, Manifest manifest,
        long estimatedBytes)
    {
        public string Key { get; } = key;
        public string Folder { get; } = folder;
        public string ArtifactPath { get; } = artifactPath;
        public Manifest Manifest { get; } = manifest;
        public long EstimatedBytes { get; } = estimatedBytes;
    }
}
