using CodeRAG.Core.Storage;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class OkfBundleTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "okf-tests", Guid.NewGuid().ToString("N"));
    private readonly CodeRepository repository = new() { FullName = "owner/repository", IndexedCommit = Commit };
    private string SourceRoot => Path.Combine(root, "source");
    private string Snapshot => Path.Combine(root, "snapshot");
    private string Database => Path.Combine(root, "index.db");

    public OkfBundleTests()
    {
        Directory.CreateDirectory(Path.Combine(SourceRoot, "Models"));
        File.WriteAllText(Path.Combine(SourceRoot, "Models", "Widget.cs"), "namespace Demo;\npublic class Widget {}\n");
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, """
            INSERT INTO repositories VALUES(@p0,'2026-01-01',1,1,0,@p1,@p2)
            """, GraphStore.RootKey(SourceRoot), GraphStore.Version, GraphStore.Parser);
        GraphStore.Execute(db, """
            INSERT INTO symbols VALUES('widget',@p0,'Models/Widget.cs','class','Widget','Demo.Widget',2,2,NULL,
                'public class Widget {}','')
            """, GraphStore.RootKey(SourceRoot));
    }

    [Fact]
    public async Task PreparationCreatesOnlySourceLinkedSnapshotArtifactsAndSearchReturnsIndexedSpans()
    {
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot);
        Assert.True(result.Status == "prepared", $"{result.Status}: {string.Join("; ", result.Errors)}");
        Assert.False(Directory.Exists(Path.Combine(SourceRoot, "okf")));
        Assert.True(File.Exists(Path.Combine(Snapshot, "okf", "index.md")));
        Assert.True(File.Exists(Path.Combine(Snapshot, "okf", "domain.md")));
        Assert.False(File.Exists(Path.Combine(Snapshot, "okf", "troubleshooting.md")));
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, expectedCommit: Commit);
        Assert.Equal("ready", read.Status);
        var concepts = read.Documents.Where(d => d.Kind == OkfDocumentKind.Concept).ToList();
        Assert.Equal(2, concepts.Count);
        Assert.All(concepts, document =>
        {
            Assert.Equal("draft", document.Status);
            Assert.Equal(OkfTrust.Unverified, document.Trust);
            Assert.False(document.Metadata.ContainsKey("verified"));
            var source = Assert.Single(document.Sources);
            Assert.Equal($"https://github.com/owner/repository/blob/{Commit}/Models/Widget.cs#L2-L2", source.Resource);
            Assert.Contains($"[^{source.Id}]", document.Body);
        });
        var search = OkfBundle.SearchExisting(repository, Commit, Database, SourceRoot, Snapshot, "Widget");
        var reference = Assert.Single(search.References);
        Assert.Equal("widget", reference.Symbol.Id);
        Assert.Equal(2, reference.Symbol.StartLine);
        Assert.Equal("public class Widget {}", reference.Symbol.Source);
        Assert.DoesNotContain("Indexed", reference.Symbol.Source);
        var before = File.ReadAllText(Path.Combine(result.BundlePath!, "overview.md"));
        var again = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot);
        Assert.Equal("existing", again.Status);
        Assert.Equal(before, File.ReadAllText(Path.Combine(result.BundlePath!, "overview.md")));
    }

    [Fact]
    public void MissingArtifactReturnsEmptyWithoutCreatingOrOpeningDatabase()
    {
        var result = OkfBundle.SearchExisting(repository, Commit, "does-not-exist.db", SourceRoot, Snapshot, "Widget");
        Assert.Equal("missing", result.Status);
        Assert.Empty(result.References);
        Assert.False(Directory.Exists(Snapshot));
    }

    [Fact]
    public async Task SnapshotMayContainSiblingSourceAndOkfDirectories()
    {
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, root);
        Assert.Equal("prepared", result.Status);
        Assert.True(File.Exists(Path.Combine(root, "okf", "overview.md")));
        Assert.False(Directory.Exists(Path.Combine(SourceRoot, "okf")));
    }

    [Fact]
    public async Task ChangedCommitInvalidatesKnowledgeWithoutRegeneration()
    {
        await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot);
        var result = OkfBundle.SearchExisting(repository, new string('a', 40), Database, SourceRoot, Snapshot, "Widget");
        Assert.Equal("stale", result.Status);
        Assert.Empty(result.References);
    }

    [Fact]
    public async Task SourceLabelsRemainStableWhenSnapshotSpecificDatabaseIdsChange()
    {
        await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot);
        var first = OkfLocalBundle.ReadExisting(Path.Combine(Snapshot, "okf")).Documents
            .Single(d => d.Id == "overview").Sources.Single().Id;
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET id='other-snapshot-symbol-id' WHERE id='widget'");
        var otherSnapshot = Path.Combine(root, "other-snapshot");
        await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, otherSnapshot);
        var second = OkfLocalBundle.ReadExisting(Path.Combine(otherSnapshot, "okf")).Documents
            .Single(d => d.Id == "overview").Sources.Single().Id;
        Assert.Equal(first, second);
        var reused = OkfBundle.SearchExisting(repository, Commit, Database, SourceRoot, Snapshot, "Widget");
        Assert.Equal("other-snapshot-symbol-id", Assert.Single(reused.References).Symbol.Id);
    }

    [Fact]
    public async Task EnrichmentIsDisabledByDefaultAndChangedIndexContentInvalidatesSourceHash()
    {
        var llm = new CountingLlm();
        await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot, llm: llm);
        Assert.Equal(0, llm.Calls);
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source='public class Changed {}' WHERE id='widget'");
        var search = OkfBundle.SearchExisting(repository, Commit, Database, SourceRoot, Snapshot, "Widget");
        Assert.Empty(search.References);
        Assert.Contains(search.Errors, e => e.Contains("content hash differs"));
    }

    [Fact]
    public async Task PreparationNeverWritesIntoSourceTreeAndReportsGeneratorErrors()
    {
        var unsafeResult = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Path.Combine(SourceRoot, "bad"));
        Assert.Equal("failed", unsafeResult.Status);
        Assert.False(Directory.Exists(Path.Combine(SourceRoot, "bad")));
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot,
            new() { EnableLlmEnrichment = true }, new ThrowingLlm());
        Assert.Equal("prepared-with-errors", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("LLM enrichment failed"));
        Assert.True(File.Exists(Path.Combine(Snapshot, "okf", "overview.md")));
    }

    [Theory]
    [InlineData("{\"claims\":[{\"text\":\"Invented behavior\",\"sourceIds\":[\"invented\"]}]}")]
    [InlineData("{\"claims\":[{\"text\":\"https://unsafe.invalid\",\"sourceIds\":[\"invented\"]}]}")]
    [InlineData("not json")]
    public async Task LlmEnrichmentRejectsInventedSourcesUnsafeLinksAndMalformedResponses(string response)
    {
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot,
            new() { EnableLlmEnrichment = true }, new ResponseLlm(response));
        Assert.Equal("prepared-with-errors", result.Status);
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain("Invented behavior", File.ReadAllText(Path.Combine(Snapshot, "okf", "overview.md")));
    }

    [Fact]
    public async Task BoundedSourceBoundEnrichmentRemainsDraftAndUnverified()
    {
        var llm = new SourceBoundLlm();
        var options = new OkfOptions { EnableLlmEnrichment = true, MaxLlmInputCharacters = 1000 };
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot, options, llm);
        Assert.True(result.Status == "prepared", $"{result.Status}: {string.Join("; ", result.Errors)}");
        Assert.InRange(llm.InputLength, 1, options.MaxLlmInputCharacters);
        var document = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents.Single(d => d.Id == "overview");
        Assert.Contains("The source declares Widget.", document.Body);
        Assert.Equal(OkfTrust.Unverified, document.Trust);
        Assert.Equal("draft", document.Status);
        Assert.True(document.Metadata.ContainsKey("poolsense_llm_generated"));
        Assert.Equal(1, llm.Calls);
        var existing = await OkfBundle.PrepareAsync(repository, Commit, Database, SourceRoot, Snapshot, options, llm);
        Assert.Equal("existing", existing.Status);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task PublicationRetriesOnlyTransientWindowsErrorsWithoutRebuildingTheStagingArtifact()
    {
        if (!OperatingSystem.IsWindows()) return;
        var staging = Path.Combine(Snapshot, "staging");
        var target = Path.Combine(Snapshot, "okf");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "index.md"), "# Completed");
        var attempts = 0;
        Action<string, string> move = (from, to) =>
        {
            attempts++;
            Assert.Equal(staging, from);
            Assert.Equal(target, to);
            Assert.Equal("# Completed", File.ReadAllText(Path.Combine(from, "index.md")));
            if (attempts <= 2) throw new IOException("Sharing violation", unchecked((int)0x80070020));
        };
        await PublishAsync(staging, target, CancellationToken.None, move);
        Assert.Equal(3, attempts);
        Assert.Equal("# Completed", File.ReadAllText(Path.Combine(staging, "index.md")));
    }

    [Fact]
    public async Task PublicationRetriesAreBoundedAndNeverOverwriteAnExistingTarget()
    {
        var staging = Path.Combine(Snapshot, "staging");
        var target = Path.Combine(Snapshot, "okf");
        Directory.CreateDirectory(staging);
        var attempts = 0;
        Action<string, string> denied = (_, _) =>
        {
            attempts++;
            throw new IOException("Access denied", unchecked((int)0x80070005));
        };
        await Assert.ThrowsAsync<IOException>(() => PublishAsync(staging, target, CancellationToken.None, denied));
        Assert.Equal(OperatingSystem.IsWindows() ? 5 : 1, attempts);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "index.md"), "# Existing");
        attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => PublishAsync(staging, target, CancellationToken.None, denied));
        Assert.Equal(1, attempts);
        Assert.Equal("# Existing", File.ReadAllText(Path.Combine(target, "index.md")));
    }

    [Fact]
    public async Task PublicationCancellationAndPermanentErrorsAreNotRetried()
    {
        var staging = Path.Combine(Snapshot, "staging");
        Directory.CreateDirectory(staging);
        var attempts = 0;
        Action<string, string> invalid = (_, _) =>
        {
            attempts++;
            throw new IOException("Invalid parameter", unchecked((int)0x80070057));
        };
        await Assert.ThrowsAsync<IOException>(() => PublishAsync(staging, Path.Combine(Snapshot, "okf"), CancellationToken.None, invalid));
        Assert.Equal(1, attempts);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublishAsync(staging, Path.Combine(Snapshot, "okf"), cancellation.Token, invalid));
        Assert.Equal(0, attempts);
    }

    private static Task PublishAsync(string staging, string target, CancellationToken cancellationToken,
        Action<string, string> move) =>
        (Task)typeof(OkfBundle).GetMethod("PublishAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [staging, target, cancellationToken, move])!;

    [Fact]
    public void LocalResolverAllowsParentWithinRootButNeverEscapeOrExternalLoads()
    {
        Directory.CreateDirectory(Snapshot);
        Assert.Equal(Path.Combine(Snapshot, "other.md"), OkfLocalBundle.Resolve(Snapshot, "sub/a.md", "../other.md"));
        Assert.Equal(Path.Combine(Snapshot, "other.md"), OkfLocalBundle.Resolve(Snapshot, "sub/a.md", "/other.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", "../outside.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", "%2e%2e/outside.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", "https://example.invalid/a.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", @"C:\outside.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", "//server/share/a.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", ".. /outside.md"));
        Assert.Null(OkfLocalBundle.Resolve(Snapshot, "a.md", "CON.md"));
    }

    [Fact]
    public void ImportedHumanBundleToleratesBrokenLinksAndResolvesRealPinnedSources()
    {
        Directory.CreateDirectory(Snapshot);
        File.WriteAllText(Path.Combine(Snapshot, "human.md"), $$"""
            ---
            type: Custom Human Reference
            verified: {by: "human:reviewer", at: "2026-01-01T00:00:00Z"}
            sources:
              - id: widget-source
                resource: https://github.com/owner/repository/blob/{{Commit}}/Models/Widget.cs#L2
            ---
            Widget source.[^widget-source] [Future](missing.md)
            [^widget-source]: Source declaration
            """);
        var read = OkfLocalBundle.ReadExisting(Snapshot);
        Assert.Equal("ready", read.Status);
        Assert.Single(OkfLocalBundle.Traverse(Snapshot, "human.md"));
        var search = OkfBundle.SearchBundle(repository, Commit, Database, SourceRoot, Snapshot, "Widget");
        Assert.Equal(OkfTrust.HumanReviewed, Assert.Single(search.References).Trust);
    }

    [Fact]
    public void MalformedDocumentsAndTraversalBudgetsAreExplicit()
    {
        Directory.CreateDirectory(Snapshot);
        File.WriteAllText(Path.Combine(Snapshot, "bad.md"), "---\ntype: A\nx: &a [*a]\n---\n");
        Assert.Equal("partial", OkfLocalBundle.ReadExisting(Snapshot).Status);
        Assert.Empty(OkfLocalBundle.Traverse(Snapshot, "bad.md"));
        File.WriteAllText(Path.Combine(Snapshot, "one.md"), "---\ntype: A\n---\n");
        File.WriteAllText(Path.Combine(Snapshot, "two.md"), "---\ntype: A\n---\n");
        Assert.Equal("limited", OkfLocalBundle.ReadExisting(Snapshot, new() { MaxTraversalEntries = 1 }).Status);
    }

    [Fact]
    public void SymbolicLinksAreNotFollowedWhenSupportedByTheHost()
    {
        Directory.CreateDirectory(Snapshot);
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "secret.md"), "---\ntype: Secret\n---\n");
        var link = Path.Combine(Snapshot, "linked");
        try { Directory.CreateSymbolicLink(link, external); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }
        try
        {
            Assert.Null(OkfLocalBundle.Resolve(Snapshot, "index.md", "linked/secret.md"));
            var read = OkfLocalBundle.ReadExisting(Snapshot);
            Assert.Empty(read.Documents);
            Assert.Contains(read.Errors, e => e.Contains("symbolic link"));
        }
        finally { Directory.Delete(link); }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class ThrowingLlm : ILLMService
    {
        public Task<string> GetResponseAsync(string prompt) => throw new InvalidOperationException("generation unavailable");
    }

    private sealed class ResponseLlm(string response) : ILLMService
    {
        public Task<string> GetResponseAsync(string prompt) => Task.FromResult(response);
    }

    private sealed class CountingLlm : ILLMService
    {
        public int Calls { get; private set; }
        public Task<string> GetResponseAsync(string prompt)
        {
            Calls++;
            return Task.FromResult("{\"claims\":[]}");
        }
    }

    private sealed class SourceBoundLlm : ILLMService
    {
        public int InputLength { get; private set; }
        public int Calls { get; private set; }
        public Task<string> GetResponseAsync(string prompt)
        {
            Calls++;
            InputLength = prompt.Length;
            var sourceId = Regex.Match(prompt, @"SOURCE (src-[a-f0-9]+)").Groups[1].Value;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                claims = new[] { new { text = "The source declares Widget.", sourceIds = new[] { sourceId } } }
            }));
        }
    }
}
