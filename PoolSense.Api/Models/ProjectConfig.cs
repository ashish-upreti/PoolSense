namespace PoolSense.Api.Models;

public class ProjectConfig
{
    public int Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public int KnowledgeLookbackYears { get; set; } = 2;
    public int SimilaritySearchLimit { get; set; } = 5;
    public bool SendEmail { get; set; } = true;
    public bool PoolingEnabled { get; set; } = true;
    public string EmailRecipients { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string TicketSourceType { get; set; } = "sql";
    public string ConnectionString { get; set; } = string.Empty;
    public List<string> KnowledgeSources { get; set; } = [];
    public string ApplicationFilter { get; set; } = string.Empty;
    public bool IsActive
    {
        get => PoolingEnabled;
        set => PoolingEnabled = value;
    }
}
