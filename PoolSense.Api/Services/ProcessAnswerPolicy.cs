using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using PoolSense.Api.Agents;
using PoolSense.Api.Models;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Services;

public interface IProcessDiagramRepair
{
    Task<string> RepairProcessDiagramAsync(string question, IReadOnlyList<ProcessSource> sources, CancellationToken token);
    Task<string> RepairVisualizationAsync(string question, VisualizationPlan plan, IReadOnlyList<ProcessSource> sources, CancellationToken token) =>
        RepairProcessDiagramAsync(question, sources, token);
}

public static class ProcessAnswerPolicy
{
    public static ProcessDiagram? Parse(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object }) return null;
        try { return value.Value.Deserialize<ProcessDiagram>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }

    public static bool ReportsFailure(string text) => Regex.IsMatch(text,
        @"\b(error|fail(?:ed|ing|ure|s)?|exception|broken|bug|crash(?:ed)?|timeout|stuck|missing|incorrect|fix|resolve|troubleshoot)\b|not working|cannot|can't", RegexOptions.IgnoreCase);
    public static bool WantsFlow(string question, string description)
    {
        var text = question + "\n" + description;
        var process = Regex.IsMatch(text, @"\b(flow|workflow|process|architecture|diagram|sequence|pipeline|journey)\b|end[\s-]+to[\s-]+end|walk me through", RegexOptions.IgnoreCase);
        return process && (!ReportsFailure(text) || Regex.IsMatch(text, @"\b(show|draw|explain|describe|visuali[sz]e|diagram)\b", RegexOptions.IgnoreCase));
    }
    public static ResolutionAnswerIntent Intent(string question, string description) =>
        !ReportsFailure(question + "\n" + description) && (WantsFlow(question, description) ||
            Regex.IsMatch(question, @"^\s*(how|what|explain|describe|show|tell me|walk me)", RegexOptions.IgnoreCase))
            ? ResolutionAnswerIntent.Explanation : ResolutionAnswerIntent.Incident;

    public static IReadOnlyList<ProcessSource> Sources(IReadOnlyList<ResolutionIncident> incidents,
        IReadOnlyList<NyraDocumentResult> documents, IReadOnlyList<CodeEvidence> code)
    {
        var sources = new List<ProcessSource>();
        for (var index = 0; index < code.Count; index++)
            sources.Add(new($"C{index + 1}", $"{code[index].FilePath}:{code[index].StartLine}-{code[index].EndLine}", code[index].Content, code[index].SourceUrl));
        for (var index = 0; index < documents.Count; index++)
            sources.Add(new($"N{index + 1}", documents[index].Title, documents[index].Content, documents[index].SourceUrl));
        for (var index = 0; index < incidents.Count; index++)
            sources.Add(new($"T{index + 1}", $"Incident {incidents[index].TicketId}", incidents[index].Problem + "\n" + incidents[index].Resolution, ""));
        return sources.Where(source => !string.IsNullOrWhiteSpace(source.Content)).ToArray();
    }

    public static IReadOnlyList<ProcessSource> RepairSources(IReadOnlyList<ProcessSource> sources)
    {
        var result = new List<ProcessSource>();
        const int budget = 12000;
        foreach (var source in sources.OrderBy(source => int.Parse(source.Id[1..])).ThenBy(source => source.Id, StringComparer.Ordinal))
        {
            var title = Plain(source.Title, 160);
            var content = global::CodeRAG.Core.Indexing.SourceText.Redact(source.Content);
            content = content[..Math.Min(content.Length, 1200)];
            var candidate = source with { Title = title, Content = content };
            if (JsonSerializer.Serialize(result.Append(candidate), JsonSerializerOptions.Web).Length > budget) continue;
            result.Add(candidate);
        }
        return result;
    }

    public static ProcessDiagramResult Build(ProcessDiagram? diagram, IReadOnlyList<ProcessSource> sources)
    {
        var unavailable = new ProcessDiagramResult(false, "", sources.Count == 0
            ? "No current source-backed process steps were retrieved. Process documentation or code showing the steps and connections is needed."
            : "A validated source-backed process diagram was not returned. Unsupported steps or connections have not been inferred.");
        if (diagram?.Nodes is { Count: 0 } && diagram.Gaps is { Count: > 0 and <= 5 })
            return unavailable with { Message = "Process relationships remain unestablished: " + string.Join("; ", diagram.Gaps.Select(gap => Plain(gap, 240))) };
        if (diagram?.Nodes is not { Count: > 0 and <= 12 } || diagram.Edges is null || diagram.Edges.Count > 20 ||
            diagram.Gaps is null || diagram.Gaps.Count > 5 || sources.Count == 0) return unavailable;
        if (diagram.Nodes.Any(node => node is null) || diagram.Edges.Any(edge => edge is null)) return unavailable;
        var ids = diagram.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var known = sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        bool Grounded(IReadOnlyList<string>? refs) => refs is { Count: > 0 and <= 5 } && refs.All(id => id is not null && known.Contains(id));
        if (ids.Count != diagram.Nodes.Count || diagram.Nodes.Any(node => node.Id is null || !Regex.IsMatch(node.Id, @"^[a-z][a-z0-9_]{0,20}$") ||
                string.IsNullOrWhiteSpace(node.Label) || node.Label.Length > 120 || !Grounded(node.SourceIds)) ||
            diagram.Edges.Any(edge => edge.From is null || edge.To is null || !ids.Contains(edge.From) || !ids.Contains(edge.To) || !Grounded(edge.SourceIds)) ||
            diagram.Nodes.Count > 1 && diagram.Edges.Count == 0 && diagram.Gaps.Count == 0) return unavailable;
        var connected = new HashSet<string>(StringComparer.Ordinal) { diagram.Nodes[0].Id };
        for (var pass = 0; pass < diagram.Nodes.Count; pass++)
            foreach (var edge in diagram.Edges)
                if (connected.Contains(edge.From) || connected.Contains(edge.To)) { connected.Add(edge.From); connected.Add(edge.To); }
        var partial = diagram.Gaps.Count > 0 || diagram.Edges.Count == 0 || connected.Count != diagram.Nodes.Count;
        var builder = new StringBuilder("## Process flow\n\n```mermaid\nflowchart TD\n");
        foreach (var node in diagram.Nodes)
        {
            var label = Regex.Replace(Plain(node.Label, 72), @"[^\p{L}\p{N} _.,:/+-]", " ").Trim();
            if (label.Length == 0 || Regex.IsMatch(label, @"https?:|javascript:|data:", RegexOptions.IgnoreCase)) return unavailable;
            label = Regex.Replace(label, @"\b(click|style|classDef|linkStyle)\b", match => $"#{(int)match.Value[0]};{match.Value[1..]}", RegexOptions.IgnoreCase);
            builder.Append("  p_").Append(node.Id).Append("[\"").Append(label).Append("\"]\n");
        }
        foreach (var edge in diagram.Edges.DistinctBy(edge => (edge.From, edge.To)))
            builder.Append("  p_").Append(edge.From).Append(" --> p_").Append(edge.To).Append('\n');
        builder.Append("```\n\n");
        if (diagram.Gaps.Count > 0)
            builder.Append("Supported partial flow only. Unconfirmed connections: ").Append(string.Join("; ", diagram.Gaps.Select(gap => Plain(gap, 240)))).Append("\n\n");
        else if (diagram.Edges.Count == 0)
            builder.Append("Supported step only; connections beyond this step are not established by the retrieved evidence.\n\n");
        else if (connected.Count != diagram.Nodes.Count)
            builder.Append("Supported partial flow only. Disconnected groups are intentional; cross-group relationships are not established by current evidence.\n\n");
        var used = diagram.Nodes.SelectMany(node => node.SourceIds).Concat(diagram.Edges.SelectMany(edge => edge.SourceIds)).ToHashSet();
        builder.Append("Source evidence: ");
        builder.AppendJoin("; ", sources.Where(source => used.Contains(source.Id)).Select(source =>
            Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0
                ? $"[{Plain(source.Title, 160)}](<{source.SourceUrl}>)"
                : Plain(source.Title, 160)));
        builder.Append("\n\n");
        return new(true, builder.ToString(), partial ? "Only source-supported steps and transitions are shown; gaps are identified." : "Flow generated from source-referenced process data.", partial);
    }

    public static string Attach(string markdown, ProcessDiagramResult result, string heading = "Process flow")
    {
        // Never keep a malformed/model-authored Mermaid fence alongside the validated process graph.
        var rest = Regex.Replace(markdown ?? "", @"(?ims)^[ \t]*(`{3,}|~{3,})[ \t]*mermaid[^\r\n]*\r?\n.*?(?:^[ \t]*\1[^\r\n]*(?:\r?\n|$)|\z)", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        rest = Regex.Replace(rest, @"(?ims)^[ \t]*(`{3,}|~{3,})[ \t]*chart[^\r\n]*\r?\n.*?(?:^[ \t]*\1[^\r\n]*(?:\r?\n|$)|\z)", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!result.Provided) rest = VisualizationPolicy.RemoveUnverifiedClaims(rest);
        return result.Provided ? result.Markdown + rest : $"## {heading}\n\n{result.Message}\n\n{rest}";
    }

    public static string Plain(string? text, int limit)
    {
        var value = Regex.Replace(text ?? "", @"[<>\[\]`*_#\r\n]", " ").Trim();
        return value[..Math.Min(value.Length, limit)];
    }
}
