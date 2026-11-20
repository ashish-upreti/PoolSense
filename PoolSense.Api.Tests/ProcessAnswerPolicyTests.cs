using System.Text.Json;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class ProcessAnswerPolicyTests
{
    [Fact]
    public void PartialGraph_PreservesOnlySourceBackedEdges_AndMarksMissingConnections()
    {
        var graph = new ProcessDiagram("Publication", "Supported fragment.",
            [new("end", "Click publish", ["C1"]), new("save", "Save publication", ["C1"])],
            [new("end", "save", ["C1"])], ["Downstream notification is not established by this excerpt."]);
        var built = ProcessAnswerPolicy.Build(graph, [new("C1", "Handler.cs:1-10", "Publish(); Save();", "https://github.com/org/repo/blob/sha/Handler.cs#L1-L10")]);
        Assert.True(built.Provided);
        Assert.Contains("p_end --> p_save", built.Markdown);
        Assert.Contains("Supported partial flow", built.Markdown);
        Assert.DoesNotContain("--> notify", built.Markdown);
    }

    [Fact]
    public void UnknownReferencesAndUnsafeNodeIds_AreRejected_AndModelFencesRemoved()
    {
        var graph = new ProcessDiagram("Bad", "", [new("n1", "Known", ["C99"])], [], []);
        var sources = new[] { new ProcessSource("C1", "Known", "Actual evidence", "") };
        Assert.False(ProcessAnswerPolicy.Build(graph, sources).Provided);
        graph = graph with { Nodes = [new("a;click", "Known", ["C1"])] };
        Assert.False(ProcessAnswerPolicy.Build(graph, sources).Provided);
        var unavailable = ProcessAnswerPolicy.Build(null, sources);
        var markdown = ProcessAnswerPolicy.Attach("## Analysis\n~~~mermaid\nmalformed[\n~~~\nKeep this explanation.", unavailable);
        Assert.DoesNotContain("malformed", markdown);
        Assert.Contains("Keep this explanation", markdown);
        Assert.Contains("validated source-backed", markdown);
        Assert.Null(ProcessAnswerPolicy.Parse(JsonSerializer.SerializeToElement("not-a-graph")));
        Assert.Null(ProcessAnswerPolicy.Parse(JsonSerializer.SerializeToElement(new { nodes = "malformed" })));
    }

    [Fact]
    public void ColonPunctuatedLabels_AreAccepted_WhileSchemeUrisAreRejected()
    {
        var sources = new[] { new ProcessSource("C1", "CodeIndex.cs:1-20", "Index symbols and edges", "https://github.com/org/repo/blob/sha/CodeIndex.cs#L1-L20") };
        var valid = new ProcessDiagram("Indexing", "Supported indexing steps.",
            [
                new("roots", "Project roots", ["C1"]),
                new("store", "Store searchable code data: symbols and edges", ["C1"]),
                new("meta", "Metadata: Http:Timeout and Data:ConnectionString", ["C1"])
            ],
            [new("roots", "store", ["C1"]), new("store", "meta", ["C1"])], []);

        var built = ProcessAnswerPolicy.Build(valid, sources);
        Assert.True(built.Provided);
        Assert.Contains("p_store[\"Store searchable code data: symbols and edges\"]", built.Markdown);

        foreach (var unsafeLabel in new[]
        {
            "Load https://evil.test/payload",
            "Run javascript:alert(1)",
            "Embed data:image/svg+xml,<svg>",
            "Embed data:;base64,AAAA"
        })
        {
            var rejected = valid with { Nodes = [new("bad", unsafeLabel, ["C1"])], Edges = [] };
            Assert.False(ProcessAnswerPolicy.Build(rejected, sources).Provided);
        }
    }

    [Fact]
    public void MultiplePartialFlowGaps_FormatAsCleanBlockquoteBulletsWithoutDoublePunctuation()
    {
        var sources = new[] { new ProcessSource("C1", "ResolutionController.cs:1-40", "RecommendAsync();", "https://github.com/org/repo/blob/sha/ResolutionController.cs#L1-L40") };
        var graph = new ProcessDiagram("Ticket flow", "Partial flow.",
            [new("ctrl", "ResolutionController receives ticket", ["C1"]), new("orch", "Invoke orchestrator", ["C1"])],
            [new("ctrl", "orch", ["C1"])],
            [
                "RecommendAsync implementation is not supplied.",
                "Downstream Wiki retrieval is not supplied.;",
                "ProcessNewTicketsAsync body is incomplete in the supplied excerpt"
            ]);

        var built = ProcessAnswerPolicy.Build(graph, sources);
        Assert.True(built.Provided);
        Assert.True(built.Partial);
        Assert.Contains("> **Supported partial flow only** — Unconfirmed connections:\n", built.Markdown);
        Assert.Contains("> - RecommendAsync implementation is not supplied.\n", built.Markdown);
        Assert.Contains("> - Downstream Wiki retrieval is not supplied.\n", built.Markdown);
        Assert.Contains("> - ProcessNewTicketsAsync body is incomplete in the supplied excerpt.\n", built.Markdown);
        Assert.DoesNotContain(".;", built.Markdown);
    }

    [Fact]
    public void MermaidAndStatus_RoundTripInStoredResultSource()
    {
        var result = new TicketWorkflowResult { QueryCategory = "Info", ProcessDiagramStatus = "provided", DetailedAnalysisMarkdown = "## Process flow\n```mermaid\nflowchart TD\np_a-->p_b\n```" };
        var restored = JsonSerializer.Deserialize<TicketWorkflowResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal("Info", restored.QueryCategory);
        Assert.Equal("provided", restored.ProcessDiagramStatus);
        Assert.Equal(result.DetailedAnalysisMarkdown, restored.DetailedAnalysisMarkdown);
    }
}
