namespace PoolSense.Api.Models;

public sealed class TicketWorkflowResult
{
    public string SuggestedRootCause { get; set; } = string.Empty;
    public string SuggestedResolution { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public IReadOnlyList<SimilarIncidentResult> SimilarIncidents { get; set; } = [];
    public IReadOnlyList<NyraDocumentResult> NyraDocuments { get; set; } = [];
    public bool NyraKnowledgeBaseUsed { get; set; }
    public string NyraKnowledgeBaseStatus { get; set; } = string.Empty;
    public string NyraKnowledgeBaseMessage { get; set; } = string.Empty;
    public IReadOnlyList<string> NyraKnowledgeBaseNames { get; set; } = [];
    public IReadOnlyList<string> NyraKnowledgeBaseProjects { get; set; } = [];
    public string QueryCategory { get; set; } = string.Empty;
    public string QueryCategorizationReasoning { get; set; } = string.Empty;
    public bool UsedPoolDatabase { get; set; }
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

public sealed class NyraDocumentResult
{
    public string DocumentId { get; set; } = string.Empty;
    public string KbName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Citation { get; set; } = string.Empty;
    public double Score { get; set; }
}
