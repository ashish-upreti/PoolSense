namespace PoolSense.Api.Feedback;

public sealed class FeedbackLog
{
    public int Id { get; set; }
    public string TicketQuery { get; set; } = string.Empty;
    public string SuggestedResolution { get; set; } = string.Empty;
    public int FeedbackType { get; set; }
    public bool WasUsed { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string RetrievedTicketIds { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
