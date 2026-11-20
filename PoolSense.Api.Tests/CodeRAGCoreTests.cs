using CodeRAG.Core.Indexing;
using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using PoolSense.Api.Services.CodeRAG;
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

    [Theory]
    [InlineData("How are indexing and retrieval related?")]
    [InlineData("How indexing and retreival is related/")]
    public async Task PlainWords_MatchOtherWordForms_AndMisspellingsAreCorrectedFromTheIndex(string question)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Indexing"));
        Directory.CreateDirectory(Path.Combine(directory, "Search"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Indexing", "RepositoryIndexer.cs"),
            "namespace Demo.Indexing { public class RepositoryIndexer { public void IndexAsync() {} } }");
        await File.WriteAllTextAsync(Path.Combine(directory, "Search", "Retriever.cs"),
            "namespace Demo.Search { public class Retriever { public void RetrieveAsync() {} } }");
        await File.WriteAllTextAsync(Path.Combine(directory, "Search", "SearchOptions.cs"),
            "namespace Demo.Search { public class SearchOptions { /* Retrieval limits. */ public int Limit { get; set; } } }");
        var database = Path.Combine(directory, "index.sqlite3");
        await new RepositoryIndexer().IndexAsync(directory, database);

        using var db = GraphStore.Open(database, true);
        var files = new Retriever().Retrieve(question, db, directory).Select(symbol => symbol.FilePath.Replace('\\', '/')).ToArray();

        Assert.Contains("Search/Retriever.cs", files);
        Assert.Contains("Indexing/RepositoryIndexer.cs", files);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public async Task IncrementalSnapshot_ReusesUnchangedFilesAndRebuildsCrossFileEdges()
    {
        var first = Path.Combine(directory, "first");
        var next = Path.Combine(directory, "next");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(next);
        const string caller = "namespace N; class B { void Run() { A.Old(); } }";
        await File.WriteAllTextAsync(Path.Combine(first, "A.cs"), "namespace N; class A { public static void Old() {} }");
        await File.WriteAllTextAsync(Path.Combine(first, "B.cs"), caller);
        await File.WriteAllTextAsync(Path.Combine(first, "Deleted.cs"), "class Deleted {}");
        var indexer = new RepositoryIndexer();
        var previousDb = Path.Combine(first, "index.sqlite3");
        await indexer.IndexAsync(first, previousDb);
        await File.WriteAllTextAsync(Path.Combine(next, "A.cs"), "namespace N; class A { public static void New() {} }");
        await File.WriteAllTextAsync(Path.Combine(next, "B.cs"), caller);
        await File.WriteAllTextAsync(Path.Combine(next, "C.cs"), "namespace N; class C { void Run() { A.New(); } }");
        var nextDb = Path.Combine(next, "index.sqlite3");
        var result = await indexer.IndexAsync(next, nextDb, true, previousDatabase: previousDb);
        Assert.Equal(1, result.ReusedFiles);
        Assert.Equal(2, result.ParsedFiles);
        using var db = GraphStore.Open(nextDb, true);
        Assert.Equal(0L, Convert.ToInt64(GraphStore.Scalar(db, "SELECT count(*) FROM symbols WHERE name IN ('Old','Deleted')")));
        var calls = GraphStore.Query(db, """
            SELECT a.qualified_name,b.qualified_name FROM edges e
            JOIN symbols a ON a.id=e.from_id JOIN symbols b ON b.id=e.to_id WHERE e.kind='CALLS'
            """, reader => (reader.GetString(0), reader.GetString(1)));
        Assert.Equal([("N.C.Run", "N.A.New")], calls);
        Assert.Equal(3L, Convert.ToInt64(GraphStore.Scalar(db, "SELECT count(*) FROM source_files")));
        using var original = GraphStore.Open(previousDb, true);
        Assert.Equal(1L, Convert.ToInt64(GraphStore.Scalar(original, "SELECT count(*) FROM symbols WHERE name='Deleted'")));
    }

    [Fact]
    public async Task GraphExpansion_BoundsWorkDespiteUnrelatedEdges_AndRetainsCallersAndCallees()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Pump.cs"),
            "class Pump {\n public static void StartPump() {\n StopPump();\n }\n static void StopPump() {}\n}");
        await File.WriteAllTextAsync(Path.Combine(directory, "Caller.cs"),
            "class Caller {\n void Invoke() {\n Pump.StartPump();\n }\n}");
        await File.WriteAllTextAsync(Path.Combine(directory, "Noise.cs"),
            "class Noise { void WriteNoise() {} void ReadNoise() {} }");
        var database = Path.Combine(directory, "index.sqlite3");
        await new RepositoryIndexer().IndexAsync(directory, database);
        using var db = GraphStore.Open(database);
        var root = GraphStore.RootKey(directory);
        var from = GraphStore.Scalar(db, "SELECT id FROM symbols WHERE name='WriteNoise'");
        var to = GraphStore.Scalar(db, "SELECT id FROM symbols WHERE name='ReadNoise'");
        GraphStore.Execute(db, """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<50000)
            INSERT INTO edges(repo_root,from_id,to_id,kind,resolution)
            SELECT @p0,@p1,@p2,'CALLS','syntax' FROM n
            """, root, from, to);
        var progress = 0;
        SQLitePCL.raw.sqlite3_progress_handler(db.Handle, 1000, _ => ++progress > 200 ? 1 : 0, null);
        try
        {
            var evidence = new Retriever().Retrieve("StartPump", db, directory);
            Assert.Contains(evidence, symbol => symbol.Name == "StartPump");
            Assert.Contains(evidence, symbol => symbol.Name == "Invoke");
            Assert.Contains(evidence, symbol => symbol.Name == "StopPump");
            Assert.DoesNotContain(evidence, symbol => symbol.FilePath == "Noise.cs");
        }
        finally { SQLitePCL.raw.sqlite3_progress_handler(db.Handle, 0, null, null); }
    }

    [Fact]
    public async Task WorkflowRetrieval_BridgesInjectedInterfaceCalls_LargeOrchestrators_AndOverlappingPollingBodies()
    {
        Directory.CreateDirectory(Path.Combine(directory, "Controllers"));
        Directory.CreateDirectory(Path.Combine(directory, "Orchestration"));
        Directory.CreateDirectory(Path.Combine(directory, "Services"));

        await File.WriteAllTextAsync(Path.Combine(directory, "Controllers", "ResolutionController.cs"), """
            namespace Demo.Controllers;
            public class ResolutionController
            {
                private readonly Demo.Orchestration.ITicketWorkflowOrchestrator _orchestrator;
                public ResolutionController(Demo.Orchestration.ITicketWorkflowOrchestrator orchestrator) => _orchestrator = orchestrator;
                public string Post(string request) => _orchestrator.RecommendAsync(request);
            }
            """);

        var orchestratorPadding = string.Concat(Enumerable.Range(1, 120)
            .Select(i => $"        var preparationStep{i:D3} = \"categorizing and analyzing ticket metadata step {i}\";\n"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Orchestration", "TicketWorkflowOrchestrator.cs"), $$"""
            namespace Demo.Orchestration;
            public interface ITicketWorkflowOrchestrator
            {
                string RecommendAsync(string request);
            }
            public class TicketWorkflowOrchestrator : ITicketWorkflowOrchestrator
            {
                private readonly Demo.Services.PoolVectorStore _pool;
                private readonly Demo.Services.NyraWikiClient _wiki;
                private readonly Demo.Services.ResolutionGenerator _generator;
                public TicketWorkflowOrchestrator(Demo.Services.PoolVectorStore pool, Demo.Services.NyraWikiClient wiki, Demo.Services.ResolutionGenerator generator)
                {
                    _pool = pool;
                    _wiki = wiki;
                    _generator = generator;
                }
                public string RecommendAsync(string request) => ProcessInternalAsync(request);
                private string ProcessInternalAsync(string request)
                {
            {{orchestratorPadding}}
                    var incidents = _pool.SearchSimilarTickets(request);
                    var docs = _wiki.RetrieveNyraDocumentsAsync(request);
                    return _generator.GenerateResolutionAsync(incidents + docs);
                }
            }
            """);

        var pollingPadding = string.Concat(Enumerable.Range(1, 115)
            .Select(i => $"        var pollCheck{i:D3} = \"checking project configuration and closed ticket sync {i}\";\n"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Services", "BackgroundTicketPollingService.cs"), $$"""
            namespace Demo.Services;
            public class BackgroundTicketPollingService
            {
                public void PollOnceAsync(Demo.Orchestration.ITicketWorkflowOrchestrator orchestrator, TicketEmailSender emailService)
                {
            {{pollingPadding}}
                    ProcessNewTicketsAsync(orchestrator, emailService);
                }
                private void ProcessNewTicketsAsync(Demo.Orchestration.ITicketWorkflowOrchestrator orchestrator, TicketEmailSender emailService)
                {
                    var recommendation = orchestrator.RecommendAsync("new-ticket");
                    emailService.SendRecommendationAsync("ops@example.com", recommendation);
                }
            }
            public class TicketEmailSender { public void SendRecommendationAsync(string to, string body) {} }
            public class PoolVectorStore { public string SearchSimilarTickets(string q) => q; }
            public class NyraWikiClient { public string RetrieveNyraDocumentsAsync(string q) => q; }
            public class ResolutionGenerator { public string GenerateResolutionAsync(string ctx) => ctx; }
            """);

        var database = Path.Combine(directory, "index.sqlite3");
        await new RepositoryIndexer().IndexAsync(directory, database);

        const string query = "Explain the end-to-end ticket recommendation and background polling workflow";
        var plan = CodeQueryPlan.Create(query);
        Assert.True(plan.IsWorkflow);

        using var db = GraphStore.Open(database, readOnly: true);
        var spans = new Retriever().Retrieve(query, db, directory, plan.Candidates(20), Retriever.MaxContextChars);
        var repository = new CodeRepository
        {
            Id = "repo-1",
            ProjectId = "proj-1",
            FullName = "demo/poolsense",
            Branch = "main",
            Generation = "gen-1",
            IndexedCommit = new string('a', 40)
        };
        var buffer = new CodeCandidateBuffer(query, 256);
        for (var i = 0; i < spans.Count; i++)
            buffer.Add(new CodeSearchCandidate(repository, spans[i], LexicalRank: i + 1));

        var selected = CodeCandidateSelection.Select(query, buffer, plan.Spans(12), plan.Characters(24000));
        var combined = string.Join("\n", selected.Select(item => item.Content));

        Assert.Contains("RecommendAsync(string request) => ProcessInternalAsync(request)", combined);
        Assert.Contains("SearchSimilarTickets", combined);
        Assert.Contains("RetrieveNyraDocumentsAsync", combined);
        Assert.Contains("GenerateResolutionAsync", combined);
        Assert.Contains("SendRecommendationAsync(\"ops@example.com\"", combined);
    }
}