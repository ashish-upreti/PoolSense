namespace PoolSense.Api.Models;

public sealed record VisualizationPlan(bool Needed, string Kind, bool ExplicitRequest, string Purpose)
{
    public static VisualizationPlan None { get; } = new(false, "none", false, "");
    public bool IsGraph => Needed && Kind is "relationship" or "process";
}

public sealed record VisualizationRow(IReadOnlyList<string> Cells, IReadOnlyList<string> SourceIds);
public sealed record VisualizationTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<VisualizationRow> Rows);
public sealed record VisualizationPoint(string Label, double Value, IReadOnlyList<string> SourceIds, string EvidenceQuote);
public sealed record VisualizationChart(string Title, string Unit, IReadOnlyList<VisualizationPoint> Points);
