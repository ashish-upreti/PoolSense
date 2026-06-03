namespace PoolSense.Api.Feedback;

public sealed record ApplicationFeedbackRequest
{
    public string UserName { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    public string FeedbackType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}