namespace PoolSense.Api.Models;

public class IngestionStatus
{
    public int Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public int TotalTickets { get; set; }
    public int IngestedTickets { get; set; }
    public DateTime LastUpdated { get; set; }
}