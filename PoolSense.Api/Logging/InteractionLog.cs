namespace PoolSense.Api.Logging;

/// <summary>
/// Stores metadata about an AI pipeline interaction for later analysis.
/// </summary>
public sealed class InteractionLog
{
    public int Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public int GeneratedEmbeddingLength { get; set; }
    public string RetrievedTicketIds { get; set; } = string.Empty;
    public string RetrievedContents { get; set; } = string.Empty;
    public string SuggestedResolution { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int ProcessingTimeMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
