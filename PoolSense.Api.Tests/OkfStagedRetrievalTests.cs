using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class OkfStagedRetrievalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "poolsense-okf-staged", Guid.NewGuid().ToString("N"));
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly CodeRepository repository = new() { FullName = "fixture/pricing", IndexedCommit = Commit };
    private string Source => Path.Combine(root, "source");
    private string Database => Path.Combine(root, "index.sqlite3");
    private string Bundle => Path.Combine(root, "okf");
    private readonly Dictionary<string, Symbol> symbols;

    public OkfStagedRetrievalTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "Pricing.cs"), """
            public class Pricing {
              public decimal GrossAmount(decimal units, decimal price) { return units * price; }
              public decimal NetSales(decimal gross, decimal discount) { return gross - discount; }
              public bool CanPublish(bool approved) { return approved; }
            }
            """);
        new RepositoryIndexer().IndexAsync(Source, Database).GetAwaiter().GetResult();
        using var db = GraphStore.Open(Database, true);
        symbols = GraphStore.Query(db, "SELECT * FROM symbols WHERE name IN ('GrossAmount','NetSales','CanPublish')", GraphStore.ReadSymbol)
            .ToDictionary(symbol => symbol.Name);
        Directory.CreateDirectory(Bundle);
        File.WriteAllText(Path.Combine(Bundle, "index.md"), "---\nokf_version: '0.2'\n---\n# Concept map\n");
    }

    [Fact]
    public void MetadataAlias_SelectsOnlyRelevantConceptAndCachesCatalogWithoutKeepingWholeBodies()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nSource-backed total.[^s]", aliases: ["customer billing amount"]);
        Write("noise", "Publication", "CanPublish", "# Publication\n\nPublication authorization.[^s]\n" + new string('x', 5000));
        var options = new OkfOptions { MaxSelectedConcepts = 1 };
        var first = Search("customer billing amount", options);
        var second = Search("customer billing amount", options);
        Assert.Equal("GrossAmount", Assert.Single(first.References).Symbol.Name);
        Assert.Equal(["invoice"], first.Metrics!.SelectedConcepts);
        Assert.False(first.Metrics.CatalogCacheHit);
        Assert.True(second.Metrics!.CatalogCacheHit);
        Assert.Equal(0, second.Metrics.CatalogBytesRead);
        Assert.True(second.Metrics.ContentBytesRead < first.Metrics.CatalogBytesRead);
        Assert.Equal(first.References, second.References);
    }

    [Fact]
    public void ExplicitCatalogWarmupPopulatesMetadataCacheWithoutAQuery()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nSource-backed total.[^s]");
        var sourceModified = File.GetLastWriteTimeUtc(Path.Combine(Bundle, "invoice.md"));

        var first = OkfBundle.WarmCatalog(Bundle, Commit);
        var second = OkfBundle.WarmCatalog(Bundle, Commit);
        var search = Search("invoice total");

        Assert.Equal("ready", first.Status);
        Assert.Equal(1, first.Concepts);
        Assert.False(first.CacheHit);
        Assert.True(first.CatalogBytesRead > 0);
        Assert.True(second.CacheHit);
        Assert.Equal(0, second.CatalogBytesRead);
        Assert.True(search.Metrics!.CatalogCacheHit);
        Assert.Equal("GrossAmount", Assert.Single(search.References).Symbol.Name);
        Assert.Equal(sourceModified, File.GetLastWriteTimeUtc(Path.Combine(Bundle, "invoice.md")));
    }

    [Fact]
    public void ExplicitCatalogWarmupMissingBundleIsLazyFallback()
    {
        var missing = OkfBundle.WarmCatalog(Path.Combine(root, "missing"), Commit);
        Assert.Equal("missing", missing.Status);
        Assert.Equal(0, missing.Concepts);
    }

    [Fact]
    public void ExplicitCatalogWarmupHonorsEightCatalogEntryCap()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nSource-backed total.[^s]");
        var bundles = Enumerable.Range(0, 9).Select(index =>
        {
            var destination = Path.Combine(root, $"bundle-{index}");
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(Bundle))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            return destination;
        }).ToArray();

        foreach (var bundle in bundles)
            Assert.Equal("ready", OkfBundle.WarmCatalog(bundle, Commit).Status);

        Assert.False(OkfBundle.WarmCatalog(bundles[0], Commit).CacheHit);
    }

    [Fact]
    public void NeededInlineNeighbor_IsFollowedWithCycleAndSiblingCaps()
    {
        Write("invoice", "Invoice total", null, "# Invoice total\n\nInvoice uses the [formula](net.md).\n\n## Unrelated\n\n[Do not load](noise.md).");
        Write("net", "Calculation", "NetSales", "# Computation\n\n`gross - discount`.[^s] See [origin](invoice.md).");
        Write("noise", "Unrelated publication", "CanPublish", "# Publication\n\nPublication authorization.[^s]");
        var result = Search("invoice total", new() { MaxSelectedConcepts = 1, MaxNeighborConcepts = 1, MaxNeighborHops = 2 });
        Assert.Equal("NetSales", Assert.Single(result.References).Symbol.Name);
        Assert.Equal(["invoice"], result.Metrics!.SelectedConcepts);
        Assert.Equal(["net"], result.Metrics.NeighborConcepts);
        Assert.DoesNotContain("noise", result.Metrics.SelectedConcepts.Concat(result.Metrics.NeighborConcepts));
    }

    [Fact]
    public void CurrentWinsRelevanceTieButHistoricalAndUnverifiedRemainDiscoverable()
    {
        Write("a-old", "Invoice total", "GrossAmount", "# Invoice total\n\nTotal.[^s]",
            status: "deprecated", stale: "2000-01-01T00:00:00Z");
        Write("z-current", "Invoice total", "NetSales", "# Invoice total\n\nTotal.[^s]");
        var current = Search("invoice total", new() { MaxSelectedConcepts = 1 });
        Assert.Equal("z-current", Assert.Single(current.References).ConceptId);
        Assert.Equal(OkfTrust.Unverified, current.References[0].Trust);
        File.Delete(Path.Combine(Bundle, "z-current.md"));
        var historical = Search("invoice total", new() { MaxSelectedConcepts = 1 });
        Assert.Equal("a-old", Assert.Single(historical.References).ConceptId);
        Assert.Contains(historical.Errors, error => error.Contains("stale or deprecated"));
    }

    [Fact]
    public void ModifiedCatalogAndSourcesInvalidateWarmReadWithoutGeneratingAnything()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nTotal.[^s]");
        Assert.Equal("GrossAmount", Assert.Single(Search("invoice total").References).Symbol.Name);
        Write("invoice", "Invoice total", "NetSales", "# Invoice total\n\nUpdated total.[^s]");
        var changed = Search("invoice total");
        Assert.False(changed.Metrics!.CatalogCacheHit);
        Assert.Equal("NetSales", Assert.Single(changed.References).Symbol.Name);
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, "UPDATE symbols SET source='invalid content' WHERE id=@p0", symbols["NetSales"].Id);
        Assert.Empty(Search("invoice total").References);
    }

    [Fact]
    public async Task ParallelWarmCatalogReadsAreBoundedAndDoNotModifyBundle()
    {
        Write("invoice", "Invoice total", "NetSales", "# Invoice total\n\nTotal.[^s]");
        var before = File.GetLastWriteTimeUtc(Path.Combine(Bundle, "invoice.md"));
        Search("invoice total");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            Search("invoice total", new() { MaxSelectedConcepts = 1, MaxReadCharacters = 256 }))));
        Assert.All(results, result =>
        {
            Assert.Single(result.References);
            Assert.True(result.Metrics!.ContextCharacters <= 256);
            Assert.Single(result.Metrics.SelectedConcepts);
        });
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(Bundle, "invoice.md")));
    }

    [Fact]
    public void CancelledColdCallerCannotPoisonTheNextAuthorizedReader()
    {
        Write("invoice", "Invoice total", "NetSales", "# Invoice total\n\nTotal.[^s]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OkfBundle.SearchBundle(repository, Commit, Database, Source,
            Bundle, "invoice total", cancellationToken: cancellation.Token));
        Assert.Equal("NetSales", Assert.Single(Search("invoice total").References).Symbol.Name);
    }

    [Fact]
    public void ForeignRepositorySourceAndOversizedBundleCannotBecomeSourceEvidence()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nTotal.[^s]");
        var file = Path.Combine(Bundle, "invoice.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("fixture/pricing", "fixture/private"));
        Assert.Empty(Search("invoice total").References);
        Write("large", "Unrelated content", null, new string('x', 2048));
        var limited = Search("invoice total", new() { MaxBundleBytes = 1024 });
        Assert.Empty(limited.References);
        Assert.Contains(limited.Errors, error => error.Contains("byte limit"));
    }

    [Fact]
    public void MetadataAliasCanPromoteItsValidatedSourceButNeverOverridesExactIdentifier()
    {
        var alias = new CodeSearchCandidate(repository, symbols["NetSales"], KnowledgeReference: true) { KnowledgeRelevance = 1 };
        var noise = new CodeSearchCandidate(repository, symbols["CanPublish"], LexicalRank: 1);
        Assert.True(CodeCandidateSelection.Relevance("invoice customer amount", alias) >
            CodeCandidateSelection.Relevance("invoice customer amount", noise));
        var exact = new CodeSearchCandidate(repository, symbols["GrossAmount"], LexicalRank: 1);
        Assert.True(CodeCandidateSelection.Relevance("GrossAmount", exact) > CodeCandidateSelection.Relevance("GrossAmount", alias));
    }

    [Fact]
    public void AmbiguousKeyedFootnotesDoNotSilentlyChooseASource()
    {
        Write("invoice", "Invoice total", "GrossAmount", "# Invoice total\n\nTotal.[^s]");
        var path = Path.Combine(Bundle, "invoice.md");
        var parsed = OkfParser.Parse("invoice.md", File.ReadAllText(path));
        var metadata = parsed.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value);
        metadata["sources"] = new[] { parsed.Sources[0].Metadata, parsed.Sources[0].Metadata };
        File.WriteAllText(path, "---\n" + JsonSerializer.Serialize(metadata) + "\n---\n" + parsed.Body);
        var result = Search("invoice total");
        Assert.Empty(result.References);
        Assert.Contains(result.Errors, error => error.Contains("Ambiguous source IDs"));
    }

    private OkfSearchResult Search(string query, OkfOptions? options = null) =>
        OkfBundle.SearchBundle(repository, Commit, Database, Source, Bundle, query, options);

    private void Write(string id, string title, string? symbolName, string body, string[]? aliases = null,
        string status = "draft", string? stale = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = "Business Rule", ["title"] = title, ["description"] = title, ["status"] = status,
            ["poolsense_commit"] = Commit, ["aliases"] = aliases ?? [],
        };
        if (stale is not null) metadata["stale_after"] = stale;
        if (symbolName is not null)
        {
            var symbol = symbols[symbolName];
            metadata["sources"] = new[] { new Dictionary<string, object?>
            {
                ["id"] = "s", ["resource"] = OkfBundle.SourceUrl(repository.FullName, Commit, symbol),
                ["poolsense_symbol"] = symbol.QualifiedName, ["poolsense_kind"] = symbol.Kind,
                ["poolsense_source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(symbol.Source)))
            } };
        }
        File.WriteAllText(Path.Combine(Bundle, id + ".md"), "---\n" + JsonSerializer.Serialize(metadata) + "\n---\n" + body);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
