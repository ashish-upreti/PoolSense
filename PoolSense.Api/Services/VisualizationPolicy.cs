using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public static class VisualizationPolicy
{
    public const string Contract = """
        Visualization plan (server-validated system metadata): {{$visualizationPlan}}
        Honor this plan in the SAME final JSON response. A plan is not evidence.
        For kind process or relationship return processGraph with title, summary, nodes [{id,label,sourceIds}],
        edges [{from,to,sourceIds}] and gaps []. Use at most 12 nodes/20 edges, 5 gaps.
        Every node and edge needs actual current source IDs. Relationship graphs may have disconnected groups:
        do NOT join unrelated applications or infer an edge from shared vocabulary. Keep supported groups separate
        and identify missing cross-group relationships in gaps. A requested image means a supported rendered graph,
        NOT image generation or an external image URL. Issue decision paths may use the same graph.
        For kind table return visualizationTable:
        {"title":"Comparison","columns":["Application","Responsibility"],"rows":[{"cells":["App","Source-backed role"],"sourceIds":["C1"]}]}.
        Use 2-5 columns and 1-12 rows. All rows must be grounded in current sources.
        For kind bar return visualizationChart:
        {"title":"Observed latency","unit":"ms","points":[{"label":"App A","value":12,"sourceIds":["C1"],"evidenceQuote":"App A: 12 ms"}]}.
        Use 1-12 points with finite values within +/-1e12. Each evidenceQuote must be an EXACT single-line
        excerpt present in EVERY source referenced by that point, containing only a direct observation
        of the form "<label>: <value> <unit>" (also "=" or "|" separators). The label must match exactly;
        value must be a literal invariant numeric token, followed by the exact unit. A trailing period is allowed.
        Do not quote a paragraph where the label and measurement refer to different observations or applications.
        If the source does not supply an unambiguous direct observation, return null rather than infer one.
        No inferred counts, invented measurements, converted units, percentages or estimates.
        If evidence is insufficient return null data or supported partial graph with explicit gaps.
        If needed=false return null visual data. No model-authored Mermaid/chart fences with a server plan,
        including needed=false:
        the server validates structured data and prepends the rendered visual to detailedAnalysisMarkdown.
        Never claim an accompanying/below diagram, image, table or chart exists: server validation decides availability.
        """;

    public static VisualizationPlan Fallback(string title, string description)
    {
        var text = title + "\n" + description;
        var flowRequest = ProcessAnswerPolicy.WantsFlow(title, description);
        var explicitRequest = flowRequest || Regex.IsMatch(text, @"\b(draw|images?|diagrams?|visuali[sz](?:e|ation)|flowcharts?|charts?|graphs?|table)\b", RegexOptions.IgnoreCase);
        var relationship = Regex.IsMatch(text, @"\b(related|relationships?|dependencies|interact|integrat(?:e|ion)|communicat(?:e|ion))\b|how .{0,80}(?:talk|connect)", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(text, @"\b(table|tabular)\b", RegexOptions.IgnoreCase))
            return new(true, "table", true, "Compare source-backed facts.");
        if (Regex.IsMatch(text, @"\b(chart|plot|histogram)\b", RegexOptions.IgnoreCase))
            return new(true, "bar", true, "Compare only supplied numeric values with units.");
        if (relationship || explicitRequest)
            return new(true, relationship ? "relationship" : "process", explicitRequest, "Explain only source-established relationships and gaps.");
        return VisualizationPlan.None;
    }

    public static VisualizationPlan ReadPlan(JsonElement? raw, VisualizationPlan fallback, Action<string> log)
    {
        if (raw is null || raw.Value.ValueKind == JsonValueKind.Null)
        {
            log("missing");
            return fallback;
        }
        try
        {
            var value = raw.Value;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("needed", out var needed) || needed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !value.TryGetProperty("explicitRequest", out var explicitRequest) || explicitRequest.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("purpose", out var purpose) || purpose.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var name = kind.GetString()!;
            if (name is not ("none" or "process" or "relationship" or "table" or "bar") ||
                needed.GetBoolean() == (name == "none") || purpose.GetString()!.Length > 300)
                throw new JsonException();
            var plan = new VisualizationPlan(needed.GetBoolean(), name, explicitRequest.GetBoolean(), ProcessAnswerPolicy.Plain(purpose.GetString(), 300));
            // Explicit format requests cannot silently disappear when a model omits or contradicts them.
            return fallback.ExplicitRequest && (!plan.Needed || !plan.ExplicitRequest) ? fallback : plan;
        }
        catch (JsonException)
        {
            log("malformed-or-unsupported");
            return fallback;
        }
    }

    public static ProcessDiagramResult Build(VisualizationPlan plan, JsonElement? graph, JsonElement? table, JsonElement? chart, IReadOnlyList<ProcessSource> sources)
    {
        if (plan.IsGraph)
        {
            var result = ProcessAnswerPolicy.Build(ProcessAnswerPolicy.Parse(graph), sources);
            return plan.Kind == "relationship" ? result with { Markdown = result.Markdown.Replace("## Process flow", "## Application relationships", StringComparison.Ordinal) } : result;
        }
        var unavailable = new ProcessDiagramResult(false, "", "The requested visualization is unavailable: current evidence did not supply valid source-backed data.");
        var known = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        bool Grounded(IReadOnlyList<string>? ids) => ids is { Count: > 0 and <= 5 } && ids.All(id => id is not null && known.ContainsKey(id));
        bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 120;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (plan.Kind == "table")
            {
                var data = table?.Deserialize<VisualizationTable>(options);
                if (data is null || !Text(data.Title) || data.Columns is not { Count: >= 2 and <= 5 } ||
                    !data.Columns.All(Text) || data.Rows is not { Count: > 0 and <= 12 } ||
                    data.Rows.Any(row => row is null || row.Cells is null || row.Cells.Count != data.Columns.Count ||
                        !row.Cells.All(Text) || !Grounded(row.SourceIds))) return unavailable;
                string Cell(string value) => ProcessAnswerPolicy.Plain(value, 120).Replace("|", " ").Replace("\\", " ").Replace("!", "");
                var markdown = $"## {Cell(data.Title)}\n\n| {string.Join(" | ", data.Columns.Select(Cell))} | Sources |\n| {string.Join(" | ", data.Columns.Select(_ => "---"))} | --- |\n";
                foreach (var row in data.Rows)
                    markdown += $"| {string.Join(" | ", row.Cells.Select(Cell))} | {string.Join(", ", row.SourceIds)} |\n";
                return new(true, markdown + "\n", "Table generated from source-referenced facts.");
            }
            if (plan.Kind == "bar")
            {
                var data = chart?.Deserialize<VisualizationChart>(options);
                if (data is null || !Text(data.Title) || !Text(data.Unit) || data.Points is not { Count: > 0 and <= 12 } ||
                    data.Points.Any(point => point is null || !Text(point.Label) || !double.IsFinite(point.Value) ||
                        Math.Abs(point.Value) > 1e12 || !Grounded(point.SourceIds) || !Quantified(point, data.Unit, known))) return unavailable;
                var rendered = new
                {
                    type = "bar", title = ProcessAnswerPolicy.Plain(data.Title, 120),
                    labels = data.Points.Select(point => ProcessAnswerPolicy.Plain(point.Label, 120)),
                    datasets = new[] { new { label = ProcessAnswerPolicy.Plain(data.Unit, 120), data = data.Points.Select(point => point.Value) } }
                };
                return new(true, "## Source-backed chart\n\n```chart\n" + JsonSerializer.Serialize(rendered) +
                    "\n```\n\nSource evidence: " + string.Join(", ", data.Points.SelectMany(point => point.SourceIds).Distinct()) + "\n\n",
                    "Chart generated from supplied values and units.");
            }
        }
        catch (JsonException) { }
        return unavailable;
    }

    private static bool Quantified(VisualizationPoint point, string unit, IReadOnlyDictionary<string, ProcessSource> sources)
    {
        if (string.IsNullOrWhiteSpace(point.EvidenceQuote) || point.EvidenceQuote.Length > 500 ||
            point.EvidenceQuote.Any(char.IsControl) || point.Label.Any(char.IsControl) || unit.Any(char.IsControl) ||
            !point.SourceIds.All(id => sources[id].Content.Contains(point.EvidenceQuote, StringComparison.Ordinal))) return false;
        var quantified = $@"^\s*{Regex.Escape(point.Label)}\s*[:=|]\s*(?<value>[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s+{Regex.Escape(unit)}\.?\s*$";
        var match = Regex.Match(point.EvidenceQuote, quantified, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            value == point.Value;
    }

    public static string RemoveUnverifiedClaims(string? text)
    {
        return Regex.Replace(text ?? "", @"[^.!?\r\n]*(?:\b(?:accompanying|below|above|attached|following|shown|see|includes?|provided|illustrat\w*)\b[^.!?\r\n]*\b(?:diagram|graph|image|chart|visual\w*|table)\b|\b(?:diagram|graph|image|chart|visual\w*|table)\b[^.!?\r\n]*\b(?:accompanying|below|above|attached|following|shown|shows?|provided|illustrat\w*)\b)[^.!?\r\n]*[.!?]?",
            "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Trim();
    }
}
