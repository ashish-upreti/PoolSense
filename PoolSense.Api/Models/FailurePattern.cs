namespace PoolSense.Api.Models;

public class FailurePattern
{
    public int Id { get; set; }
    public string System { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string FailureType { get; set; } = string.Empty;
    public string ResolutionCategory { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public int KnowledgeYear { get; set; }
    public DateTime CreatedAt { get; set; }
}
