namespace PoolSense.Api.Models;

public class TicketKnowledge
{
    public int Id { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string[] Keywords { get; set; } = [];
    public List<string> SearchVariants { get; set; } = [];
    public float[] Embedding { get; set; } = [];
    public string Application { get; set; } = string.Empty;
    public int KnowledgeYear { get; set; }
    public string SourceStatus { get; set; } = string.Empty;
    public DateTime? SourceSubmittedAt { get; set; }
    public DateTime? SourceClosedAt { get; set; }
    public string SubmitterId { get; set; } = string.Empty;
    public string LifeguardId { get; set; } = string.Empty;
    public string SourceProject { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public double Similarity { get; set; }
}