using System.Diagnostics;
using System.Text.Json;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using PoolSense.Application.Models;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class CodeRetrievalImprovementsTests(ITestOutputHelper output)
{
    private static CodeKnowledgeService Service(RetrievalFixture fixture, CodeSemanticIndex? semantic = null, OkfOptions? knowledge = null) =>
        new(fixture.Store, null!, fixture.GitHub, fixture.Access, fixture.Files, fixture.Context,
            Microsoft.Extensions.Options.Options.Create(fixture.Settings), NullLogger<CodeKnowledgeService>.Instance, semantic,
            knowledge is null ? null : Microsoft.Extensions.Options.Options.Create(knowledge));

    [Fact]
    public async Task ParallelSearch_DoesNotUseHeadChecksOrGlobalIndexGate_AndHonorsScope()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        await fixture.Files.Gate.WaitAsync();
        try
        {
            var result = await Service(fixture).RetrieveAsync("CaptureCharge", ["project"], default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEmpty(result.Documents);
            Assert.Equal(0, fixture.GitHub.HeadCalls);
            Assert.Equal(3, fixture.GitHub.AccessCalls);
            Assert.InRange(fixture.GitHub.MaxActive, 2, fixture.Settings.SearchConcurrency);
            Assert.DoesNotContain(result.Documents, document => document.ProjectId == "shipping-only");
            Assert.All(result.Documents, document => Assert.Contains("/blob/" + new string('a', 40) + "/", document.SourceUrl));
        }
        finally { fixture.Files.Gate.Release(); }
    }

    [Fact]
    public async Task Deadline_PreservesCompletedEvidence_AndCallerCancellationPropagates()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        fixture.Settings.SearchDeadlineSeconds = .2;
        fixture.GitHub.SlowRepository = "refunds";
        var result = await Service(fixture).RetrieveAsync("CaptureCharge", ["project", "shipping-only"], default);
        Assert.Equal("partial", result.Status);
        Assert.Contains(result.Documents, document => document.FilePath == "Orders.cs");
        Assert.True(result.Coverage.DeadlineExceeded);
        Assert.False(result.Coverage.AuthorizationComplete);
        using var caller = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(fixture).RetrieveAsync("CaptureCharge", ["project"], caller.Token));
    }

    [Fact]
    public async Task Routing_UsesBroaderFallbackForWeakAndCrossRepositoryQuestions()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        fixture.Settings.RepositoryRoutingEnabled = true;
        fixture.Settings.RepositoryShortlistSize = 1;
        var targeted = await Service(fixture).RetrieveAsync("orders CaptureCharge", ["project", "shipping-only"], default);
        Assert.Contains(targeted.Documents, document => document.FilePath == "Orders.cs");
        Assert.Equal(1, targeted.Coverage.SearchedRepositories);
        var broader = await Service(fixture).RetrieveAsync("CaptureCharge ReserveStock workflow", ["project", "shipping-only"], default);
        Assert.True(broader.Coverage.ExpandedFallback);
        Assert.Equal(4, broader.Coverage.SearchedRepositories);
        Assert.Contains(broader.Documents, document => document.FilePath == "Orders.cs");
        Assert.Contains(broader.Documents, document => document.FilePath == "Shipping.cs");
    }

    [Fact]
    public async Task SnapshotLeases_DeferPruningUntilAllReadersRelease()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var old = fixture.Store.Entries[0];
        var first = fixture.Files.Lease(old)!;
        var second = fixture.Files.Lease(old)!;
        fixture.Files.Prune(old with { Generation = Guid.NewGuid().ToString("N") });
        Assert.True(Directory.Exists(fixture.Files.Folder(old)));
        first.Dispose();
        Assert.True(Directory.Exists(fixture.Files.Folder(old)));
        second.Dispose();
        Assert.False(Directory.Exists(fixture.Files.Folder(old)));
        Assert.Null(fixture.Files.Lease(old));
    }

    [Fact]
    public async Task UnchangedCommitOptionalPreparation_ReusesCachedSourceWithoutDownloadingAgain()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var previous = fixture.Store.Entries[0];
        using var lease = fixture.Files.Lease(previous)!;
        using var originalDatabase = GraphStore.Open(fixture.Files.Database(previous), true);
        var originalSummary = Assert.Single(GraphStore.Summaries(originalDatabase));
        var originalIds = GraphStore.Query(originalDatabase, "SELECT * FROM symbols ORDER BY id", GraphStore.ReadSymbol).Select(symbol => symbol.Id).ToArray();
        var staged = previous with { Generation = Guid.NewGuid().ToString("N") };
        var factoryCalls = 0;
        var builder = new CodeSnapshotBuilder(fixture.GitHub, fixture.Files,
            knowledge: Microsoft.Extensions.Options.Options.Create(new OkfOptions()),
            llmFactory: () => { factoryCalls++; throw new InvalidOperationException("Must remain lazy while generation is disabled"); });
        var summary = await builder.BuildAsync(staged, previous.IndexedCommit, default, fixture.Files.Folder(previous));
        Assert.True(summary.FileCount > 0);
        Assert.True(fixture.Files.Ready(staged));
        Assert.True(File.Exists(Path.Combine(fixture.Files.Folder(staged), CodeSnapshotPreparation.FileName)));
        Assert.Equal(0, factoryCalls);
        Assert.Equal(originalSummary.IndexedAt, summary.IndexedAt);
        using var copiedDatabase = GraphStore.Open(fixture.Files.Database(staged), true);
        Assert.Equal(originalIds, GraphStore.Query(copiedDatabase, "SELECT * FROM symbols ORDER BY id", GraphStore.ReadSymbol).Select(symbol => symbol.Id).ToArray());
        Assert.Equal(GraphStore.RootKey(fixture.Files.Source(staged)), summary.Root);
        // Fixture DownloadAsync throws, so success proves this background path used only leased local source.
    }

    [Fact]
    public async Task OlderPreparationIsScheduledForUpgradeButCurrentIncompleteWorkKeepsNormalBackoff()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var repository = fixture.Store.Entries[0];
        var folder = fixture.Files.Folder(repository);
        var path = Path.Combine(folder, CodeSnapshotPreparation.FileName);
        var old = new CodeSnapshotPreparation("old-profile", true, "prepared", "disabled");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(old));
        Assert.True(CodeSnapshotPreparation.NeedsUpgrade(folder));
        var current = old with { Complete = false, PreparationVersion = CodeSnapshotPreparation.Version };
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(current));
        Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder));
        Assert.True(CodeSnapshotPreparation.NeedsRefresh(folder, new OkfOptions(), null));
    }

    [Theory]
    [InlineData("disabled", true)]
    [InlineData("ready", true)]
    [InlineData("failed", false)]
    [InlineData("partial", false)]
    [InlineData("unavailable", false)]
    public async Task SemanticActivationSchedulesMissingVectorsWithoutBypassingFailureBackoff(string semanticStatus, bool expected)
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var folder = fixture.Files.Folder(fixture.Store.Entries[0]);
        var state = new CodeSnapshotPreparation("profile", true, "prepared", semanticStatus)
            { PreparationVersion = CodeSnapshotPreparation.Version };
        await File.WriteAllTextAsync(Path.Combine(folder, CodeSnapshotPreparation.FileName),
            System.Text.Json.JsonSerializer.Serialize(state));
        Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder));
        Assert.Equal(expected, CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true));
        if (semanticStatus == "ready")
        {
            await File.WriteAllTextAsync(Path.Combine(folder, CodeSemanticIndex.ArtifactName), "{}");
            Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true));
        }
    }

    [Fact]
    public async Task ProgressiveCooldownAndPolicyMigrationAreFiniteAndFair()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var folder = fixture.Files.Folder(fixture.Store.Entries[0]);
        var now = DateTimeOffset.UtcNow;
        var state = new CodeSnapshotPreparation("profile", false, "prepared", "partial")
        {
            PreparationVersion = CodeSnapshotPreparation.Version, SemanticSelectionPolicy = "current",
            SemanticIndexedChunks = 64, SemanticTargetChunks = 512, NextSemanticRefreshAt = now.AddMinutes(5)
        };
        var path = Path.Combine(folder, CodeSnapshotPreparation.FileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
        Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true, semanticPolicy: "current", now: now));
        Assert.True(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true, semanticPolicy: "changed", now: now));
        Assert.True(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true, semanticPolicy: "current", now: now.AddMinutes(5)));
        Assert.Equal(.125, CodeSnapshotPreparation.SemanticProgress(folder, NullLogger.Instance));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state with { SemanticTargetReached = true, SemanticIndexedChunks = 512 }));
        Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true, semanticPolicy: "current", now: now.AddHours(1)));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state with { SemanticErrorType = "ProviderFailure" }));
        Assert.False(CodeSnapshotPreparation.NeedsUpgrade(folder, semanticEnabled: true, semanticPolicy: "current", now: now.AddHours(1)));
    }

    [Fact]
    public async Task CancelledBatchCheckpointSurvivesPruneAndResumesWithoutReembeddingSavedVectors()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var original = fixture.Store.Entries[0];
        var staged = original with { Generation = Guid.NewGuid().ToString("N") };
        using var cancellation = new CancellationTokenSource();
        var provider = new CancellingEmbeddings(cancellation);
        var semantic = new CodeSemanticIndex(provider,
            Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions { Enabled = true, Dimensions = 4, MaxChunks = 8 }),
            NullLogger<CodeSemanticIndex>.Instance);
        var builder = new CodeSnapshotBuilder(fixture.GitHub, fixture.Files, semantic);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync(staged, staged.IndexedCommit, cancellation.Token, fixture.Files.Folder(original)));
        var checkpoint = semantic.ReadCoverage(fixture.Files.SemanticCheckpoint(original));
        Assert.Equal(1, checkpoint!.IndexedChunks);
        fixture.Files.Prune(original);
        Assert.True(Directory.Exists(fixture.Files.SemanticCheckpoint(original)));
        provider.CancelOnSecond = false;
        var next = original with { Generation = Guid.NewGuid().ToString("N") };
        await builder.BuildAsync(next, next.IndexedCommit, default, fixture.Files.Folder(original));
        var coverage = semantic.ReadCoverage(fixture.Files.Folder(next));
        Assert.True(coverage!.IndexedChunks > 1);
        Assert.Equal(coverage.IndexedChunks + 1, provider.Calls);
        fixture.Files.ClearSemanticCheckpoint(original);
        Assert.False(Directory.Exists(fixture.Files.SemanticCheckpoint(original)));
    }

    private sealed class CancellingEmbeddings(CancellationTokenSource cancellation) : IEmbeddingService
    {
        public string Profile => "cancellation-fixture";
        public int Calls;
        public bool CancelOnSecond = true;
        public Task<float[]> GenerateEmbedding(string text)
        {
            Calls++;
            if (Calls == 2 && CancelOnSecond)
            {
                cancellation.Cancel();
                return Task.FromCanceled<float[]>(cancellation.Token);
            }
            return Task.FromResult<float[]>([1, 0, 0, 0]);
        }
    }

    [Fact]
    public async Task UnchangedKnowledgeIsReusedWithoutTimestampOrLogChurnAcrossOptionalRetries()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var previous = fixture.Store.Entries[0];
        var first = previous with { Generation = Guid.NewGuid().ToString("N") };
        var second = previous with { Generation = Guid.NewGuid().ToString("N") };
        var builder = new CodeSnapshotBuilder(fixture.GitHub, fixture.Files,
            knowledge: Microsoft.Extensions.Options.Options.Create(new OkfOptions()));
        await builder.BuildAsync(first, first.IndexedCommit, default, fixture.Files.Folder(previous));
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Files.Folder(first), "okf", "overview.md"));
        var log = await File.ReadAllTextAsync(Path.Combine(fixture.Files.Folder(first), "okf", "log.md"));
        await builder.BuildAsync(second, second.IndexedCommit, default, fixture.Files.Folder(first));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(fixture.Files.Folder(second), "okf", "overview.md")));
        Assert.Equal(log, await File.ReadAllTextAsync(Path.Combine(fixture.Files.Folder(second), "okf", "log.md")));
    }

    [Fact]
    public async Task Disconnect_DiscardsPrivateEvidence_AndNoAccessDoesNotReadOptionalIndexes()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var provider = new FixtureEmbeddings();
        var semantic = Semantic(provider);
        foreach (var repository in fixture.Store.Entries)
        {
            await semantic.BuildAsync(fixture.Files.Database(repository), fixture.Files.Source(repository), fixture.Files.Folder(repository), repository.IndexedCommit);
            Assert.NotNull(semantic.WarmSnapshot(fixture.Files.Folder(repository), repository.IndexedCommit));
        }
        provider.Calls = 0;
        fixture.GitHub.Denied = true;
        var denied = await Service(fixture, semantic).RetrieveAsync("customer reimbursement", ["project"], default);
        Assert.Empty(denied.Documents);
        Assert.Equal(0, denied.Coverage.AuthorizedRepositories);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("not-available", denied.Coverage.KnowledgeStatus);
        fixture.GitHub.Denied = false;
        fixture.GitHub.OnAccess = () => fixture.Access.Identity = new(2, "rotated", "different-protected-token");
        var disconnected = await Service(fixture, semantic).RetrieveAsync("CaptureCharge", ["project"], default);
        Assert.Equal("not-connected", disconnected.Status);
        Assert.Empty(disconnected.Documents);
    }

    [Fact]
    public async Task SemanticQuery_ReusesExactOrchestratorEmbedding_AndNeverWritesDuringSearch()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var provider = new FixtureEmbeddings();
        var semantic = Semantic(provider);
        foreach (var repository in fixture.Store.Entries)
            await semantic.BuildAsync(fixture.Files.Database(repository), fixture.Files.Source(repository), fixture.Files.Folder(repository), repository.IndexedCommit);
        var before = Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.GetLastWriteTimeUtc);
        provider.Calls = 0;
        var result = await Service(fixture, semantic).ForRequestAsync("customer reimbursement",
            new TicketRequest { SelectedGroupIds = ["project", "shipping-only"] }, default,
            new CodeQueryEmbedding("customer reimbursement", provider.Profile, [1, 0, 0, 0]));
        Assert.Contains(result.Documents, document => document.FilePath == "Refunds.cs");
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, fixture.GitHub.HeadCalls);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key)));
    }

    [Fact]
    public async Task RequestScopedLexicalRetry_DoesNotGenerateASecondQueryEmbedding()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var provider = new FixtureEmbeddings();
        var semantic = Semantic(provider);
        foreach (var repository in fixture.Store.Entries)
            await semantic.BuildAsync(fixture.Files.Database(repository), fixture.Files.Source(repository), fixture.Files.Folder(repository), repository.IndexedCommit);
        provider.Calls = 0;
        var service = Service(fixture, semantic);
        await service.RetrieveAsync("customer reimbursement", ["project"], default);
        var second = await service.RetrieveAsync("CaptureCharge", ["project"], default);
        Assert.Equal(1, provider.Calls);
        Assert.Contains(second.Documents, document => document.FilePath == "Orders.cs");
        Assert.Contains(second.Coverage.Warnings, warning => warning.Contains("Semantic retrieval unavailable"));
    }

    [Fact]
    public void CandidatePool_IsBoundedAndLateStrongMatchesReplaceWeakOnes()
    {
        var repository = new CodeRepository { Id = "repo" };
        var pool = new CodeCandidateBuffer("CaptureCharge", 24);
        for (var index = 0; index < 100; index++)
            pool.Add(new(repository, new Symbol(index.ToString(), "root", $"Noise{index}.cs", "Method", "Noise", "Noise", 1, 1, null, "unrelated noise", ""), index + 1));
        pool.Add(new(repository, new Symbol("exact", "root", "Orders.cs", "Method", "CaptureCharge", "CaptureCharge", 1, 1, null, "void CaptureCharge() {}", ""), 1));
        Assert.Equal(24, pool.Count);
        Assert.True(pool.Limited);
        Assert.Contains(pool, candidate => candidate.Symbol.Name == "CaptureCharge");
    }

    [Fact]
    public void PartialCoverage_RoundTripsInSavedWorkflowResult_AndNoticePreservesHistoryWarning()
    {
        var coverage = new CodeSearchCoverage { AuthorizedRepositories = 3, SearchedRepositories = 1, DeadlineExceeded = true, AuthorizationComplete = false };
        var result = new PoolSense.Api.Models.TicketWorkflowResult { CodeKnowledgeStatus = "partial", CodeSearchCoverage = coverage };
        var restored = JsonSerializer.Deserialize<PoolSense.Api.Models.TicketWorkflowResult>(JsonSerializer.Serialize(result))!;
        Assert.True(restored.CodeSearchCoverage!.DeadlineExceeded);
        Assert.Equal(1, restored.CodeSearchCoverage.SearchedRepositories);
        var notice = CodeRetrievalMessages.Notice(new("partial", []) { Coverage = coverage });
        Assert.Contains("not exhaustive", notice);
        Assert.Contains("Searched 1 of 3", notice);
        Assert.Contains("unverified repositories were not used", notice);
    }

    [Fact]
    public async Task CompareIdenticalFixtures_DefaultLexicalAndOptInHybrid()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var provider = new FixtureEmbeddings();
        var semantic = Semantic(provider);
        foreach (var repository in fixture.Store.Entries)
        {
            await semantic.BuildAsync(fixture.Files.Database(repository), fixture.Files.Source(repository), fixture.Files.Folder(repository), repository.IndexedCommit);
            await OkfBundle.PrepareAsync(repository, repository.IndexedCommit, fixture.Files.Database(repository), fixture.Files.Source(repository),
                fixture.Files.Folder(repository), new OkfOptions());
        }
        var rows = new List<object>();
        foreach (var example in RetrievalFixture.Examples)
        {
            foreach (var mode in new[] { "frozen-baseline", "parallel-lexical", "parallel-okf", "mock-hybrid" })
            {
                var samples = new List<double>();
                CodeRetrievalResult result = CodeRetrievalResult.Skipped;
                for (var run = 0; run < 3; run++)
                {
                    provider.Calls = 0;
                    var watch = Stopwatch.StartNew();
                    result = mode == "frozen-baseline"
                        ? await fixture.LegacyAsync(example.Query, example.Scope, TimeSpan.FromSeconds(20))
                        : await Service(fixture, mode == "mock-hybrid" ? semantic : null,
                            mode is "parallel-okf" or "mock-hybrid" ? new OkfOptions() : null).RetrieveAsync(example.Query, example.Scope, default);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.InRange(provider.Calls, 0, 1);
                }
                var hits = example.Expected.Count(path => result.Documents.Any(document => document.FilePath == path));
                var rank = result.Documents.ToList().FindIndex(document => example.Expected.Contains(document.FilePath));
                var row = new
                {
                    example.Name,
                    mode,
                    elapsedMs = samples.Order().ElementAt(1),
                    samplesMs = samples,
                    recall = example.Expected.Length == 0 ? (double?)null : (double)hits / example.Expected.Length,
                    firstRelevantRank = rank < 0 ? (int?)null : rank + 1,
                    returned = result.Documents.Count,
                    result.Status,
                    queryEmbeddings = provider.Calls
                };
                rows.Add(row);
                output.WriteLine(JsonSerializer.Serialize(row));
                if (example.Expected.Length == 0) Assert.Empty(result.Documents);
                else if (example.Name != "business-paraphrase" || mode == "mock-hybrid") Assert.Equal(example.Expected.Length, hits);
                Assert.InRange(provider.Calls, 0, 1);
            }
        }
        async Task<CodeRetrievalResult> Extra(string name, string mode, string query)
        {
            provider.Calls = 0;
            var watch = Stopwatch.StartNew();
            var result = mode == "frozen-baseline"
                ? await fixture.LegacyAsync(query, ["project", "shipping-only"], TimeSpan.FromSeconds(20))
                : await Service(fixture, mode == "mock-hybrid" ? semantic : null,
                    mode is "parallel-okf" or "mock-hybrid" ? new OkfOptions() : null).RetrieveAsync(query, ["project", "shipping-only"], default);
            rows.Add(new { Name = name, mode, elapsedMs = watch.Elapsed.TotalMilliseconds, returned = result.Documents.Count, result.Status, queryEmbeddings = provider.Calls });
            return result;
        }
        fixture.GitHub.Denied = true;
        foreach (var mode in new[] { "frozen-baseline", "parallel-lexical", "parallel-okf", "mock-hybrid" })
            Assert.Empty((await Extra("no-access", mode, "CaptureCharge")).Documents);
        fixture.GitHub.Denied = false;
        fixture.Store.Entries[0] = fixture.Store.Entries[0] with { Status = "error" };
        foreach (var mode in new[] { "frozen-baseline", "parallel-lexical", "parallel-okf", "mock-hybrid" })
            Assert.Equal("stale", (await Extra("stale-source", mode, "CaptureCharge")).Status);
        var old = fixture.Store.Entries[0];
        var refreshed = old with { Generation = Guid.NewGuid().ToString("N"), IndexedCommit = new string('b', 40), Status = "ready", LastCheckedAt = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(fixture.Files.Source(refreshed));
        await File.WriteAllTextAsync(Path.Combine(fixture.Files.Source(refreshed), "Orders.cs"), "public class UpdatedSettlement { public void CaptureChargeV2() {} }");
        await new RepositoryIndexer().IndexAsync(fixture.Files.Source(refreshed), fixture.Files.Database(refreshed));
        await semantic.BuildAsync(fixture.Files.Database(refreshed), fixture.Files.Source(refreshed), fixture.Files.Folder(refreshed), refreshed.IndexedCommit, fixture.Files.Folder(old));
        await OkfBundle.PrepareAsync(refreshed, refreshed.IndexedCommit, fixture.Files.Database(refreshed), fixture.Files.Source(refreshed),
            fixture.Files.Folder(refreshed), new OkfOptions());
        fixture.Store.Entries[0] = refreshed;
        fixture.Files.Prune(refreshed);
        foreach (var mode in new[] { "frozen-baseline", "parallel-lexical", "parallel-okf", "mock-hybrid" })
        {
            var current = await Extra("refreshed-source", mode, "CaptureChargeV2");
            Assert.NotEmpty(current.Documents);
            Assert.All(current.Documents, document => Assert.Contains("/blob/" + new string('b', 40) + "/", document.SourceUrl));
        }
        fixture.GitHub.SlowRepository = "refunds";
        fixture.Settings.SearchDeadlineSeconds = .2;
        var timeoutBefore = await fixture.LegacyAsync("CaptureCharge", ["project"], TimeSpan.FromMilliseconds(200));
        var timeoutAfter = await Service(fixture).RetrieveAsync("CaptureCharge", ["project"], default);
        rows.Add(new { Name = "scaled-200ms-timeout", mode = "frozen-baseline", returned = timeoutBefore.Documents.Count, timeoutBefore.Status });
        rows.Add(new { Name = "scaled-200ms-timeout", mode = "parallel-lexical", returned = timeoutAfter.Documents.Count, timeoutAfter.Status });
        Assert.Empty(timeoutBefore.Documents);
        Assert.NotEmpty(timeoutAfter.Documents);
        Assert.True(timeoutAfter.Coverage.DeadlineExceeded);
        var path = Environment.GetEnvironmentVariable("POOLSENSE_EVAL_OUTPUT");
        if (!string.IsNullOrEmpty(path)) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void GlobalSelection_DeduplicatesOverlapsAndKeepsStrongDiverseMatches()
    {
        var a = new CodeRepository { Id = "a", FullName = "org/a", IndexedCommit = new string('a', 40) };
        var b = a with { Id = "b", FullName = "org/b" };
        Symbol Symbol(string id, string name, int start, int end, string source) => new(id, "root", "Code.cs", "Method", name, name, start, end, null, source, "");
        var exact = Symbol("exact", "CaptureCharge", 10, 12, "void CaptureCharge() { Authorize(); }");
        var overlap = Symbol("class", "Parent", 1, 30, "class Parent { void CaptureCharge() {} }");
        var other = Symbol("other", "ReserveStock", 1, 3, "void ReserveStock() { Allocate(); }");
        var selected = CodeCandidateSelection.Select("CaptureCharge ReserveStock",
            [new(a, exact, 1), new(a, overlap, 2), new(b, other, 1), new(b, exact, 2),
                new(b, Symbol("weak", "Menu", 50, 51, "void OpenMenu() {}"), KnowledgeReference: true)], 12, 24000);
        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, document => document.RepositoryId == "a" && document.StartLine == 10);
        Assert.Contains(selected, document => document.RepositoryId == "b" && document.Symbol == "ReserveStock");
    }

    private static CodeSemanticIndex Semantic(FixtureEmbeddings provider) => new(provider,
        Microsoft.Extensions.Options.Options.Create(new CodeSemanticOptions { Enabled = true, Dimensions = 4 }),
        NullLogger<CodeSemanticIndex>.Instance);

    private sealed class FixtureEmbeddings : IEmbeddingService
    {
        public int Calls;
        public bool Fail;
        public string Profile => "synthetic-fixed-vectors-v1";
        public Task<float[]> GenerateEmbedding(string text)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new InvalidOperationException("Synthetic provider outage");
            if (text.Contains("CaptureCharge") && text.Contains("ReserveStock")) return Task.FromResult<float[]>([0, 1, 1, 0]);
            if (text.Contains("customer reimbursement") || text.Contains("Refund") || text.Contains("ReversePayment")) return Task.FromResult<float[]>([1, 0, 0, 0]);
            if (text.Contains("CaptureCharge") || text.Contains("OrderSettlement") || text.Contains("Orders.cs")) return Task.FromResult<float[]>([0, 1, 0, 0]);
            if (text.Contains("ReserveStock") || text.Contains("Shipment") || text.Contains("Shipping.cs")) return Task.FromResult<float[]>([0, 0, 1, 0]);
            if (text.Contains("Navigation") || text.Contains("Menu.cs")) return Task.FromResult<float[]>([0, 0, 0, -1]);
            return Task.FromResult<float[]>([0, 0, 0, 1]);
        }
    }

    [Fact]
    public async Task SemanticProviderFailure_LeavesWorkingLexicalEvidenceAndExplicitDegradation()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var provider = new FixtureEmbeddings();
        var semantic = Semantic(provider);
        foreach (var repository in fixture.Store.Entries)
            await semantic.BuildAsync(fixture.Files.Database(repository), fixture.Files.Source(repository), fixture.Files.Folder(repository), repository.IndexedCommit);
        provider.Fail = true;
        var result = await Service(fixture, semantic).RetrieveAsync("CaptureCharge", ["project"], default);
        Assert.Contains(result.Documents, document => document.FilePath == "Orders.cs");
        Assert.Contains(result.Coverage.Warnings, warning => warning.Contains("Semantic retrieval unavailable"));
        Assert.Contains("Semantic retrieval is limited", CodeRetrievalMessages.Notice(result));
    }

    [Fact]
    public async Task Search_UsesExistingOkfReferencesOnlyAfterAccess_AndNeverGeneratesMissingBundles()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var knowledge = new OkfOptions { Enabled = true };
        var repo = fixture.Store.Entries[0];
        var prepared = await OkfBundle.PrepareAsync(repo, repo.IndexedCommit, fixture.Files.Database(repo), fixture.Files.Source(repo),
            fixture.Files.Folder(repo), knowledge);
        Assert.Equal("prepared", prepared.Status);
        var result = await Service(fixture, knowledge: knowledge).RetrieveAsync("CaptureCharge", ["project"], default);
        Assert.Contains(result.Documents, document => document.FilePath == "Orders.cs");
        Assert.Contains("ready", result.Coverage.KnowledgeStatus);
        Assert.All(fixture.Store.Entries.Skip(1), repository => Assert.False(Directory.Exists(Path.Combine(fixture.Files.Folder(repository), "okf"))));
        fixture.GitHub.Denied = true;
        var denied = await Service(fixture, knowledge: knowledge).RetrieveAsync("CaptureCharge", ["project"], default);
        Assert.Equal("not-available", denied.Coverage.KnowledgeStatus);
        Assert.Empty(denied.Documents);
    }

    [Fact]
    public async Task BackgroundRefresh_PublishesAtomically_AndCoalescesSameRepositoryWhileReadersContinue()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var original = fixture.Store.Entries[0];
        using var reader = fixture.Files.Lease(original)!;
        fixture.GitHub.HeadOverride = new string('b', 40);
        var builder = new PausedBuilder(fixture.Files);
        var coordinator = new CodeIndexCoordinator(fixture.Store, fixture.GitHub, fixture.Access, builder, fixture.Files,
            Microsoft.Extensions.Options.Options.Create(fixture.Settings), TimeProvider.System);
        var refresh = coordinator.RefreshAsync(original, default);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await coordinator.RefreshAsync(original, default);
            Assert.Equal(1, builder.Calls);
            await fixture.Files.CleanupAsync(fixture.Store, default);
            Assert.True(Directory.Exists(fixture.Files.Folder(builder.Staged!)));
            var during = await Service(fixture).RetrieveAsync("CaptureCharge", ["project"], default);
            Assert.Contains(during.Documents, document => document.Commit == original.IndexedCommit);
            Assert.Equal("stale", during.Status);
        }
        finally
        {
            fixture.GitHub.HeadOverride = new string('c', 40);
            builder.Continue.TrySetResult();
            await refresh;
        }
        Assert.True(Directory.Exists(fixture.Files.Folder(original)));
        Assert.Equal("queued", fixture.Store.Entries[0].Status);
        var after = await Service(fixture).RetrieveAsync("CaptureChargeV2", ["project"], default);
        Assert.Contains(after.Documents, document => document.Commit == new string('b', 40));
        reader.Dispose();
        Assert.False(Directory.Exists(fixture.Files.Folder(original)));
    }

    private sealed class PausedBuilder(CodeIndexFiles files) : ICodeSnapshotBuilder
    {
        public int Calls;
        public CodeRepository? Staged;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token, string? previousSnapshotFolder = null)
        {
            Calls++;
            Staged = repository;
            Directory.CreateDirectory(files.Source(repository));
            await File.WriteAllTextAsync(Path.Combine(files.Source(repository), "Orders.cs"), "public class UpdatedSettlement { public void CaptureChargeV2() {} }", token);
            var summary = (await new RepositoryIndexer().IndexAsync(files.Source(repository), files.Database(repository), cancellationToken: token)).Summary;
            Started.TrySetResult();
            await Continue.Task.WaitAsync(token);
            return summary;
        }
    }
}
