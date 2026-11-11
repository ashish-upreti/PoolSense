using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PoolSense.Api.Services.CodeRAG;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class CodeRetrievalEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecordFrozenLexicalBaseline()
    {
        using var fixture = await RetrievalFixture.CreateAsync();
        var measurements = new List<object>();
        foreach (var example in RetrievalFixture.Examples)
        {
            var watch = Stopwatch.StartNew();
            var result = await fixture.LegacyAsync(example.Query, example.Scope, TimeSpan.FromSeconds(20));
            watch.Stop();
            var hits = example.Expected.Count(path => result.Documents.Any(document => document.FilePath == path));
            var rank = result.Documents.ToList().FindIndex(document => example.Expected.Contains(document.FilePath));
            var row = new { example.Name, elapsedMs = watch.Elapsed.TotalMilliseconds,
                recall = example.Expected.Length == 0 ? (double?)null : (double)hits / example.Expected.Length,
                firstRelevantRank = rank < 0 ? (int?)null : rank + 1, returned = result.Documents.Count, result.Status };
            output.WriteLine(JsonSerializer.Serialize(row));
            measurements.Add(row);
        }
        fixture.GitHub.Denied = true;
        var denied = await fixture.LegacyAsync("CaptureCharge", ["project"], TimeSpan.FromSeconds(20));
        Assert.Empty(denied.Documents);
        fixture.GitHub.Denied = false;
        fixture.GitHub.SlowRepository = "refunds";
        var timeoutWatch = Stopwatch.StartNew();
        var timedOut = await fixture.LegacyAsync("CaptureCharge", ["project"], TimeSpan.FromMilliseconds(200));
        Assert.Empty(timedOut.Documents); // Frozen legacy deadline behavior discards already accumulated evidence.
        measurements.Add(new { Name = "scaled-timeout", elapsedMs = timeoutWatch.Elapsed.TotalMilliseconds, returned = timedOut.Documents.Count, timedOut.Status });
        var path = Environment.GetEnvironmentVariable("POOLSENSE_EVAL_OUTPUT");
        if (!string.IsNullOrEmpty(path)) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class RetrievalFixture : IDisposable
{
    public sealed record Example(string Name, string Query, string[] Scope, string[] Expected);
    public static readonly Example[] Examples =
    [
        new("exact-symbol", "CaptureCharge", ["project", "shipping-only"], ["Orders.cs"]),
        new("business-paraphrase", "customer reimbursement", ["project", "shipping-only"], ["Refunds.cs"]),
        new("cross-repository", "CaptureCharge ReserveStock workflow", ["project", "shipping-only"], ["Orders.cs", "Shipping.cs"]),
        new("irrelevant", "orbit telescope signal", ["project", "shipping-only"], []),
        new("scope-specific", "CaptureCharge", ["shipping-only"], [])
    ];
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "poolsense-retrieval-eval", Guid.NewGuid().ToString("N"));
    public FixtureStore Store { get; } = new();
    public FixtureGitHub GitHub { get; } = new();
    public CodeRAGOptions Settings { get; }
    public CodeIndexFiles Files { get; }
    public IHttpContextAccessor Context { get; } = new FixtureContextAccessor
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test")], "fixture")) }
    };
    private RetrievalFixture()
    {
        Settings = new() { Enabled = true, StoragePath = DirectoryPath };
        Files = new(new FixtureEnvironment(DirectoryPath), Microsoft.Extensions.Options.Options.Create(Settings));
        Store.Connections["test"] = new("test", 1, "test", "opaque-fixture-credential");
    }
    public static async Task<RetrievalFixture> CreateAsync()
    {
        var fixture = new RetrievalFixture();
        var sources = new[]
        {
            ("orders", "Orders.cs", "public class OrderSettlement { public void CaptureCharge(string orderId) { WriteLedger(orderId); } private void WriteLedger(string id) {} }"),
            ("refunds", "Refunds.cs", "public class RefundProcessor { /* Returns collected money to the payer after a cancelled order. */ public void ReversePayment(string orderId) {} }"),
            ("shipping", "Shipping.cs", "public class ShipmentPlanner { public void ReserveStock(string orderId) { CommitDispatch(orderId); } private void CommitDispatch(string id) {} }"),
            ("noise", "Menu.cs", "public class NavigationMenu { public void ShowToast(string text) {} public void OpenSettings() {} }")
        };
        foreach (var (name, path, source) in sources)
        {
            var repo = new CodeRepository { Id = Guid.NewGuid().ToString("N"), ProjectId = name == "shipping" ? "shipping-only" : "project",
                FullName = "fixture/" + name, GitHubId = name.GetHashCode(StringComparison.Ordinal), Branch = "main", IndexingUser = "test",
                Generation = Guid.NewGuid().ToString("N"), IndexedCommit = new string('a', 40), Status = "ready", LastCheckedAt = DateTimeOffset.UtcNow, LastIndexedAt = DateTimeOffset.UtcNow };
            Directory.CreateDirectory(fixture.Files.Source(repo));
            await File.WriteAllTextAsync(Path.Combine(fixture.Files.Source(repo), path), source);
            var indexed = await new RepositoryIndexer().IndexAsync(fixture.Files.Source(repo), fixture.Files.Database(repo));
            repo = repo with { FileCount = indexed.Summary.FileCount, SymbolCount = indexed.Summary.SymbolCount };
            fixture.Store.Entries.Add(repo);
        }
        return fixture;
    }
    public async Task<CodeRetrievalResult> LegacyAsync(string query, string[] scope, TimeSpan timeout)
    {
        // Frozen pre-change algorithm: sequential head checks, global lock, equal per-repo budgets,
        // and outer deadline catch that loses completed evidence.
        using var deadline = new CancellationTokenSource(timeout);
        var evidence = new List<CodeEvidence>();
        var stale = false;
        var candidates = Store.Entries.Where(repo => scope.Contains(repo.ProjectId)).ToArray();
        try
        {
            foreach (var repo in candidates)
            {
                try
                {
                    var head = await GitHub.CommitAsync("test", repo, deadline.Token);
                    stale |= head != repo.IndexedCommit || repo.Status is "error" or "access-required";
                    await Files.Gate.WaitAsync(deadline.Token);
                    try
                    {
                        using var db = GraphStore.Open(Files.Database(repo), true);
                        var spans = new Retriever().Retrieve(query, db, Files.Source(repo), Math.Max(1, 12 / candidates.Length),
                            Math.Max(2000, Settings.ContextCharacters / candidates.Length), token: deadline.Token);
                        evidence.AddRange(spans.Select(span => new CodeEvidence(repo.Id, repo.ProjectId, repo.FullName, repo.Branch,
                            repo.IndexedCommit, span.FilePath, span.QualifiedName, span.StartLine, span.EndLine, span.Source,
                            $"https://github.com/{repo.FullName}/blob/{repo.IndexedCommit}/{span.FilePath}#L{span.StartLine}-L{span.EndLine}")));
                    }
                    finally { Files.Gate.Release(); }
                }
                catch (Exception) when (!deadline.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { return CodeRetrievalResult.Skipped; }
        return new(evidence.Count == 0 ? "no-accessible-matches" : stale ? "stale" : "ready", evidence);
    }
    public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }

    internal sealed class FixtureStore : ICodeRepositoryStore
    {
        public List<CodeRepository> Entries { get; } = [];
        public ConcurrentDictionary<string, GitHubConnection> Connections { get; } = new();
        public Task<GitHubConnection?> ConnectionAsync(string user, CancellationToken token) => Task.FromResult(Connections.GetValueOrDefault(user));
        public Task SaveConnectionAsync(GitHubConnection connection, CancellationToken token) { Connections[connection.UserName] = connection; return Task.CompletedTask; }
        public Task DisconnectAsync(string user, CancellationToken token) { Connections.TryRemove(user, out _); return Task.CompletedTask; }
        public Task<IReadOnlyList<CodeRepository>> RepositoriesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CodeRepository>>(Entries.ToArray());
        public Task AddAsync(CodeRepository repository, CancellationToken token) { Entries.Add(repository); return Task.CompletedTask; }
        public Task<bool> UpdateAsync(CodeRepository repository, int revision, CancellationToken token)
        {
            var index = Entries.FindIndex(entry => entry.Id == repository.Id && entry.Revision == revision);
            if (index < 0) return Task.FromResult(false);
            Entries[index] = repository; return Task.FromResult(true);
        }
        public Task<bool> DeleteAsync(string id, int revision, CancellationToken token) => Task.FromResult(Entries.RemoveAll(repo => repo.Id == id && repo.Revision == revision) > 0);
    }
    internal sealed class FixtureGitHub : IGitHubCodeClient
    {
        public bool Denied { get; set; }
        public string SlowRepository { get; set; } = "";
        public string HeadOverride { get; set; } = "";
        public int HeadCalls, AccessCalls;
        public Action? OnAccess { get; set; }
        public int Active, MaxActive;
        public async Task<GitHubRepository> RepositoryAsync(string user, string fullName, CancellationToken token)
        {
            Interlocked.Increment(ref AccessCalls);
            await Delay(fullName, token);
            OnAccess?.Invoke();
            return new(fullName.Split('/')[^1].GetHashCode(StringComparison.Ordinal), fullName, "main");
        }
        public async Task<string> CommitAsync(string user, CodeRepository repository, CancellationToken token)
        {
            Interlocked.Increment(ref HeadCalls);
            await Delay(repository.FullName, token);
            return HeadOverride.Length == 0 ? repository.IndexedCommit : HeadOverride;
        }
        private async Task Delay(string fullName, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active);
            int current;
            do { current = MaxActive; } while (active > current && Interlocked.CompareExchange(ref MaxActive, active, current) != current);
            try
            {
                await Task.Delay(fullName.EndsWith("/" + SlowRepository, StringComparison.Ordinal) ? 1000 : 40, token);
                if (Denied) throw new CodeAccessException();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task<GitHubBranches> BranchesAsync(string user, string name, CancellationToken token) => throw new NotSupportedException();
        public Task DownloadAsync(string user, CodeRepository repository, string commit, string destination, CancellationToken token) => throw new NotSupportedException();
    }
    internal sealed class FixtureEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class FixtureContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
