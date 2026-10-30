using CodeRAG.Core.Indexing;
using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class CodeRAGCoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "poolsense-coderag-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndexAndRefresh_PreserveGraphAndRemoveDeletedCode()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "Widget.cs");
        var database = Path.Combine(directory, "index.sqlite3");
        await File.WriteAllTextAsync(source, "class Widget { void StartPump() { StopPump(); } void StopPump() {} }");
        var indexer = new RepositoryIndexer();
        var initial = await indexer.IndexAsync(directory, database);
        Assert.True(initial.Summary.EdgeCount > 0);
        using (var connection = GraphStore.Open(database, true))
        {
            var evidence = new Retriever().Retrieve("StartPump", connection, directory);
            Assert.Contains(evidence, symbol => symbol.Source.Contains("StartPump"));
        }

        File.Delete(source);
        await File.WriteAllTextAsync(Path.Combine(directory, "client.ts"), "export function NewClientOperation() { return 42; }");
        await indexer.IndexAsync(directory, database, refresh: true);
        using var refreshed = GraphStore.Open(database, true);
        Assert.Empty(new Retriever().Retrieve("StartPump", refreshed, directory));
        Assert.Contains(new Retriever().Retrieve("NewClientOperation", refreshed, directory), symbol => symbol.FilePath == "client.ts");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}