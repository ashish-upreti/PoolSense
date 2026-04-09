namespace PoolSense.Api.Models;

public class ProjectConfig
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string TicketSourceType { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public List<string> KnowledgeSources { get; set; } = [];
    public bool IsActive { get; set; }
}
