namespace PoolSense.Api.Models;

public sealed class TicketWorkflowProgress
{
    public string Stage { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string State { get; set; } = "active";
    public int Order { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}
