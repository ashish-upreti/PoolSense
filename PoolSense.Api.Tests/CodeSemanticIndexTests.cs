using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class CodeSemanticIndexTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), ".semantic-tests", Guid.NewGuid().ToString("N"));
    private readonly StubEmbeddings embeddings = new();
    private string Database => Path.Combine(directory, "index.sqlite3");
    private string Snapshot(string name) => Path.Combine(directory, name);

    public CodeSemanticIndexTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task BatchedEmbeddings_SaveCompletedBatchesAndResumeWithoutRepaying()
    {
        Seed("A", "B", "C", "D", "E", "F");
        var provider = new BatchEmbeddings { FailOnCall = 2 };
        var index = new CodeSemanticIndex(provider, Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions
        {
            Enabled = true, Dimensions = 2, EmbeddingBatchSize = 3
        }), NullLogger<CodeSemanticIndex>.Instance);
        var first = await index.BuildAsync(Database, directory, Snapshot("batch-first"), "commit-a");
        Assert.Equal("partial", first.Status);
        Assert.Equal(3, first.NewEmbeddings);
        Assert.Equal(3, first.IndexedChunks);
        Assert.Equal([3, 3], provider.BatchSizes);
        provider.FailOnCall = 0;
        var second = await index.BuildAsync(Database, directory, Snapshot("batch-second"), "commit-b", Snapshot("batch-first"));
        Assert.Equal("ready", second.Status);
        Assert.Equal(3, second.ReusedEmbeddings);
        Assert.Equal(3, second.NewEmbeddings);
        Assert.Equal([3, 3, 3], provider.BatchSizes);
    }

    [Fact]
    public async Task BatchedEmbeddings_RejectMismatchedResponseBeforeCheckpointing()
    {
        Seed("A", "B", "C");
        var index = new CodeSemanticIndex(new BatchEmbeddings { WrongCount = true },
            Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions { Enabled = true, Dimensions = 2, EmbeddingBatchSize = 3 }),
            NullLogger<CodeSemanticIndex>.Instance);
        var result = await index.BuildAsync(Database, directory, Snapshot("bad-batch"), "commit-a");
        Assert.Equal("failed", result.Status);
        Assert.Equal("InvalidDataException", result.ErrorType);
        Assert.Equal(0, result.IndexedChunks);
    }

    private sealed class BatchEmbeddings : IEmbeddingService, IEmbeddingBatchService
    {
        public string Profile => "batch-fixture";
        public List<int> BatchSizes { get; } = [];
        public int FailOnCall { get; set; }
        public bool WrongCount { get; init; }
        public Task<float[]> GenerateEmbedding(string text) => throw new InvalidOperationException("Batch-capable provider must not be called per chunk.");
        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken token)
        {
            BatchSizes.Add(texts.Count);
            if (BatchSizes.Count == FailOnCall) throw new IOException("Synthetic batch failure.");
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Take(WrongCount ? texts.Count - 1 : texts.Count)
                .Select(_ => new float[] { 1, 0 }).ToArray());
        }
    }

    [Fact]
    public async Task OfflineBuild_ReusesUnchangedContentAcrossCommits_AndInvalidatesChangedProfile()
    {
        Seed("FillPool", "StopMotor");
        var index = Create();
        var first = await index.BuildAsync(Database, directory, Snapshot("first"), "commit-a");
        Assert.Equal("ready", first.Status);
        Assert.Equal(2, embeddings.Calls.Count);
        var second = await index.BuildAsync(Database, directory, Snapshot("second"), "commit-b", Snapshot("first"));
        Assert.Equal(2, second.ReusedEmbeddings);
        Assert.Equal(0, second.NewEmbeddings);
        Assert.Equal(2, embeddings.Calls.Count);

        embeddings.Profile = "model-b:dimensions-2:settings-b";
        var third = await index.BuildAsync(Database, directory, Snapshot("third"), "commit-c", Snapshot("second"));
        Assert.Equal(0, third.ReusedEmbeddings);
        Assert.Equal(2, third.NewEmbeddings);
        Assert.Equal(4, embeddings.Calls.Count);
    }

    [Fact]
    public async Task BuildAndSearch_AcceptOnlyTheCredentialBoundIntermediateProfile()
    {
        Seed("FillPool");
        embeddings.Profile = "previous-profile";
        var oldIndex = Create();
        await oldIndex.BuildAsync(Database, directory, Snapshot("previous-profile-snapshot"), "commit-a");

        embeddings.Profile = "current-profile";
        embeddings.PreviousProfile = "previous-profile";
        var currentIndex = Create();
        var result = await currentIndex.BuildAsync(Database, directory, Snapshot("current-profile-snapshot"),
            "commit-b", Snapshot("previous-profile-snapshot"));

        Assert.Equal(1, result.ReusedEmbeddings);
        Assert.Equal(0, result.NewEmbeddings);
        Assert.Single(embeddings.Calls);
        Assert.Equal("ready", currentIndex.Search(Snapshot("previous-profile-snapshot"),
            new("query", "current-profile", [1, 0])).Status);
        Assert.Equal("ready", currentIndex.Search(Snapshot("current-profile-snapshot"),
            new("query", "current-profile", [1, 0])).Status);
    }

    [Fact]
    public async Task Refresh_ReusesContentAfterImmutableSnapshotSourceRootMoves()
    {
        Seed("FillPool");
        var index = Create();
        await index.BuildAsync(Database, directory, Snapshot("first"), "commit-a");
        var nextRoot = Snapshot("next-source");
        Directory.CreateDirectory(nextRoot);
        var nextDatabase = Path.Combine(nextRoot, "index.sqlite3");
        SeedInto(nextDatabase, nextRoot, "FillPool");
        var result = await index.BuildAsync(nextDatabase, nextRoot, Snapshot("second"), "commit-b", Snapshot("first"));
        Assert.Equal(1, result.ReusedEmbeddings);
        Assert.Equal(0, result.NewEmbeddings);
        Assert.Single(embeddings.Calls);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(24)]
    public async Task Build_RedactsBeforeTruncationEmbeddingAndCacheIdentity(int maxText)
    {
        Seed("A");
        var password = new string('p', 3000);
        const string apiKey = "private-api-key-value";
        const string docSecret = "private-doc-value";
        var source = $"Password = \"{password}\"; ApiKey = \"{apiKey}\";";
        var docstring = $"secret = \"{docSecret}\";";
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source=@p0,docstring=@p1 WHERE id='A'", source, docstring);

        var index = Create(maxText: maxText);
        var first = await index.BuildAsync(Database, directory, Snapshot("redacted"), "commit-a");
        Assert.Equal("ready", first.Status);
        var sent = Assert.Single(embeddings.Calls);
        var expected = SourceText.Redact($"A\n{docstring}\n{source}");
        Assert.Equal(expected[..Math.Min(expected.Length, maxText)], sent);
        Assert.DoesNotContain(new string('p', 8), sent);
        Assert.DoesNotContain(apiKey, sent);
        Assert.DoesNotContain(docSecret, sent);
        Assert.Contains("[REDACTED]", sent);
        var artifact = File.ReadAllText(Path.Combine(Snapshot("redacted"), CodeSemanticIndex.ArtifactName));
        Assert.DoesNotContain(password, artifact);
        Assert.DoesNotContain(apiKey, artifact);
        Assert.DoesNotContain(docSecret, artifact);

        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source=@p0,docstring=@p1 WHERE id='A'",
                "Password = \"different-password\"; ApiKey = \"different-key\";", "secret = \"different-doc\";");
        var second = await index.BuildAsync(Database, directory, Snapshot("redacted-next"), "commit-b", Snapshot("redacted"));
        Assert.Equal("ready", second.Status);
        Assert.Equal(1, second.ReusedEmbeddings);
        Assert.Equal(0, second.NewEmbeddings);
        Assert.Single(embeddings.Calls);
    }

    [Fact]
    public async Task Build_SkipsOversizedRawChunksRatherThanEmbeddingUnredactedPrefixes()
    {
        Seed("A");
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source=@p0 WHERE id='A'", $"Password = \"{new string('p', 65536)}\";");
        var result = await Create().BuildAsync(Database, directory, Snapshot("oversized-raw"), "commit");
        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.EligibleChunks);
        Assert.Equal(0, result.IndexedChunks);
        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task Build_EnforcesSelectionTextAndNewEmbeddingBudgets()
    {
        Seed("First", "Second", "Third", "Fourth");
        var index = Create(maxChunks: 2, budget: 1, maxText: 12);
        var result = await index.BuildAsync(Database, directory, Snapshot("bounded"), "commit");
        Assert.Equal("partial", result.Status);
        Assert.Equal(4, result.EligibleChunks);
        Assert.Equal(1, result.IndexedChunks);
        Assert.Single(embeddings.Calls);
        Assert.All(embeddings.Calls, text => Assert.True(text.Length <= 12));
        var next = await index.BuildAsync(Database, directory, Snapshot("next"), "commit", Snapshot("bounded"));
        Assert.Equal(1, next.NewEmbeddings);
        Assert.Equal(1, next.ReusedEmbeddings);
        Assert.Equal(2, next.IndexedChunks);
        Assert.Equal("partial", next.Status);
    }

    [Fact]
    public async Task Query_ReusesOnlyExactQueryProfileAndValidVector()
    {
        var index = Create();
        var reusable = new CodeQueryEmbedding("lower water", embeddings.Profile, [1, 0]);
        Assert.Same(reusable, (await index.CreateQueryAsync("lower water", reusable)).Embedding);
        Assert.Empty(embeddings.Calls);
        Assert.Equal("ready", (await index.CreateQueryAsync("LOWER water", reusable)).Status);
        Assert.Single(embeddings.Calls);
        await index.CreateQueryAsync("lower water", reusable with { Profile = "other" });
        await index.CreateQueryAsync("lower water", reusable with { Vector = [float.NaN, 0] });
        await index.CreateQueryAsync("lower water", reusable with { Vector = [1] });
        Assert.Equal(2, embeddings.Calls.Count);
    }

    [Fact]
    public async Task ParaphraseSearch_RanksOfflineVectorsWithoutAnyProviderCallsOrWrites()
    {
        Seed("DrainPool", "StartMotor");
        embeddings.Vector = text => text.Contains("DrainPool", StringComparison.Ordinal) || text == "lower water level"
            ? [1, 0] : [0, 1];
        var index = Create();
        await index.BuildAsync(Database, directory, Snapshot("ranked"), "commit");
        var query = await index.CreateQueryAsync("lower water level");
        var calls = embeddings.Calls.Count;
        var before = File.ReadAllBytes(Path.Combine(Snapshot("ranked"), CodeSemanticIndex.ArtifactName));
        var result = index.Search(Snapshot("ranked"), query.Embedding!, expectedCommit: "commit");
        Assert.Equal("ready", result.Status);
        Assert.Equal(new[] { "DrainPool", "StartMotor" }, result.Matches.Select(match => match.SymbolId));
        Assert.Equal(1d, result.Matches[0].Score);
        Assert.Equal(calls, embeddings.Calls.Count);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Snapshot("ranked"), CodeSemanticIndex.ArtifactName)));
        Assert.Equal("invalid", index.Search(Snapshot("ranked"), query.Embedding!, expectedCommit: "different").Status);
    }

    [Fact]
    public async Task Search_DecodesSnapshotOnceAndDetectsReplacedArtifact()
    {
        Seed("DrainPool", "StartMotor");
        var index = Create();
        var folder = Snapshot("cached");
        await index.BuildAsync(Database, directory, folder, "commit");
        var query = new CodeQueryEmbedding("lower water", embeddings.Profile, [1, 0]);
        var before = CodeSemanticIndex.SnapshotCacheMetrics;

        var timer = Stopwatch.StartNew();
        var first = index.Search(folder, query, expectedCommit: "commit");
        var coldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var second = index.Search(folder, query, expectedCommit: "commit");
        var warmMs = timer.Elapsed.TotalMilliseconds;
        var warmed = CodeSemanticIndex.SnapshotCacheMetrics;

        Assert.Equal(first.Matches, second.Matches);
        Assert.True(warmed.Loads >= before.Loads + 1);
        Assert.True(warmed.Hits >= before.Hits + 1);
        Assert.InRange(warmed.Bytes, 1, 64L * 1024 * 1024);
        Assert.True(CodeSemanticIndex.IsSnapshotCached(folder));
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            benchmark = "decoded-semantic-snapshot",
            coldSearchMilliseconds = Math.Round(coldMs, 3),
            warmSearchMilliseconds = Math.Round(warmMs, 3),
            decodeLoadsAvoided = 1,
            cachedBytes = warmed.Bytes,
            cacheEntryCap = 8,
            cacheByteCap = 64 * 1024 * 1024
        }));
        CodeSemanticIndex.EvictSnapshotCache(folder);
        Assert.False(CodeSemanticIndex.IsSnapshotCached(folder));

        var path = Path.Combine(folder, CodeSemanticIndex.ArtifactName);
        var invalid = File.ReadAllText(path).Replace("\"Commit\":\"commit\"", "\"Commit\":\"tampered\"", StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(path), invalid);
        File.WriteAllText(path, invalid);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));

        Assert.Equal("invalid", index.Search(folder, query, expectedCommit: "commit").Status);
    }

    [Fact]
    public async Task WarmSnapshot_LoadsValidatedVectorsWithoutCreatingAQueryOrCallingTheProvider()
    {
        Seed("DrainPool");
        var index = Create();
        var folder = Snapshot("explicit-warm");
        await index.BuildAsync(Database, directory, folder, "commit");
        var providerCalls = embeddings.Calls.Count;
        CodeSemanticIndex.EvictSnapshotCache(folder);
        var before = CodeSemanticIndex.SnapshotCacheMetrics;

        var warm = index.WarmSnapshot(folder, "commit");
        var after = CodeSemanticIndex.SnapshotCacheMetrics;

        Assert.NotNull(warm);
        Assert.Equal("ready", warm.Status);
        Assert.Equal(1, warm.Vectors);
        Assert.False(warm.CacheHit);
        Assert.True(CodeSemanticIndex.IsSnapshotCached(folder));
        Assert.True(after.Loads > before.Loads);
        Assert.Equal(providerCalls, embeddings.Calls.Count);

        var hit = index.WarmSnapshot(folder, "commit");
        Assert.True(hit!.CacheHit);
        Assert.Equal(providerCalls, embeddings.Calls.Count);
    }

    [Fact]
    public async Task ConcurrentColdWarmupsShareOneSnapshotReadAndCanceledWaiterDoesNotPoisonIt()
    {
        Seed("DrainPool");
        var index = Create();
        var folder = Snapshot("concurrent-warm");
        await index.BuildAsync(Database, directory, folder, "commit");
        CodeSemanticIndex.EvictSnapshotCache(folder);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.WarmSnapshot(folder, "commit", canceled.Token));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            index.WarmSnapshot(folder, "commit"))));

        Assert.All(results, result => Assert.Equal("ready", result!.Status));
        Assert.Single(results, result => result is { CacheHit: false });
    }

    [Fact]
    public async Task DecodedSnapshotCache_EvictsLeastRecentlyUsedAtEntryCap()
    {
        Seed("FillPool");
        var index = Create();
        var query = new CodeQueryEmbedding("query", embeddings.Profile, [1, 0]);
        for (var i = 0; i < 10; i++)
        {
            var folder = Snapshot($"cache-{i}");
            await index.BuildAsync(Database, directory, folder, $"commit-{i}");
            Assert.Equal("ready", index.Search(folder, query, expectedCommit: $"commit-{i}").Status);
        }

        var metrics = CodeSemanticIndex.SnapshotCacheMetrics;
        Assert.InRange(metrics.Entries, 1, 8);
        Assert.InRange(metrics.Bytes, 1, 64L * 1024 * 1024);
    }

    [Fact]
    public void MissingArtifact_SearchDoesNotCreateDirectoryOrContactProvider()
    {
        var index = Create();
        Assert.Null(index.WarmSnapshot(Snapshot("missing"), "commit"));
        var result = index.Search(Snapshot("missing"), new("query", embeddings.Profile, [1, 0]));
        Assert.Equal("unavailable", result.Status);
        Assert.False(Directory.Exists(Snapshot("missing")));
        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task ProviderFailure_CheckpointsAndRetainsEarlierValidVectors()
    {
        Seed("A", "B", "C");
        embeddings.FailOnCall = 2;
        var index = Create();
        var result = await index.BuildAsync(Database, directory, Snapshot("partial"), "commit");
        Assert.Equal("partial", result.Status);
        Assert.Equal(nameof(HttpRequestException), result.ErrorType);
        Assert.Equal(1, result.IndexedChunks);
        Assert.Equal("partial", index.Search(Snapshot("partial"), new("query", embeddings.Profile, [1, 0])).Status);
        Assert.Single(index.Search(Snapshot("partial"), new("query", embeddings.Profile, [1, 0])).Matches);
        embeddings.FailOnCall = 3;
        var query = await index.CreateQueryAsync("query");
        Assert.Equal("failed", query.Status);
        Assert.Null(query.Embedding);
        Assert.Equal(nameof(HttpRequestException), query.ErrorType);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Build_ProfileChangesDuringAwait_DiscardMismatchedVectorAndKeepOnlyOriginalProfile(int changeOnCall)
    {
        Seed("A", "B");
        var originalProfile = embeddings.Profile;
        embeddings.AsyncVector = async (_, _) =>
        {
            await Task.Yield();
            if (embeddings.Calls.Count == changeOnCall)
            {
                embeddings.Profile = "model-b:dimensions-2:settings-b";
                return [0, 1];
            }
            return [1, 0];
        };
        var index = Create();
        var result = await index.BuildAsync(Database, directory, Snapshot("profile-changed"), "commit");
        Assert.Equal("partial", result.Status);
        Assert.Equal("ProfileChanged", result.ErrorType);
        Assert.Equal(changeOnCall - 1, result.IndexedChunks);
        Assert.Equal(changeOnCall - 1, result.NewEmbeddings);
        Assert.Equal(changeOnCall, embeddings.Calls.Count);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Snapshot("profile-changed"), CodeSemanticIndex.ArtifactName)))!;
        Assert.Equal(originalProfile, manifest["Profile"]!.GetValue<string>());
        var entries = manifest["Entries"]!.AsArray();
        Assert.Equal(changeOnCall - 1, entries.Count);
        Assert.All(entries, entry => Assert.Equal(0f,
            BitConverter.ToSingle(Convert.FromBase64String(entry!["Vector"]!.GetValue<string>()), 4)));
        Assert.Equal("incompatible", index.Search(Snapshot("profile-changed"), new("query", originalProfile, [1, 0])).Status);
    }

    [Fact]
    public async Task Query_ProfileChangesDuringAwait_ReturnsUnavailableWithoutMislabeledHint()
    {
        embeddings.AsyncVector = async (_, _) =>
        {
            await Task.Yield();
            embeddings.Profile = "model-b:dimensions-2:settings-b";
            return [0, 1];
        };
        var result = await Create().CreateQueryAsync("query");
        Assert.Equal("unavailable", result.Status);
        Assert.Equal("ProfileChanged", result.ErrorType);
        Assert.Null(result.Embedding);
        Assert.Single(embeddings.Calls);
    }

    [Fact]
    public async Task BuildFailure_StillPreservesPreviouslyCachedLaterEntries()
    {
        Seed("A", "B");
        var index = Create();
        await index.BuildAsync(Database, directory, Snapshot("previous"), "commit");
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source='changed' WHERE id='A'");
        embeddings.FailOnCall = 3;
        var result = await index.BuildAsync(Database, directory, Snapshot("partial"), "commit-2", Snapshot("previous"));
        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.ReusedEmbeddings);
        Assert.Equal("B", Assert.Single(index.Search(Snapshot("partial"), new("query", embeddings.Profile, [1, 0])).Matches).SymbolId);
    }

    [Fact]
    public async Task Cancellation_PropagatesToProvider_AndPreservesEarlierCheckpoint()
    {
        Seed("A", "B");
        using var cancellation = new CancellationTokenSource();
        embeddings.OnCall = (count, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (count == 2) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var index = Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.BuildAsync(Database, directory, Snapshot("cancelled"), "commit", token: cancellation.Token));
        var result = index.Search(Snapshot("cancelled"), new("query", embeddings.Profile, [1, 0]));
        Assert.Single(result.Matches);
        Assert.Equal("partial", result.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.CreateQueryAsync("q", token: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.Search(Snapshot("cancelled"),
            new("q", embeddings.Profile, [1, 0]), cancellation.Token));
    }

    [Fact]
    public async Task QueryCancellation_IsNotReportedAsProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CreateQueryAsync("q", token: cancellation.Token));
        Assert.Empty(embeddings.Calls);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("metadata")]
    [InlineData("vector")]
    [InlineData("oversized")]
    public async Task CorruptArtifacts_AreRejectedAsAWhole(string corruption)
    {
        Seed("A", "B");
        var index = Create();
        var folder = Snapshot("corrupt");
        await index.BuildAsync(Database, directory, folder, "commit");
        var path = Path.Combine(folder, CodeSemanticIndex.ArtifactName);
        if (corruption == "json") File.WriteAllText(path, "{");
        else if (corruption == "oversized") File.WriteAllText(path, new string(' ', 8 * 1024 * 1024 + 1));
        else
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path))!;
            if (corruption == "metadata") manifest["Commit"] = "";
            else manifest["Entries"]![1]!["Vector"] = new JsonArray(1);
            File.WriteAllText(path, manifest.ToJsonString());
        }
        var result = index.Search(folder, new("query", embeddings.Profile, [1, 0]));
        Assert.Equal("invalid", result.Status);
        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    public async Task InvalidProviderVectors_AreNeverIndexedOrUsedForQueries(float invalid)
    {
        Seed("A");
        embeddings.Vector = _ => [invalid, 0];
        var index = Create();
        var build = await index.BuildAsync(Database, directory, Snapshot("invalid"), "commit");
        Assert.Equal("failed", build.Status);
        Assert.Equal(0, build.IndexedChunks);
        var query = await index.CreateQueryAsync("query");
        Assert.Equal("failed", query.Status);
        Assert.Null(query.Embedding);
        Assert.Equal("incompatible", index.Search(Snapshot("invalid"), new("q", embeddings.Profile, [invalid, 0])).Status);
    }

    [Fact]
    public async Task DisabledOrUnknownProfile_NeverCallsProviderOrWritesArtifacts()
    {
        var disabled = Create(enabled: false);
        Assert.Equal("disabled", (await disabled.BuildAsync(Database, directory, Snapshot("disabled"), "commit")).Status);
        Assert.Equal("disabled", (await disabled.CreateQueryAsync("query")).Status);
        embeddings.Profile = "";
        var unknown = Create();
        Assert.Equal("unavailable", (await unknown.BuildAsync(Database, directory, Snapshot("unknown"), "commit")).Status);
        Assert.Equal("unavailable", (await unknown.CreateQueryAsync("query")).Status);
        Assert.Empty(embeddings.Calls);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task PublishedArtifact_IsNotOverwrittenByBuild()
    {
        Seed("A");
        var index = Create();
        await index.BuildAsync(Database, directory, Snapshot("immutable"), "commit");
        var path = Path.Combine(Snapshot("immutable"), CodeSemanticIndex.ArtifactName);
        var original = File.ReadAllBytes(path);
        Assert.Equal("failed", (await index.BuildAsync(Database, directory, Snapshot("immutable"), "other")).Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(embeddings.Calls);
    }

    [Fact]
    public void Options_EnforceHardCostAndReadBounds()
    {
        var index = new CodeSemanticIndex(embeddings, Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions
        {
            MaxChunks = int.MaxValue, MaxNewEmbeddingsPerBuild = int.MaxValue,
            MaxTextCharacters = int.MaxValue, Dimensions = int.MaxValue, MaxManifestBytes = int.MaxValue
        }), NullLogger<CodeSemanticIndex>.Instance);
        Assert.Equal(64, index.Options.MaxChunks);
        Assert.Equal(64, index.Options.MaxNewEmbeddingsPerBuild);
        Assert.Equal(8000, index.Options.MaxTextCharacters);
        Assert.Equal(1536, index.Options.Dimensions);
        Assert.Equal(8 * 1024 * 1024, index.Options.MaxManifestBytes);
    }

    [Fact]
    public void ProductionQueryCacheProfile_TracksCredentialsWithoutInvalidatingIndexVectors()
    {
        var settings = new NyraSettings
        {
            EmbeddingModel = "text-embedding-3-small",
            GatewayEndpoint = "https://gateway.test",
            AuthMode = NyraAuthModes.ApiKeyAndClientCredentials
        };
        var monitor = new Monitor(settings);
        var service = new EmbeddingService(null!, null!, monitor, NullLogger<EmbeddingService>.Instance);
        var initial = service.Profile;
        var initialQueryCacheProfile = service.QueryCacheProfile;
        settings.ClientSecret = "do-not-persist";
        settings.ApiKey = "do-not-persist-either";
        Assert.Equal(initial, service.Profile);
        Assert.NotEqual(initialQueryCacheProfile, service.QueryCacheProfile);
        Assert.DoesNotContain("do-not-persist", service.QueryCacheProfile);
        Assert.DoesNotContain("gateway", initial);
        settings.EmbeddingModel = "different-model";
        Assert.NotEqual(initial, service.Profile);
        settings.EmbeddingModel = "text-embedding-3-small";
        settings.GatewayEndpoint += "/changed";
        Assert.NotEqual(initial, service.Profile);
    }

    private CodeSemanticIndex Create(bool enabled = true, int maxChunks = 64, int budget = 64, int maxText = 2000) =>
        new(embeddings, Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions
        {
            Enabled = enabled, Dimensions = 2, MaxChunks = maxChunks,
            MaxNewEmbeddingsPerBuild = budget, MaxTextCharacters = maxText
        }), NullLogger<CodeSemanticIndex>.Instance);

    private void Seed(params string[] symbols)
    {
        Directory.CreateDirectory(directory);
        SeedInto(Database, directory, symbols);
    }

    private static void SeedInto(string database, string sourceRoot, params string[] symbols)
    {
        using var db = GraphStore.Open(database);
        var root = GraphStore.RootKey(sourceRoot);
        GraphStore.Execute(db, "INSERT INTO repositories VALUES(@p0,'now',1,@p1,0,@p2,@p3)",
            root, symbols.Length, GraphStore.Version, GraphStore.Parser);
        foreach (var name in symbols)
            GraphStore.Execute(db, """
                INSERT INTO symbols VALUES(@p0,@p1,'source.cs','method',@p0,@p0,1,1,NULL,@p2,'')
                """, name, root, $"void {name}() {{ return; }}");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class StubEmbeddings : IEmbeddingService
    {
        public string Profile { get; set; } = "model-a:dimensions-2:settings-a";
        public string PreviousProfile { get; set; } = "";
        public List<string> Calls { get; } = [];
        public Func<string, float[]> Vector { get; set; } = _ => [1, 0];
        public Func<string, CancellationToken, Task<float[]>>? AsyncVector { get; set; }
        public Action<int, CancellationToken>? OnCall { get; set; }
        public int FailOnCall { get; set; }
        public Task<float[]> GenerateEmbedding(string text) => GenerateEmbedding(text, CancellationToken.None);
        public Task<float[]> GenerateEmbedding(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(text);
            OnCall?.Invoke(Calls.Count, token);
            if (Calls.Count == FailOnCall) throw new HttpRequestException("Provider unavailable.");
            return AsyncVector?.Invoke(text, token) ?? Task.FromResult(Vector(text));
        }
    }

    private sealed class Monitor(NyraSettings settings) : IOptionsMonitor<NyraSettings>
    {
        public NyraSettings CurrentValue => settings;
        public NyraSettings Get(string? name) => settings;
        public IDisposable? OnChange(Action<NyraSettings, string?> listener) => null;
    }
}
