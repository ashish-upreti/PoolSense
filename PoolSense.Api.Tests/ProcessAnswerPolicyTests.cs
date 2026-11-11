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
    public void MermaidAndStatus_RoundTripInStoredResultSource()
    {
        var result = new TicketWorkflowResult { QueryCategory = "Info", ProcessDiagramStatus = "provided", DetailedAnalysisMarkdown = "## Process flow\n```mermaid\nflowchart TD\np_a-->p_b\n```" };
        var restored = JsonSerializer.Deserialize<TicketWorkflowResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal("Info", restored.QueryCategory);
        Assert.Equal("provided", restored.ProcessDiagramStatus);
        Assert.Equal(result.DetailedAnalysisMarkdown, restored.DetailedAnalysisMarkdown);
    }
}
