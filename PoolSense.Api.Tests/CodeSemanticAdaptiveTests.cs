using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class CodeSemanticAdaptiveTests : IDisposable
{
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), ".semantic-tests", Guid.NewGuid().ToString("N"));
    private readonly Embeddings provider = new();
    private string Database => Path.Combine(root, "index.sqlite3");
    private string Folder(string name) => Path.Combine(root, name);
    private string Artifact(string name) => Path.Combine(Folder(name), CodeSemanticIndex.ArtifactName);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(512, 128)]
    [InlineData(513, 512)]
    [InlineData(4096, 512)]
    [InlineData(4097, 768)]
    public async Task UsefulUniqueSources_DetermineColdTierAtExactBoundaries(int count, int target)
    {
        Seed(count);
        var index = Create(budget: 0);
        var result = await index.BuildAsync(Database, root, Folder("tier"), "commit");
        Assert.Equal(count, result.EligibleChunks);
        Assert.Equal(target, result.TargetChunks);
        Assert.Equal(count == 0, result.TargetReached);
        Assert.Equal(count > target, result.CoverageCapped);
        Assert.False(result.ScanCapped);
        Assert.Empty(provider.Calls);
        var coverage = index.ReadCoverage(Folder("tier"), "commit");
        Assert.NotNull(coverage);
        Assert.Equal(result.TargetChunks, coverage.TargetChunks);
        Assert.Equal(index.Policy, coverage.SelectionPolicy);
        Assert.Null(index.ReadCoverage(Folder("tier"), "wrong"));
    }

    [Fact]
    public void AdaptiveHardCeiling_AndLegacyFixedDefaults_AreSeparateFromBatchBudget()
    {
        var adaptive = Create(ceiling: int.MaxValue, budget: int.MaxValue);
        Assert.Equal(768, adaptive.Options.MaxChunks);
        Assert.Equal(64, adaptive.Options.MaxNewEmbeddingsPerBuild);
        var defaults = new CodeSemanticOptions();
        Assert.False(defaults.AdaptiveCoverageEnabled);
        Assert.Equal(64, defaults.MaxChunks);
        Assert.Equal(128, defaults.SmallRepositoryTarget);
        Assert.Equal(512, defaults.MediumRepositoryTarget);
        Assert.Equal(768, defaults.LargeRepositoryTarget);
        Assert.Equal(300, defaults.BatchCooldownSeconds);
        Assert.Equal(8192, defaults.CandidateLimit);
        Assert.Equal(16 * 1024 * 1024, defaults.CandidateBytes);
        var fixedIndex = Create(ceiling: int.MaxValue, adaptive: false);
        Assert.Equal(64, fixedIndex.Options.MaxChunks);
        Assert.NotEqual(adaptive.Policy, fixedIndex.Policy);
        Assert.Equal(adaptive.Policy, CodeSemanticIndex.PolicySignature(adaptive.Options));
    }

    [Fact]
    public async Task SecondAndThirdGenerations_GrowOnlyNewContent_UntilTargetReached()
    {
        Seed(400);
        var index = Create();
        var first = await index.BuildAsync(Database, root, Folder("one"), "c1");
        var second = await index.BuildAsync(Database, root, Folder("two"), "c2", Folder("one"));
        var third = await index.BuildAsync(Database, root, Folder("three"), "c3", Folder("two"));
        var fourth = await index.BuildAsync(Database, root, Folder("four"), "c4", Folder("three"));
        Assert.Equal(new[] { 64, 128, 128, 128 }, new[] { first.IndexedChunks, second.IndexedChunks, third.IndexedChunks, fourth.IndexedChunks });
        Assert.Equal(new[] { 64, 64, 0, 0 }, new[] { first.NewEmbeddings, second.NewEmbeddings, third.NewEmbeddings, fourth.NewEmbeddings });
        Assert.Equal(128, provider.Calls.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(128, provider.Calls.Count);
        Assert.True(second.TargetReached);
        Assert.Equal("partial", second.Status);
        Assert.Equal(400, second.EligibleChunks);
        Assert.Equal(128, third.ReusedEmbeddings);
        Assert.All(new[] { first, second, third, fourth }, result => Assert.Null(result.ErrorType));
    }

    [Fact]
    public async Task MediumRepository_GrowsThroughThreeBatches_AndHardCeilingStopsCalls()
    {
        Seed(600);
        var index = Create(ceiling: 150);
        var first = await index.BuildAsync(Database, root, Folder("one"), "c");
        var second = await index.BuildAsync(Database, root, Folder("two"), "c", Folder("one"));
        var third = await index.BuildAsync(Database, root, Folder("three"), "c", Folder("two"));
        var fourth = await index.BuildAsync(Database, root, Folder("four"), "c", Folder("three"));
        Assert.All(new[] { first, second, third, fourth }, result => Assert.True(result.ErrorType is null, result.ToString()));
        Assert.Equal(150, third.IndexedChunks);
        Assert.Equal(22, third.NewEmbeddings);
        Assert.Equal(128, third.ReusedEmbeddings);
        Assert.False(second.TargetReached);
        Assert.True(third.TargetReached);
        Assert.Equal(0, fourth.NewEmbeddings);
        Assert.Equal(150, provider.Calls.Count);
    }

    [Fact]
    public async Task BalancedDirectoryLanguageSelection_ExcludesNoise_AndDeduplicatesRealSource()
    {
        Seed(0);
        using (var db = GraphStore.Open(Database))
        {
            var directories = new[] { "api", "domain", "data", "workflows", "z-last" };
            foreach (var directory in directories)
                for (var i = 0; i < 30; i++)
                    Insert(db, root, $"{directory}-{i}", $"{directory}\\handler{i}.cs", $"process_{directory}_{i}();");
            for (var i = 0; i < 30; i++)
                Insert(db, root, $"python-{i}", $"z-last\\job{i}.py", $"process_python_{i}()");
            var noise = new[] { "vendor\\a.cs", "node_modules\\a.ts", "assets\\a.cs", "obj\\a.cs",
                "generated\\a.cs", "ui\\style.css", "build\\a.py", "api\\a.g.cs", "api\\a.designer.cs", "api\\a.min.js" };
            foreach (var path in noise) Insert(db, root, path, path, "DoNotEmbed_" + path + "();");
            Insert(db, root, "generated-marker", "api\\marker.cs", "// <auto-generated>\nDoNotEmbed();");
            Insert(db, root, "duplicate", "api\\duplicate.cs", "process_api_0();");
            Insert(db, root, "blank", "api\\blank.cs", "{} \n ;");
        }
        var index = Create(ceiling: 24);
        var result = await index.BuildAsync(Database, root, Folder("balanced"), "c");
        Assert.Equal(180, result.EligibleChunks);
        Assert.Equal(24, result.IndexedChunks);
        Assert.True(result.TargetReached);
        foreach (var name in new[] { "api", "domain", "data", "workflows", "z-last", "python" })
            Assert.Contains(provider.Calls, text => text.Contains($"process_{name}_", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Calls, text => text.Contains("DoNotEmbed", StringComparison.Ordinal));
        Assert.Equal(24, provider.Calls.Distinct().Count());
    }

    [Fact]
    public async Task BoundedScanAndBytes_ReportLowerBoundNotFullCoverage()
    {
        Seed(40);
        var scan = Create(budget: 0, candidateLimit: 20);
        var limited = await scan.BuildAsync(Database, root, Folder("scan"), "c");
        Assert.Equal(20, limited.EligibleChunks);
        Assert.True(limited.ScanCapped);
        Assert.True(limited.CoverageCapped);
        var bytes = Create(candidateBytes: 1024);
        var bounded = await bytes.BuildAsync(Database, root, Folder("bytes"), "c");
        Assert.InRange(bounded.EligibleChunks, 1, 39);
        Assert.True(bounded.ScanCapped);
        Assert.Equal("partial", bounded.Status);
        Assert.True(bounded.TargetReached);
        Assert.True(bytes.ReadCoverage(Folder("bytes"))!.ScanCapped);
    }

    [Fact]
    public async Task LegacyVersionTwo_ReadsExactOldHashes_ReusesAll64DespiteNewBalance()
    {
        Seed(600);
        var fixedIndex = Create(adaptive: false);
        var legacy = await fixedIndex.BuildAsync(Database, root, Folder("legacy"), "old");
        Assert.True(legacy.ErrorType is null, legacy.ToString());
        ConvertToLegacy("legacy");
        Assert.Equal(64, fixedIndex.ReadCoverage(Folder("legacy"), "old")!.IndexedChunks);
        var legacyIds = fixedIndex.Search(Folder("legacy"), new("q", provider.Profile, [1, 0])).Matches.Select(match => match.SymbolId).ToHashSet();
        var adaptive = Create();
        var next = await adaptive.BuildAsync(Database, root, Folder("next"), "new", Folder("legacy"));
        Assert.True(next.ReusedEmbeddings == 64, $"Build: {next}; old IDs missing: "
            + string.Join(",", legacyIds.Except(adaptive.Search(Folder("next"), new("q", provider.Profile, [1, 0])).Matches.Select(match => match.SymbolId))));
        Assert.True(next.NewEmbeddings == 64, next.ToString());
        var nextIds = adaptive.Search(Folder("next"), new("q", provider.Profile, [1, 0])).Matches.Select(match => match.SymbolId).ToHashSet();
        Assert.True(legacyIds.IsSubsetOf(nextIds));
        Assert.Equal(128, provider.Calls.Count);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(Artifact("next")))!["Version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidLegacyCoverage_SurvivesNewNoiseFiltersAndBoundedSamplingAcrossRootChanges(bool versionTwo)
    {
        Seed(0);
        using (var db = GraphStore.Open(Database))
        {
            for (var i = 0; i < 64; i++)
                Insert(db, root, $"old-{i:D4}", $"assets\\a{i:D4}.css", $"LegacyStyle_{i} {{ Value: {i}; }}");
            for (var i = 0; i < 500; i++)
                Insert(db, root, $"new-{i:D4}", $"z-domain\\business{i}.cs", $"ExecuteBusiness_{i}();");
        }
        var old = Create(adaptive: false);
        await old.BuildAsync(Database, root, Folder("old"), "old");
        if (versionTwo) ConvertToLegacy("old");
        var oldHashes = JsonNode.Parse(File.ReadAllText(Artifact("old")))!["Entries"]!.AsArray()
            .Select(entry => entry!["ContentHash"]!.GetValue<string>()).ToHashSet();
        var nextRoot = MoveRootAndIds();
        var index = Create(candidateLimit: 128);
        var result = await index.BuildAsync(Database, nextRoot, Folder("new"), "new", Folder("old"));
        Assert.True(result.ErrorType is null, result.ToString());
        Assert.Equal(64, result.ReusedEmbeddings);
        Assert.Equal(64, result.NewEmbeddings);
        Assert.Equal(128, result.IndexedChunks);
        Assert.True(result.ScanCapped);
        var nextHashes = JsonNode.Parse(File.ReadAllText(Artifact("new")))!["Entries"]!.AsArray()
            .Select(entry => entry!["ContentHash"]!.GetValue<string>()).ToHashSet();
        Assert.True(oldHashes.IsSubsetOf(nextHashes));
        Assert.Equal(128, provider.Calls.Count);
        Assert.All(JsonNode.Parse(File.ReadAllText(Artifact("new")))!["Entries"]!.AsArray(),
            entry => Assert.NotNull(entry!["FilePath"]));
    }

    [Fact]
    public async Task ExistingV3WithoutLocators_UsesImmutablePreviousDatabaseToPreserveSampledCoverage()
    {
        Seed(600);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("old"), "old");
        var manifest = JsonNode.Parse(File.ReadAllText(Artifact("old")))!;
        foreach (var entry in manifest["Entries"]!.AsArray())
        {
            entry!.AsObject().Remove("FilePath");
            entry.AsObject().Remove("StartLine");
        }
        RehashCompact(manifest);
        File.WriteAllText(Artifact("old"), manifest.ToJsonString());
        File.Copy(Database, Path.Combine(Folder("old"), "index.sqlite3"));
        var nextRoot = MoveRootAndIds();
        var bounded = Create(candidateLimit: 128);
        var result = await bounded.BuildAsync(Database, nextRoot, Folder("new"), "new", Folder("old"));
        Assert.True(result.ErrorType is null, result.ToString());
        Assert.Equal(64, result.ReusedEmbeddings);
        Assert.Equal(64, result.NewEmbeddings);
        Assert.Equal(128, result.IndexedChunks);
    }

    [Fact]
    public async Task SmallerWarmTier_DoesNotDiscardValidCoverageAlreadyAboveItsColdTarget()
    {
        Seed(600);
        var index = Create(ceiling: 150);
        await index.BuildAsync(Database, root, Folder("one"), "c");
        await index.BuildAsync(Database, root, Folder("two"), "c", Folder("one"));
        await index.BuildAsync(Database, root, Folder("three"), "c", Folder("two"));
        var kept = JsonNode.Parse(File.ReadAllText(Artifact("three")))!["Entries"]!.AsArray()
            .Select(entry => entry!["SymbolId"]!.GetValue<string>()).ToHashSet();
        using (var db = GraphStore.Open(Database))
        {
            var ids = GraphStore.Query(db, "SELECT id FROM symbols", reader => reader.GetString(0));
            foreach (var id in ids.Where(id => !kept.Contains(id)))
                GraphStore.Execute(db, "UPDATE symbols SET source='' WHERE id=@p0", id);
        }
        var warm = Create();
        var result = await warm.BuildAsync(Database, root, Folder("warm"), "new", Folder("three"));
        Assert.Equal(150, result.ReusedEmbeddings);
        Assert.Equal(150, result.IndexedChunks);
        Assert.Equal(150, result.TargetChunks);
        Assert.Equal(0, result.NewEmbeddings);
        Assert.True(result.TargetReached);
        Assert.Equal(150, provider.Calls.Count);
    }

    [Fact]
    public async Task InsufficientRetentionBudget_FailsBeforeProviderCallsRatherThanPublishingLostCoverage()
    {
        Seed(600);
        var old = Create(adaptive: false);
        await old.BuildAsync(Database, root, Folder("old"), "c");
        var before = File.ReadAllBytes(Artifact("old"));
        var bounded = Create(candidateLimit: 16);
        var result = await bounded.BuildAsync(Database, root, Folder("limited"), "c", Folder("old"));
        Assert.Equal("failed", result.Status);
        Assert.Equal("RetentionBudgetException", result.ErrorType);
        Assert.Equal(0, result.NewEmbeddings);
        Assert.Equal(64, provider.Calls.Count);
        Assert.Equal(before, File.ReadAllBytes(Artifact("old")));
        Assert.False(File.Exists(Artifact("limited")));
    }

    [Fact]
    public async Task EarlyV3Recovery_RestoresOriginalNoiseSpanEligibilityAcrossNormalBatches_ThenStops()
    {
        SeedRecoveryRepository();
        var index = Create();
        await index.BuildAsync(Database, root, Folder("early"), "same-head");
        MakeEarlyV3("early");
        Assert.DoesNotContain(provider.Calls, text => text.Contains("OriginalStyle", StringComparison.Ordinal));
        var repair = Create(budget: 16);
        string previous = Folder("early");
        var sourceRoot = root;
        for (var batch = 0; batch < 4; batch++)
        {
            var folder = Folder($"repair-{batch}");
            var result = await repair.BuildAsync(Database, sourceRoot, folder, "same-head", previous,
                checkpointSnapshotFolder: Folder("recovery-journal"));
            Assert.True(result.ErrorType is null, result.ToString());
            Assert.Equal(16, result.NewEmbeddings);
            Assert.Equal(80 + batch * 16, result.IndexedChunks);
            Assert.Equal(batch < 3, result.LegacyRecoveryPending);
            Assert.Equal(batch == 3, result.TargetReached);
            Assert.Equal(128, result.TargetChunks);
            Assert.Equal(500, result.UsefulChunks);
            Assert.Equal(result.LegacyRecoveryPending, repair.ReadCoverage(folder)!.LegacyRecoveryPending);
            previous = folder;
            if (batch == 0) sourceRoot = MoveRootAndIds();
        }
        Assert.Equal(128, provider.Calls.Count);
        Assert.All(provider.Calls.Skip(64), text => Assert.Contains("OriginalStyle", text));
        var restored = JsonNode.Parse(File.ReadAllText(Path.Combine(previous, CodeSemanticIndex.ArtifactName)))!;
        Assert.Equal(1, restored["RetentionPolicyVersion"]!.GetValue<int>());
        Assert.Equal(64, restored["Entries"]!.AsArray().Count(entry =>
            entry!["FilePath"]!.GetValue<string>().StartsWith("a-legacy", StringComparison.Ordinal)));
        using (var db = GraphStore.Open(Database))
            Insert(db, sourceRoot, "future-css", "a-legacy\\000-future.css", "FutureStyleShouldNotBeAdded {}");
        var completed = await repair.BuildAsync(Database, sourceRoot, Folder("completed"), "same-head", previous);
        Assert.Equal(128, completed.ReusedEmbeddings);
        Assert.Equal(0, completed.NewEmbeddings);
        Assert.False(completed.LegacyRecoveryPending);
        Assert.Equal(128, provider.Calls.Count);

        provider.Profile = "new-profile";
        var changedProfile = await index.BuildAsync(Database, sourceRoot, Folder("profile-changed"), "same-head", previous);
        Assert.Equal(0, changedProfile.ReusedEmbeddings);
        Assert.Equal(64, changedProfile.NewEmbeddings);
        Assert.False(changedProfile.LegacyRecoveryPending);
        Assert.DoesNotContain(provider.Calls.Skip(128), text => text.Contains("OriginalStyle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EarlyV3Recovery_DoesNotGrandfatherUnknownOriginalSpansFromADifferentHead()
    {
        SeedRecoveryRepository();
        var index = Create();
        await index.BuildAsync(Database, root, Folder("early"), "old-head");
        MakeEarlyV3("early");
        var repair = Create(budget: 16);
        var result = await repair.BuildAsync(Database, root, Folder("new-head"), "different-head", Folder("early"));
        Assert.Equal(64, result.ReusedEmbeddings);
        Assert.Equal(16, result.NewEmbeddings);
        Assert.False(result.LegacyRecoveryPending);
        Assert.DoesNotContain(provider.Calls, text => text.Contains("OriginalStyle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EarlyV3Recovery_RepairsAnEmptyBadPolicyArtifact_ButColdPolicyDoesNotRestoreNoise()
    {
        Seed(0);
        using (var db = GraphStore.Open(Database))
            for (var i = 0; i < 64; i++)
                Insert(db, root, $"original-{i}", $"assets\\file{i:D4}.css", $"OriginalStyle_{i} {{ Value: {i}; }}");
        var index = Create();
        var cold = await index.BuildAsync(Database, root, Folder("early"), "same-head");
        Assert.Equal(0, cold.IndexedChunks);
        Assert.Empty(provider.Calls);
        MakeEarlyV3("early");
        var result = await index.BuildAsync(Database, root, Folder("repaired"), "same-head", Folder("early"));
        Assert.Equal(64, result.IndexedChunks);
        Assert.Equal(64, result.NewEmbeddings);
        Assert.Equal(0, result.ReusedEmbeddings);
        Assert.Equal(0, result.UsefulChunks);
        Assert.False(result.LegacyRecoveryPending);
        Assert.All(provider.Calls, text => Assert.Contains("OriginalStyle", text));
        var futureCold = await index.BuildAsync(Database, root, Folder("future-cold"), "same-head");
        Assert.Equal(0, futureCold.NewEmbeddings);
        Assert.Equal(64, provider.Calls.Count);
    }

    private void SeedRecoveryRepository()
    {
        Seed(0);
        using var db = GraphStore.Open(Database);
        for (var i = 0; i < 64; i++)
            Insert(db, root, $"original-{i:D4}", $"a-legacy\\file{i:D4}.css", $"OriginalStyle_{i} {{ Value: {i}; }}");
        for (var i = 0; i < 500; i++)
            Insert(db, root, $"business-{i:D4}", $"z-domain\\file{i:D4}.cs", $"ExecuteBusiness_{i}();");
    }

    private void MakeEarlyV3(string name)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Artifact(name)))!;
        foreach (var property in new[] { "RetentionPolicyVersion", "LegacyRecoveryPending", "UsefulChunks" })
            manifest.AsObject().Remove(property);
        foreach (var entry in manifest["Entries"]!.AsArray())
        {
            entry!.AsObject().Remove("FilePath");
            entry.AsObject().Remove("StartLine");
        }
        RehashCompact(manifest);
        File.WriteAllText(Artifact(name), manifest.ToJsonString());
    }

    [Fact]
    public async Task ChangedSourceAndProfile_ReuseOnlyCurrentContent()
    {
        Seed(40);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("first"), "old");
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source='ChangedBusinessLogic();' WHERE id='symbol-00000'");
        var changed = await index.BuildAsync(Database, root, Folder("changed"), "new", Folder("first"));
        Assert.Equal(39, changed.ReusedEmbeddings);
        Assert.Equal(1, changed.NewEmbeddings);
        provider.Profile = "different-profile";
        var profile = await index.BuildAsync(Database, root, Folder("profile"), "new", Folder("changed"));
        Assert.Equal(0, profile.ReusedEmbeddings);
        Assert.Equal(40, profile.NewEmbeddings);
    }

    [Fact]
    public async Task IdenticalTruncatedEmbeddingText_UsesOnePaidVectorWithinTheSameBatch()
    {
        Seed(0);
        using (var db = GraphStore.Open(Database))
        {
            var prefix = "void Shared() { var text = \"" + new string('x', 2500) + "\";";
            Insert(db, root, "a", "domain\\a.cs", prefix + "ExecuteFirst(); }");
            Insert(db, root, "b", "domain\\b.cs", prefix + "ExecuteSecond(); }");
            GraphStore.Execute(db, "UPDATE symbols SET qualified_name='Shared'");
        }
        var index = Create();
        var first = await index.BuildAsync(Database, root, Folder("first"), "c");
        Assert.Equal(2, first.EligibleChunks);
        Assert.Equal(2, first.IndexedChunks);
        Assert.Equal(1, first.NewEmbeddings);
        Assert.Equal(1, first.ReusedEmbeddings);
        Assert.Single(provider.Calls);
        Assert.Equal("ready", first.Status);
        var next = await index.BuildAsync(Database, root, Folder("next"), "c", Folder("first"));
        Assert.Equal(2, next.ReusedEmbeddings);
        Assert.Equal(0, next.NewEmbeddings);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task PrivateCheckpointAndActiveArtifact_MergeWithoutCallsForSavedContent()
    {
        Seed(180);
        var index = Create(ceiling: 180);
        await index.BuildAsync(Database, root, Folder("active"), "c");
        using var cancellation = new CancellationTokenSource();
        provider.OnCall = count => { if (count == 75) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildAsync(Database, root,
            Folder("checkpoint"), "c", Folder("active"), cancellation.Token));
        Assert.Equal(74, index.ReadCoverage(Folder("checkpoint"), "c")!.IndexedChunks);
        var firstCheckpoint = File.ReadAllBytes(Artifact("checkpoint"));
        provider.OnCall = null;
        var resume = await index.BuildAsync(Database, root, Folder("resume"), "c", Folder("active"),
            checkpointSnapshotFolder: Folder("checkpoint"));
        Assert.True(resume.ErrorType is null, resume.ToString());
        Assert.Equal(74, resume.ReusedEmbeddings);
        Assert.Equal(54, resume.NewEmbeddings);
        Assert.Equal(128, resume.IndexedChunks);
        Assert.NotEqual(firstCheckpoint, File.ReadAllBytes(Artifact("checkpoint")));
        Assert.Equal(128, index.ReadCoverage(Folder("checkpoint"))!.IndexedChunks);
        Assert.Equal(File.ReadAllBytes(Artifact("resume")), File.ReadAllBytes(Artifact("checkpoint")));
        Assert.Equal(129, provider.Calls.Count); // one canceled, unsaved request is necessarily retried
    }

    [Fact]
    public async Task SizePreflight_PreventsUnsavablePaidCalls_AndRetainsPublishedCheckpoint()
    {
        Seed(30);
        var index = Create(manifestBytes: 1024);
        var first = await index.BuildAsync(Database, root, Folder("tiny"), "c");
        Assert.Equal("ManifestSizeLimit", first.ErrorType);
        Assert.InRange(first.IndexedChunks, 1, 4);
        Assert.Equal(first.IndexedChunks, provider.Calls.Count);
        var before = File.ReadAllBytes(Artifact("tiny"));
        var next = await index.BuildAsync(Database, root, Folder("next"), "c", Folder("tiny"));
        Assert.Equal(first.IndexedChunks, next.ReusedEmbeddings);
        Assert.Equal(0, next.NewEmbeddings);
        Assert.Equal("ManifestSizeLimit", next.ErrorType);
        Assert.Equal(before, File.ReadAllBytes(Artifact("tiny")));
        Assert.NotNull(index.ReadCoverage(Folder("next")));
        Assert.True(new FileInfo(Artifact("next")).Length <= 1024);

        var bigVector = Create(manifestBytes: 1024, dimensions: 1536);
        provider.Dimensions = 1536;
        var noFit = await bigVector.BuildAsync(Database, root, Folder("no-fit"), "c");
        Assert.Equal("ManifestSizeLimit", noFit.ErrorType);
        Assert.Equal(0, noFit.NewEmbeddings);
        Assert.Equal(first.IndexedChunks, provider.Calls.Count);
    }

    [Fact]
    public async Task ProviderFailure_RetainsAllCachedVectorsAndSuccessfulNewCheckpoint()
    {
        Seed(600);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("active"), "c");
        provider.FailAt = 70;
        var result = await index.BuildAsync(Database, root, Folder("failure"), "c", Folder("active"));
        Assert.Equal(nameof(HttpRequestException), result.ErrorType);
        Assert.Equal(69, result.IndexedChunks);
        Assert.Equal(64, result.ReusedEmbeddings);
        Assert.Equal(5, result.NewEmbeddings);
        Assert.Equal(69, index.ReadCoverage(Folder("failure"))!.IndexedChunks);
        provider.FailAt = 0;
        var next = await index.BuildAsync(Database, root, Folder("next"), "c", Folder("failure"));
        Assert.Equal(69, next.ReusedEmbeddings);
        Assert.Equal(64, next.NewEmbeddings);
    }

    [Fact]
    public async Task Full768By1536Artifact_FitsEightMiB_SearchIsBoundedAndOffline()
    {
        Seed(4100);
        provider.Dimensions = 1536;
        var index = Create(dimensions: 1536);
        var stopwatch = Stopwatch.StartNew();
        CodeSemanticBuildResult? result = null;
        string? previous = null;
        for (var i = 0; i < 12; i++)
        {
            var folder = Folder($"batch-{i}");
            result = await index.BuildAsync(Database, root, folder, "c", previous,
                checkpointSnapshotFolder: Folder("durable"));
            Assert.Equal(64, result.NewEmbeddings);
            previous = folder;
        }
        Assert.Equal(768, result!.IndexedChunks);
        Assert.True(result.TargetReached);
        Assert.Equal(768, provider.Calls.Count);
        Assert.InRange(new FileInfo(Path.Combine(previous!, CodeSemanticIndex.ArtifactName)).Length, 6_000_000, 8 * 1024 * 1024);
        var query = new float[1536];
        query[0] = 1;
        var searchWatch = Stopwatch.StartNew();
        var search = index.Search(previous!, new("q", provider.Profile, query), expectedCommit: "c");
        Assert.Equal(768, search.Matches.Count);
        Assert.True(search.Coverage!.TargetReached);
        Assert.Equal(768, provider.Calls.Count);
        Assert.True(searchWatch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(3));
        Assert.True(GC.GetTotalMemory(forceFullCollection: true) < 256L * 1024 * 1024);
        Assert.Equal(File.ReadAllBytes(Path.Combine(previous!, CodeSemanticIndex.ArtifactName)), File.ReadAllBytes(Artifact("durable")));
    }

    [Fact]
    public async Task DurablePrivateCheckpoint_SurvivesCanceledStagingPrune_AndEveryPaidCallIsCheckpointed()
    {
        Seed(600);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("active"), "c");
        using var cancellation = new CancellationTokenSource();
        provider.OnCall = count =>
        {
            Assert.Equal(count - 1, index.ReadCoverage(Folder("durable"), "c")!.IndexedChunks);
            if (count == 70) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildAsync(Database, root,
            Folder("interrupted"), "c", Folder("active"), cancellation.Token, Folder("durable")));
        Assert.Equal(69, index.ReadCoverage(Folder("durable"))!.IndexedChunks);
        Assert.Equal(File.ReadAllBytes(Artifact("interrupted")), File.ReadAllBytes(Artifact("durable")));
        Directory.Delete(Folder("interrupted"), true);
        provider.OnCall = null;
        var result = await index.BuildAsync(Database, root, Folder("restarted"), "c", Folder("active"),
            checkpointSnapshotFolder: Folder("durable"));
        Assert.Equal(69, result.ReusedEmbeddings);
        Assert.Equal(64, result.NewEmbeddings);
        Assert.Equal(133, index.ReadCoverage(Folder("durable"))!.IndexedChunks);
        Assert.Equal(134, provider.Calls.Count);
    }

    [Fact]
    public async Task DurableCheckpoint_CannotAliasPublishedOrCurrentSnapshot()
    {
        Seed(2);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("active"), "c");
        var before = File.ReadAllBytes(Artifact("active"));
        var activeAlias = await index.BuildAsync(Database, root, Folder("stage"), "c", Folder("active"),
            checkpointSnapshotFolder: Path.Combine(Folder("active"), "."));
        Assert.Equal("failed", activeAlias.Status);
        Assert.Equal(nameof(InvalidOperationException), activeAlias.ErrorType);
        var stageAlias = await index.BuildAsync(Database, root, Folder("stage"), "c",
            checkpointSnapshotFolder: Folder("stage"));
        Assert.Equal("failed", stageAlias.Status);
        Assert.Equal(before, File.ReadAllBytes(Artifact("active")));
        Assert.Equal(2, provider.Calls.Count);
        Assert.False(Directory.Exists(Folder("stage")));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("policy")]
    [InlineData("root")]
    [InlineData("nonfinite")]
    [InlineData("zero")]
    [InlineData("dimension")]
    [InlineData("count")]
    public async Task TamperedCompactArtifact_IsRejectedWithoutProviderCalls(string corruption)
    {
        Seed(3);
        var index = Create();
        await index.BuildAsync(Database, root, Folder("tampered"), "c");
        var manifest = JsonNode.Parse(File.ReadAllText(Artifact("tampered")))!;
        switch (corruption)
        {
            case "target": manifest["TargetChunks"] = 769; break;
            case "policy": manifest["SelectionPolicy"] = "other"; break;
            case "root": manifest["RootHash"] = "wrong"; break;
            case "count":
                while (manifest["Entries"]!.AsArray().Count <= 768)
                    manifest["Entries"]!.AsArray().Add(manifest["Entries"]![0]!.DeepClone());
                break;
            default:
                var vector = corruption == "dimension" ? new[] { 1f } : new[] { corruption == "zero" ? 0f : float.NaN, 0f };
                manifest["Entries"]![0]!["Vector"] = Convert.ToBase64String(vector.SelectMany(BitConverter.GetBytes).ToArray());
                break;
        }
        RehashCompact(manifest);
        File.WriteAllText(Artifact("tampered"), manifest.ToJsonString());
        Assert.Null(index.ReadCoverage(Folder("tampered")));
        Assert.Equal("invalid", index.Search(Folder("tampered"), new("q", provider.Profile, [1, 0])).Status);
        Assert.Equal(3, provider.Calls.Count);
    }

    private CodeSemanticIndex Create(int ceiling = 768, int budget = 64, bool adaptive = true,
        int candidateLimit = 8192, int candidateBytes = 16 * 1024 * 1024, int manifestBytes = 8 * 1024 * 1024,
        int dimensions = 2) => new(provider, Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions
        {
            Enabled = true, AdaptiveCoverageEnabled = adaptive, MaxChunks = ceiling,
            MaxNewEmbeddingsPerBuild = budget, CandidateLimit = candidateLimit, CandidateBytes = candidateBytes,
            Dimensions = dimensions, MaxManifestBytes = manifestBytes
        }), NullLogger<CodeSemanticIndex>.Instance);

    private void Seed(int count)
    {
        Directory.CreateDirectory(root);
        using var db = GraphStore.Open(Database);
        using var transaction = db.BeginTransaction();
        GraphStore.Execute(db, "INSERT INTO repositories VALUES(@p0,'now',1,@p1,0,@p2,@p3)",
            GraphStore.RootKey(root), count, GraphStore.Version, GraphStore.Parser);
        for (var i = 0; i < count; i++)
            Insert(db, root, $"symbol-{i:D5}", $"{(i % 5 == 0 ? "api" : "domain")}\\file{i:D5}.cs",
                $"void Operation_{i}() {{ ExecuteWorkflow({i}); CommitChanges(); }}");
        transaction.Commit();
    }

    private static void Insert(Microsoft.Data.Sqlite.SqliteConnection db, string root, string id, string path, string source) =>
        GraphStore.Execute(db, "INSERT INTO symbols VALUES(@p0,@p1,@p2,'method',@p0,@p0,1,1,NULL,@p3,'')",
            id, GraphStore.RootKey(root), path, source);

    private string MoveRootAndIds()
    {
        var nextRoot = Folder("next-source");
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, "INSERT INTO repositories VALUES(@p0,'now',1,1,0,@p1,@p2)",
            GraphStore.RootKey(nextRoot), GraphStore.Version, GraphStore.Parser);
        GraphStore.Execute(db, "UPDATE symbols SET repo_root=@p0,id='root-moved-'||id", GraphStore.RootKey(nextRoot));
        GraphStore.Execute(db, "DELETE FROM repositories WHERE root=@p0", GraphStore.RootKey(root));
        return nextRoot;
    }

    private void ConvertToLegacy(string name)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Artifact(name)))!.AsObject();
        manifest["Version"] = 2;
        foreach (var entry in manifest["Entries"]!.AsArray())
        {
            var bytes = Convert.FromBase64String(entry!["Vector"]!.GetValue<string>());
            entry["Vector"] = new JsonArray(Enumerable.Range(0, bytes.Length / 4)
                .Select(i => (JsonNode?)JsonValue.Create(BitConverter.ToSingle(bytes, i * 4))).ToArray());
            entry.AsObject().Remove("FilePath");
            entry.AsObject().Remove("StartLine");
        }
        var entries = manifest["Entries"]!.AsArray().Select(entry => new LegacyEntry(
            entry!["SymbolId"]!.GetValue<string>(), entry["ContentHash"]!.GetValue<string>(),
            entry["Vector"]!.AsArray().Select(value => value!.GetValue<float>()).ToArray())).ToArray();
        var identity = JsonSerializer.Serialize(new
        {
            Version = 2, Profile = manifest["Profile"]!.GetValue<string>(), Dimensions = 2,
            Commit = manifest["Commit"]!.GetValue<string>(), RootHash = manifest["RootHash"]!.GetValue<string>(),
            EligibleChunks = manifest["EligibleChunks"]!.GetValue<int>(), Status = manifest["Status"]!.GetValue<string>(),
            ErrorType = (string?)null, Entries = entries
        });
        manifest["ContentHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        foreach (var property in new[] { "TargetChunks", "TargetReached", "SelectionPolicy", "ScanCapped", "CoverageCapped",
            "RetentionPolicyVersion", "LegacyRecoveryPending", "UsefulChunks" })
            manifest.Remove(property);
        File.WriteAllText(Artifact(name), manifest.ToJsonString());
    }

    private static void RehashCompact(JsonNode manifest)
    {
        var identity = JsonSerializer.SerializeToNode(new
        {
            Version = manifest["Version"]!.GetValue<int>(),
            Profile = manifest["Profile"]!.GetValue<string>(),
            Dimensions = manifest["Dimensions"]!.GetValue<int>(),
            Commit = manifest["Commit"]!.GetValue<string>(),
            RootHash = manifest["RootHash"]!.GetValue<string>(),
            EligibleChunks = manifest["EligibleChunks"]!.GetValue<int>(),
            Status = manifest["Status"]!.GetValue<string>(),
            ErrorType = manifest["ErrorType"]?.GetValue<string>(),
            Entries = manifest["Entries"],
            TargetChunks = manifest["TargetChunks"]!.GetValue<int>(),
            TargetReached = manifest["TargetReached"]!.GetValue<bool>(),
            SelectionPolicy = manifest["SelectionPolicy"]!.GetValue<string>(),
            ScanCapped = manifest["ScanCapped"]!.GetValue<bool>(),
            CoverageCapped = manifest["CoverageCapped"]!.GetValue<bool>()
        })!.AsObject();
        var retentionVersion = manifest["RetentionPolicyVersion"]?.GetValue<int>() ?? 0;
        if (retentionVersion > 0)
        {
            identity["RetentionPolicyVersion"] = retentionVersion;
            identity["LegacyRecoveryPending"] = manifest["LegacyRecoveryPending"]?.GetValue<bool>() ?? false;
            identity["UsefulChunks"] = manifest["UsefulChunks"]?.GetValue<int>() ?? 0;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        manifest["ContentHash"] = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record LegacyEntry(string SymbolId, string ContentHash, float[] Vector);
    private sealed class Embeddings : IEmbeddingService
    {
        public string Profile { get; set; } = "test-model:1536";
        public int Dimensions { get; set; } = 2;
        public int FailAt { get; set; }
        public List<string> Calls { get; } = [];
        public Action<int>? OnCall { get; set; }
        public Task<float[]> GenerateEmbedding(string text) => GenerateEmbedding(text, CancellationToken.None);
        public Task<float[]> GenerateEmbedding(string text, CancellationToken token)
        {
            Calls.Add(text);
            OnCall?.Invoke(Calls.Count);
            token.ThrowIfCancellationRequested();
            if (Calls.Count == FailAt) throw new HttpRequestException();
            var vector = new float[Dimensions];
            vector[0] = 1;
            for (var i = 1; i < Dimensions; i++) vector[i] = MathF.Sin(i) * 0.25f;
            return Task.FromResult(vector);
        }
    }
}
