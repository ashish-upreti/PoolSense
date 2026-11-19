using System.Diagnostics;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class IndexingPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecordFullAndOneFileRefresh()
    {
        var folder = Path.Combine(Path.GetTempPath(), "poolsense-index-performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var file = 0; file < 150; file++)
                await File.WriteAllTextAsync(Path.Combine(folder, $"Service{file}.cs"), Source(file));
            var database = Path.Combine(folder, "index.sqlite3");
            var indexer = new RepositoryIndexer();
            var watch = Stopwatch.StartNew();
            var full = await indexer.IndexAsync(folder, database, true);
            var fullMs = watch.Elapsed.TotalMilliseconds;
            await File.WriteAllTextAsync(Path.Combine(folder, "Service0.cs"), Source(0).Replace("Run0()", "Start0()", StringComparison.Ordinal));
            watch.Restart();
            var changed = await indexer.IndexAsync(folder, database, true);
            var changedMs = watch.Elapsed.TotalMilliseconds;
            using var db = GraphStore.Open(database, true);
            Assert.Equal(150, changed.Summary.FileCount);
            Assert.Equal(full.Summary.EdgeCount, changed.Summary.EdgeCount);
            Assert.Equal(1, changed.ParsedFiles);
            Assert.Equal(149, changed.ReusedFiles);
            Assert.Equal(1L, Convert.ToInt64(GraphStore.Scalar(db, "SELECT count(*) FROM symbols WHERE name='Start0'")));
            output.WriteLine($"index-baseline fullMs={fullMs:F2} oneFileMs={changedMs:F2} symbols={changed.Summary.SymbolCount} edges={changed.Summary.EdgeCount} parsed={changed.ParsedFiles} reused={changed.ReusedFiles}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Source(int file) => $"class Service{file} {{\n" +
        string.Join("\n", Enumerable.Range(0, 20).Select(method =>
            $"void Run{method}() {{ Run{(method + 1) % 20}(); }}")) + "\n}";
}
