namespace PoolSense.Api.Feedback;

public sealed record FeedbackRequest
{
    public string Query { get; init; } = string.Empty;
    public string SuggestedResolution { get; init; } = string.Empty;
    public int FeedbackType { get; init; }
    public bool WasUsed { get; init; }
    public string? Comment { get; init; }
    public string[] RetrievedTicketIds { get; init; } = [];
}
