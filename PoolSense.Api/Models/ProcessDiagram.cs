namespace PoolSense.Api.Models;

public enum ResolutionAnswerIntent { Incident, Explanation }

public sealed record ProcessNode(string Id, string Label, IReadOnlyList<string> SourceIds);
public sealed record ProcessEdge(string From, string To, IReadOnlyList<string> SourceIds);
public sealed record ProcessDiagram(string Title, string Summary, IReadOnlyList<ProcessNode> Nodes,
    IReadOnlyList<ProcessEdge> Edges, IReadOnlyList<string> Gaps);
public sealed record ProcessSource(string Id, string Title, string Content, string SourceUrl);
public sealed record ProcessDiagramResult(bool Provided, string Markdown, string Message, bool Partial = false);
