using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Connectors;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/ingestion")]
public class IngestionController : ControllerBase
{
    private const string ClosedKnowledgeKind = "ClosedKnowledge";

    private readonly IIngestionStatusRepository _ingestionStatusRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IProcessedSourceEventRepository _processedSourceEventRepository;
    private readonly SqlTicketConnector _sqlTicketConnector;
    private readonly TicketAutomationSettings _settings;

    public IngestionController(
        IIngestionStatusRepository ingestionStatusRepository,
        IProjectRepository projectRepository,
        IProcessedSourceEventRepository processedSourceEventRepository,
        SqlTicketConnector sqlTicketConnector,
        IOptions<TicketAutomationSettings> settings)
    {
        _ingestionStatusRepository = ingestionStatusRepository;
        _projectRepository = projectRepository;
        _processedSourceEventRepository = processedSourceEventRepository;
        _sqlTicketConnector = sqlTicketConnector;
        _settings = settings.Value;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetAllStatus([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var projects = await _projectRepository.GetAllProjectsAsync(cancellationToken);
            var statuses = await GetProjectStatusesAsync(projects, refresh, cancellationToken);
            var statusByProjectId = statuses.ToDictionary(status => status.ProjectId, StringComparer.OrdinalIgnoreCase);

            var projectIds = projects
                .Select(project => project.ProjectId)
                .Concat(statuses.Select(status => status.ProjectId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(projectIds.Select(projectId => ToResponse(statusByProjectId.GetValueOrDefault(projectId) ?? new IngestionStatus
            {
                ProjectId = projectId,
                TotalTickets = 0,
                IngestedTickets = 0,
                LastUpdated = DateTime.UtcNow
            })));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving ingestion statuses: {ex.Message}");
        }
    }

    [HttpGet("status/{projectId}")]
    public async Task<IActionResult> GetStatus(string projectId, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _projectRepository.GetProjectByIdAsync(projectId, cancellationToken);
            var status = await _ingestionStatusRepository.GetStatusAsync(projectId, cancellationToken);

            if (project is null && status is null)
            {
                return NotFound($"Project '{projectId}' was not found.");
            }

            if (project is not null)
            {
                status = await EnsureProjectStatusAsync(project, status, refresh, cancellationToken);
            }

            return Ok(ToResponse(status ?? new IngestionStatus
            {
                ProjectId = projectId,
                TotalTickets = 0,
                IngestedTickets = 0,
                LastUpdated = project?.CreatedAt ?? DateTime.UtcNow
            }));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving ingestion status for '{projectId}': {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<IngestionStatus>> GetProjectStatusesAsync(
        IReadOnlyList<ProjectConfig> projects,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var statuses = await _ingestionStatusRepository.GetAllStatusAsync(cancellationToken);
        var statusByProjectId = statuses.ToDictionary(status => status.ProjectId, StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var existingStatus = statusByProjectId.GetValueOrDefault(project.ProjectId);
            var refreshedStatus = await EnsureProjectStatusAsync(project, existingStatus, refresh, cancellationToken);

            if (refreshedStatus is not null)
            {
                statusByProjectId[project.ProjectId] = refreshedStatus;
            }
        }

        return statusByProjectId.Values
            .OrderBy(status => status.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IngestionStatus?> EnsureProjectStatusAsync(
        ProjectConfig project,
        IngestionStatus? currentStatus,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!project.PoolingEnabled
            || !project.TicketSourceType.Equals("sql", StringComparison.OrdinalIgnoreCase)
            || (!refresh && currentStatus is not null))
        {
            return currentStatus;
        }

        var closedTickets = await _sqlTicketConnector.GetTicketsByStatusAsync(project, _settings.ClosedStatusName, cancellationToken);
        var totalTickets = closedTickets.Count;
        var processedSourceEventIds = closedTickets
            .Select(ticket => ticket.SourceEventId)
            .Where(sourceEventId => !string.IsNullOrWhiteSpace(sourceEventId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ingestedTickets = await _processedSourceEventRepository.CountProcessedAsync(
            processedSourceEventIds,
            ClosedKnowledgeKind,
            cancellationToken);

        await _ingestionStatusRepository.RefreshStatusAsync(project.ProjectId, totalTickets, ingestedTickets, cancellationToken);

        return new IngestionStatus
        {
            Id = currentStatus?.Id ?? 0,
            ProjectId = project.ProjectId,
            TotalTickets = totalTickets,
            IngestedTickets = ingestedTickets,
            LastUpdated = DateTime.UtcNow
        };
    }

    private static object ToResponse(IngestionStatus status)
    {
        var total = Math.Max(0, status.TotalTickets);
        var ingested = Math.Min(Math.Max(0, status.IngestedTickets), total == 0 ? Math.Max(0, status.IngestedTickets) : total);
        var progressPercentage = total == 0
            ? 0
            : (int)Math.Round((double)ingested / total * 100, MidpointRounding.AwayFromZero);

        return new
        {
            projectId = status.ProjectId,
            ingested,
            total,
            progressPercentage
        };
    }
}