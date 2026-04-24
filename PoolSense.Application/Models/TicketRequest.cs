namespace PoolSense.Application.Models;

public class TicketRequest
{
    public string TicketId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string SourceResolution { get; set; } = string.Empty;
    public string SourceSolution { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string EventStatusName { get; set; } = string.Empty;
    public string EventStatusId { get; set; } = string.Empty;
    public string SubmitterId { get; set; } = string.Empty;
    public string LifeguardId { get; set; } = string.Empty;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? SimilaritySearchLimitOverride { get; set; }
    /// <summary>
    /// Optional project IDs (from project_configs) used to scope similarity search.
    /// null or empty = search across all configured projects; non-empty = search only the selected projects.
    /// </summary>
    public List<string>? SelectedGroupIds { get; set; } = null;

    public string GetWorkflowTitle()
    {
        if (!string.IsNullOrWhiteSpace(Title))
        {
            return Title;
        }

        if (!string.IsNullOrWhiteSpace(Issue))
        {
            return Issue;
        }

        return string.IsNullOrWhiteSpace(TicketId)
            ? SourceEventId
            : TicketId;
    }

    public string GetWorkflowDescription()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Description))
        {
            parts.Add(Description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Issue) && !string.Equals(Issue, GetWorkflowTitle(), StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Issue: {Issue.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(SourceResolution))
        {
            parts.Add($"Source Resolution: {SourceResolution.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(SourceSolution))
        {
            parts.Add($"Source Solution: {SourceSolution.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(Application))
        {
            parts.Add($"Application: {Application.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(EventStatusName))
        {
            parts.Add($"Status: {EventStatusName.Trim()}");
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public int GetKnowledgeYear()
    {
        if (ClosedAt.HasValue)
        {
            return ClosedAt.Value.Year;
        }

        if (SubmittedAt.HasValue)
        {
            return SubmittedAt.Value.Year;
        }

        return DateTime.UtcNow.Year;
    }
}
