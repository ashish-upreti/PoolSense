using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using PoolSense.Api.Services.CodeRAG;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class OkfKnowledgeEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompareIdenticalSyntheticSources_CodeOldKnowledgeAndStagedKnowledge()
    {
        var root = Path.Combine(Path.GetTempPath(), "poolsense-okf-evaluation", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            for (var index = 0; index < 12; index++)
                File.WriteAllText(Path.Combine(source, $"A{index:00}Noise.cs"),
                    $"public class Menu{index} {{ public void Touch{index}() {{ }} }}");
            File.WriteAllText(Path.Combine(source, "ZInvoiceRules.cs"), """
                public class InvoiceRules {
                  /// <summary>Net amount after discount.</summary>
                  public decimal CalculateNetAmount(decimal gross, decimal discount) { return gross - discount; }
                }
                """);
            File.WriteAllText(Path.Combine(source, "ZPublishController.cs"), """
                public class PublishController {
                  public void PublishInvoice() { ValidateInvoice(); PersistInvoice(); NotifyConsumer(); }
                  private void ValidateInvoice() { }
                  private void PersistInvoice() { }
                  private void NotifyConsumer() { }
                }
                """);
            File.WriteAllText(Path.Combine(source, "ZRevenue.sql"), """
                SELECT SUM(i.GrossAmount - i.Discount) AS NetRevenue
                FROM Invoices i
                JOIN Customers c ON c.CustomerId = i.CustomerId
                WHERE i.Approved = 1;
                """);
            var database = Path.Combine(root, "index.sqlite3");
            await new RepositoryIndexer().IndexAsync(source, database);
            var repository = new CodeRepository { FullName = "synthetic/invoicing", IndexedCommit = new string('a', 40) };
            using var db = GraphStore.Open(database, true);
            var symbols = GraphStore.Query(db, """
                SELECT * FROM symbols WHERE length(source)>0 AND length(source)<=65536
                ORDER BY CASE WHEN lower(kind) IN ('class','record','interface','enum','struct') THEN 0 ELSE 1 END,
                         file_path,start_line,id LIMIT 80
                """, GraphStore.ReadSymbol);
            var old = Path.Combine(root, "old-okf");
            Directory.CreateDirectory(old);
            var oldGroups = new List<(string Name, Symbol[] Symbols)>
            {
                ("overview", symbols.Take(12).ToArray()), ("architecture", symbols.Take(24).ToArray())
            };
            var validation = symbols.Where(symbol => symbol.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                symbol.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase)).Take(8).ToArray();
            if (validation.Length > 0) oldGroups.Add(("troubleshooting", validation));
            foreach (var (name, selected) in oldGroups)
            {
                var metadata = new
                {
                    type = "Reference", title = name, status = "draft",
                    poolsense_commit = repository.IndexedCommit,
                    sources = selected.Select((symbol, index) => new
                    {
                        id = $"s{index}", resource = OkfBundle.SourceUrl(repository.FullName, repository.IndexedCommit, symbol),
                        poolsense_symbol = symbol.QualifiedName, poolsense_kind = symbol.Kind,
                        poolsense_source_sha256 = Hash(symbol.Source)
                    })
                };
                File.WriteAllText(Path.Combine(old, name + ".md"), "---\n" + JsonSerializer.Serialize(metadata) +
                    "\n---\n# " + name + "\n" + string.Join('\n', selected.Select(symbol => $"- Indexed {symbol.QualifiedName} in {symbol.FilePath}.")));
            }
            File.WriteAllText(Path.Combine(old, "index.md"), "# Source-linked knowledge\n* [Overview](overview.md) - Indexed declarations\n");
            var snapshot = Path.Combine(root, "new-snapshot");
            var prepared = await OkfBundle.PrepareAsync(repository, repository.IndexedCommit, database, source, snapshot);
            Assert.True(prepared.Status == "prepared", string.Join("; ", prepared.Errors));
            var rows = new List<object>();
            foreach (var example in new[]
            {
                (Query: "CalculateNetAmount", Expected: "ZInvoiceRules.cs"),
                (Query: "net amount after discount", Expected: "ZInvoiceRules.cs"),
                (Query: "PublishInvoice workflow", Expected: "ZPublishController.cs"),
                (Query: "NetRevenue calculation", Expected: "ZRevenue.sql"),
            })
            {
                var codeWatch = Stopwatch.StartNew();
                var code = new Retriever().Retrieve(example.Query, db, source, 12, 24000);
                codeWatch.Stop();
                var oldWatch = Stopwatch.StartNew();
                var oldDocuments = OkfLocalBundle.ReadExisting(old);
                var terms = CodeCandidateSelection.Terms(example.Query);
                var legacy = oldDocuments.Documents.Where(document => document.Kind == OkfDocumentKind.Concept)
                    .OrderByDescending(document => terms.Count(term => (document.Title + document.Body).Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .Where(document => terms.Any(term => (document.Title + document.Body).Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .SelectMany(document => document.Sources).Select(reference => symbols.First(symbol =>
                        symbol.QualifiedName == reference.Metadata["poolsense_symbol"] as string &&
                        symbol.Kind == reference.Metadata["poolsense_kind"] as string))
                    .DistinctBy(symbol => symbol.Id).Take(12).ToArray();
                oldWatch.Stop();
                var coldPath = Path.Combine(root, "cold-" + rows.Count);
                CopyBundle(prepared.BundlePath!, coldPath);
                var coldWatch = Stopwatch.StartNew();
                var cold = OkfBundle.SearchBundle(repository, repository.IndexedCommit, database, source, coldPath, example.Query);
                coldWatch.Stop();
                var warmTimes = new List<double>();
                OkfSearchResult warm = cold;
                for (var sample = 0; sample < 5; sample++)
                {
                    var watch = Stopwatch.StartNew();
                    warm = OkfBundle.SearchBundle(repository, repository.IndexedCommit, database, source, coldPath, example.Query);
                    watch.Stop();
                    warmTimes.Add(watch.Elapsed.TotalMilliseconds);
                }
                var oldRecall = legacy.Any(symbol => symbol.FilePath == example.Expected) ? 1 : 0;
                var newRecall = warm.References.Any(reference => reference.Symbol.FilePath == example.Expected) ? 1 : 0;
                Assert.True(newRecall >= oldRecall, $"Knowledge recall regressed for {example.Query}: {string.Join("; ", warm.Errors)}");
                Assert.Equal(1, newRecall);
                var row = new
                {
                    example.Query, expectedSource = example.Expected, k = 12,
                    code = new { recallAtK = code.Any(symbol => symbol.FilePath == example.Expected) ? 1 : 0, firstRank = Rank(code.Select(symbol => symbol.FilePath), example.Expected), elapsedMs = codeWatch.Elapsed.TotalMilliseconds },
                    oldKnowledge = new { recallAtK = oldRecall, firstRank = Rank(legacy.Select(symbol => symbol.FilePath), example.Expected), elapsedMs = oldWatch.Elapsed.TotalMilliseconds,
                        bytesLoaded = Directory.GetFiles(old, "*.md").Sum(file => new FileInfo(file).Length) },
                    stagedKnowledge = new { recallAtK = newRecall, firstRank = Rank(warm.References.Select(reference => reference.Symbol.FilePath), example.Expected),
                        coldMs = coldWatch.Elapsed.TotalMilliseconds, warmMedianMs = warmTimes.Order().ElementAt(2), coldBytesLoaded = cold.Metrics!.CatalogBytesRead + cold.Metrics.ContentBytesRead,
                        warmBytesLoaded = warm.Metrics!.CatalogBytesRead + warm.Metrics.ContentBytesRead, warm.Metrics.ContextCharacters,
                        warm.Metrics.SelectedConcepts, warm.Metrics.NeighborConcepts }
                };
                rows.Add(row);
                output.WriteLine(JsonSerializer.Serialize(row));
            }
            var routing = new List<object>();
            using (var fixture = await RetrievalFixture.CreateAsync())
            {
                foreach (var entry in fixture.Store.Entries)
                    await OkfBundle.PrepareAsync(entry, entry.IndexedCommit, fixture.Files.Database(entry), fixture.Files.Source(entry), fixture.Files.Folder(entry));
                foreach (var example in RetrievalFixture.Examples)
                {
                    var results = new List<CodeRetrievalResult>();
                    foreach (var enabled in new[] { false, true })
                    {
                        fixture.Settings.RepositoryRoutingEnabled = enabled;
                        var accessBefore = fixture.GitHub.AccessCalls;
                        var service = new CodeKnowledgeService(fixture.Store, null!, fixture.GitHub, fixture.Access, fixture.Files, fixture.Context,
                            Microsoft.Extensions.Options.Options.Create(fixture.Settings),
                            knowledgeOptions: Microsoft.Extensions.Options.Options.Create(new OkfOptions()));
                        var watch = Stopwatch.StartNew();
                        var result = await service.RetrieveAsync(example.Query, example.Scope, default);
                        watch.Stop();
                        results.Add(result);
                        routing.Add(new { example.Name, enabled, elapsedMs = watch.Elapsed.TotalMilliseconds,
                            recallAt12 = example.Expected.Length == 0 ? (double?)null :
                                (double)example.Expected.Count(path => result.Documents.Any(document => document.FilePath == path)) / example.Expected.Length,
                            accessChecks = fixture.GitHub.AccessCalls - accessBefore, result.Coverage.ExpandedFallback, result.Status });
                    }
                    var expectedWithout = example.Expected.Count(path => results[0].Documents.Any(document => document.FilePath == path));
                    var expectedWith = example.Expected.Count(path => results[1].Documents.Any(document => document.FilePath == path));
                    Assert.True(expectedWith >= expectedWithout, $"Routing regressed recall for {example.Name}");
                }
                Assert.Equal(0, fixture.GitHub.HeadCalls);
                fixture.GitHub.Denied = true;
                var denied = await new CodeKnowledgeService(fixture.Store, null!, fixture.GitHub, fixture.Access, fixture.Files, fixture.Context,
                    Microsoft.Extensions.Options.Options.Create(fixture.Settings),
                    knowledgeOptions: Microsoft.Extensions.Options.Options.Create(new OkfOptions())).RetrieveAsync("CaptureCharge", ["project"], default);
                Assert.Empty(denied.Documents);
            }
            var report = new { corpus = "Synthetic invoicing C#/SQL; identical indexed code for all candidates. No NYRA calls.", cases = rows, routing,
                routingGate = "OFF; a small synthetic benchmark is insufficient to enable production routing." };
            var destination = Environment.GetEnvironmentVariable("POOLSENSE_OKF_EVAL_OUTPUT");
            if (!string.IsNullOrEmpty(destination))
            {
                await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                var sample = Environment.GetEnvironmentVariable("POOLSENSE_OKF_SAMPLE_OUTPUT");
                if (!string.IsNullOrEmpty(sample)) CopyBundle(prepared.BundlePath!, sample, overwrite: true);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OptInLocalSnapshotUpgrade_IsShadowOnlyAndNeverDownloadsOrCallsLlm()
    {
        var storage = Environment.GetEnvironmentVariable("POOLSENSE_OKF_UPGRADE_ROOT");
        if (string.IsNullOrEmpty(storage)) return;
        var candidate = Directory.GetFiles(storage, "index.sqlite3", SearchOption.AllDirectories)
            .Where(file => File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "okf", "overview.md")))
            .OrderBy(file => new FileInfo(file).Length).First();
        var previousFolder = Path.GetDirectoryName(candidate)!;
        var old = OkfParser.Parse("overview.md", await File.ReadAllTextAsync(Path.Combine(previousFolder, "okf", "overview.md")));
        var repository = new CodeRepository
        {
            Id = Path.GetFileName(Path.GetDirectoryName(previousFolder))!, Generation = Guid.NewGuid().ToString("N"),
            FullName = (string)old.Metadata["poolsense_repository"]!, IndexedCommit = (string)old.Metadata["poolsense_commit"]!
        };
        var temporary = Path.Combine(Path.GetDirectoryName(storage)!, "okf-upgrade-check", Guid.NewGuid().ToString("N"));
        try
        {
            using var beforeStream = File.OpenRead(candidate);
            var beforeHash = Convert.ToHexString(SHA256.HashData(beforeStream));
            var files = new CodeIndexFiles(new RetrievalFixture.FixtureEnvironment(temporary),
                Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions { StoragePath = temporary }));
            var provider = new RetrievalFixture.FixtureGitHub();
            var llmCalls = 0;
            var builder = new CodeSnapshotBuilder(provider, files,
                knowledge: Microsoft.Extensions.Options.Options.Create(new OkfOptions()),
                llmFactory: () => { llmCalls++; throw new InvalidOperationException("No paid generation allowed"); });
            using var previousDatabase = GraphStore.Open(candidate, true);
            var before = Assert.Single(GraphStore.Summaries(previousDatabase));
            var summary = await builder.BuildAsync(repository, repository.IndexedCommit, default, previousFolder);
            Assert.True(files.Ready(repository));
            Assert.Equal(before.IndexedAt, summary.IndexedAt);
            Assert.Equal(before.SymbolCount, summary.SymbolCount);
            Assert.Equal(0, provider.HeadCalls);
            Assert.Equal(0, llmCalls);
            using var afterStream = File.OpenRead(candidate);
            Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(afterStream)));
            var read = OkfLocalBundle.ReadExisting(Path.Combine(files.Folder(repository), "okf"));
            Assert.Equal("ready", read.Status);
            var report = new { shadowUpgradeVerified = true, activeGenerationChanged = false, downloaded = false, llmCalls,
                reusedGraph = true, summary.FileCount, summary.SymbolCount,
                conceptCount = read.Documents.Count(document => document.Kind == OkfDocumentKind.Concept),
                types = read.Documents.Where(document => document.Kind == OkfDocumentKind.Concept).Select(document => document.Type).Distinct().Order().ToArray() };
            output.WriteLine(JsonSerializer.Serialize(report));
            var destination = Environment.GetEnvironmentVariable("POOLSENSE_OKF_UPGRADE_OUTPUT");
            if (!string.IsNullOrEmpty(destination)) await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static int? Rank(IEnumerable<string> paths, string expected)
    {
        var rank = paths.ToList().FindIndex(path => path == expected);
        return rank < 0 ? null : rank + 1;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void CopyBundle(string source, string destination, bool overwrite = false)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}
