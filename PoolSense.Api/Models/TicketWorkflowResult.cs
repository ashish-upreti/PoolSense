namespace PoolSense.Api.Models;

public sealed class TicketWorkflowResult
{
    public string SuggestedRootCause { get; set; } = string.Empty;
    public string SuggestedResolution { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public IReadOnlyList<SimilarIncidentResult> SimilarIncidents { get; set; } = [];
    public FailurePattern FailurePattern { get; set; } = new();
    public string Reasoning { get; set; } = string.Empty;
    public int FailurePatternFrequency { get; set; }
}

public sealed class SimilarIncidentResult
{
    public string TicketId { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public double Similarity { get; set; }
}
