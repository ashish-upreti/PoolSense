namespace PoolSense.Api.Feedback;

public sealed class ValidatedResolution
{
    public int Id { get; set; }
    public string TargetTicketId { get; set; } = string.Empty;
    public string CurrentIssueId { get; set; } = string.Empty;
    public string ConfirmedNote { get; set; } = string.Empty;
    public int FeedbackType { get; set; }
    public bool WasUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
