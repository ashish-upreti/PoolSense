using System.Text.Json;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class VisualizationPolicyTests
{
    [Theory]
    [InlineData("Can you create a image to explain how the multiple project applications are related to each other if at all", "relationship")]
    [InlineData("How apps talk", "relationship")]
    [InlineData("Draw that", "process")]
    [InlineData("Show publication flow", "process")]
    [InlineData("Compare roles in a table", "table")]
    [InlineData("Chart actual latency", "bar")]
    [InlineData("What is App A?", "none")]
    public void MissingPlan_FallbackPreservesIntent(string query, string kind)
    {
        var fallback = VisualizationPolicy.Fallback(query, query);
        Assert.Equal(kind, fallback.Kind);
        var logs = new List<string>();
        Assert.Equal(fallback, VisualizationPolicy.ReadPlan(null, fallback, logs.Add));
        Assert.Equal("missing", Assert.Single(logs));
    }

    [Theory]
    [InlineData("""{"needed":true,"kind":"pie","explicitRequest":true,"purpose":"Counts"}""")]
    [InlineData("""{"needed":true,"kind":"none","explicitRequest":true,"purpose":"Counts"}""")]
    [InlineData("""{"needed":false,"kind":"bar","explicitRequest":true,"purpose":"Counts"}""")]
    [InlineData("""{"needed":"true","kind":"bar","explicitRequest":true,"purpose":"Counts"}""")]
    [InlineData("""{"needed":true,"kind":"bar","explicitRequest":null,"purpose":"Counts"}""")]
    [InlineData("""[]""")]
    public void UnsupportedOrMalformedPlan_IsLoggedAndSafelyFallsBack(string raw)
    {
        var logs = new List<string>();
        var fallback = VisualizationPolicy.Fallback("draw related applications", "");
        var result = VisualizationPolicy.ReadPlan(JsonSerializer.Deserialize<JsonElement>(raw), fallback, logs.Add);
        Assert.Equal(fallback, result);
        Assert.Single(logs);
    }

    [Theory]
    [InlineData("Create an image of related apps")]
    [InlineData("Draw that")]
    [InlineData("Show a diagram of application dependencies")]
    public void ExplicitSchematicIntent_CannotBeSuppressedByAValidNoVisualPlan(string question)
    {
        var fallback = VisualizationPolicy.Fallback(question, "");
        var raw = JsonSerializer.SerializeToElement(new { needed = false, kind = "none", explicitRequest = true, purpose = "" });
        var result = VisualizationPolicy.ReadPlan(raw, fallback, _ => { });
        Assert.True(result.Needed);
        Assert.True(result.ExplicitRequest);
        Assert.True(result.IsGraph);
        Assert.Equal(fallback, result);
    }

    [Theory]
    [InlineData(12, "ms", "App A: 12 ms", true)]
    [InlineData(13, "ms", "App A: 12 ms", false)]
    [InlineData(12, "seconds", "App A: 12 ms", false)]
    [InlineData(12, "ms", "App A: 12 ms invented", false)]
    [InlineData(2, "ms", "App A: 12 ms", false)]
    public void QuantitativeChart_RequiresActualSourceValueUnitAndExactExcerpt(double value, string unit, string quote, bool provided)
    {
        var plan = new VisualizationPlan(true, "bar", true, "Compare observed latency.");
        var chart = new VisualizationChart("Latency", unit, [new("App A", value, ["C1"], quote)]);
        var result = VisualizationPolicy.Build(plan, null, null, JsonSerializer.SerializeToElement(chart),
            [new("C1", "Measurements", "App A: 12 ms", "")]);
        Assert.Equal(provided, result.Provided);
        Assert.Equal(provided, result.Markdown.Contains("```chart", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("App A: 2 workers; App B: 12 ms", 12)]
    [InlineData("App A: 2 workers; App B: 12 ms", 2)]
    [InlineData("App A: 2 workers\nApp B: 12 ms", 12)]
    [InlineData("App A is unrelated. App B: 12 ms", 12)]
    [InlineData("App A: 12 requests, not ms", 12)]
    public void QuantitativeChart_RejectsLabelsAndUnitsFromUnrelatedObservations(string quote, double value)
    {
        var chart = new VisualizationChart("Latency", "ms", [new("App A", value, ["C1"], quote)]);
        var built = VisualizationPolicy.Build(new(true, "bar", true, "Observed latency."), null, null,
            JsonSerializer.SerializeToElement(chart), [new("C1", "Measurements", quote, "")]);
        Assert.False(built.Provided);
        Assert.DoesNotContain("```chart", built.Markdown);
    }

    [Fact]
    public void QuantitativeChart_EveryReferencedSourceMustSupportTheExactObservation()
    {
        const string quote = "App A: 12 ms";
        var sources = new[] { new ProcessSource("C1", "Measurement", quote, ""),
            new ProcessSource("C2", "Unrelated measurement", "App B: 12 ms", "") };
        var chart = new VisualizationChart("Latency", "ms", [new("App A", 12, ["C1", "C2"], quote)]);
        Assert.False(VisualizationPolicy.Build(new(true, "bar", true, "Observed latency."), null, null,
            JsonSerializer.SerializeToElement(chart), sources).Provided);
        chart = chart with { Points = [new("App A", 12, ["C1"], quote)] };
        Assert.True(VisualizationPolicy.Build(new(true, "bar", true, "Observed latency."), null, null,
            JsonSerializer.SerializeToElement(chart), sources).Provided);
    }

    [Fact]
    public void Table_RejectsUnknownReferencesAndEscapesModelMarkup()
    {
        var plan = new VisualizationPlan(true, "table", true, "Compare roles.");
        var table = new VisualizationTable("Roles", ["App", "Role"], [new(["App A", "Publisher"], ["C99"])]);
        var sources = new[] { new ProcessSource("C1", "Roles", "App A publishes.", "") };
        Assert.False(VisualizationPolicy.Build(plan, null, JsonSerializer.SerializeToElement(table), null, sources).Provided);
        table = table with { Rows = [new(["App A | B", "![bad](https://example.test)"], ["C1"])] };
        var built = VisualizationPolicy.Build(plan, null, JsonSerializer.SerializeToElement(table), null, sources);
        Assert.True(built.Provided);
        Assert.DoesNotContain("![", built.Markdown);
        Assert.Contains("| App A   B |", built.Markdown);
    }

    [Fact]
    public void Graph_DisconnectedGroupsArePartialEvenWhenModelOmitsGap()
    {
        var graph = new ProcessDiagram("Apps", "", [new("a", "App A", ["C1"]), new("b", "App B", ["C1"]),
            new("c", "App C", ["C1"]), new("d", "App D", ["C1"])], [new("a", "b", ["C1"]), new("c", "d", ["C1"])], []);
        var result = ProcessAnswerPolicy.Build(graph, [new("C1", "App links", "A calls B. C calls D.", "")]);
        Assert.True(result.Provided);
        Assert.True(result.Partial);
        Assert.Contains("Disconnected groups", result.Markdown);
    }

    [Fact]
    public void RepairContext_IsBoundedAndRedacted_AndHistoryRoundTripsPlan()
    {
        var sources = Enumerable.Range(1, 100).Select(index => new ProcessSource($"C{index}", "Evidence", new string('a', 5000), "")).ToArray();
        var bounded = ProcessAnswerPolicy.RepairSources(sources);
        Assert.True(JsonSerializer.Serialize(bounded, JsonSerializerOptions.Web).Length <= 12000);
        var escaped = Enumerable.Range(1, 100).Select(index => new ProcessSource($"C{index}", "Evidence", new string('\u4e00', 5000), "")).ToArray();
        Assert.True(JsonSerializer.Serialize(ProcessAnswerPolicy.RepairSources(escaped), JsonSerializerOptions.Web).Length <= 12000);
        var result = new TicketWorkflowResult { VisualizationPlan = new(true, "relationship", true, "Explain application links."),
            VisualizationStatus = "partial", VisualizationMessage = "Cross-group links unestablished.", ProcessDiagramStatus = "provided" };
        var restored = JsonSerializer.Deserialize<TicketWorkflowResult>(JsonSerializer.Serialize(result, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.Equal(result.VisualizationPlan, restored.VisualizationPlan);
        Assert.Equal("partial", restored.VisualizationStatus);
        Assert.Equal(result.VisualizationMessage, restored.VisualizationMessage);
    }
}
