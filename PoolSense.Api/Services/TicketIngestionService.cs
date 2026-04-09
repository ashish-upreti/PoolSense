using PoolSense.Api.Connectors;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Orchestration;

namespace PoolSense.Api.Services;

/// <summary>
/// Ingests tickets from registered project sources and runs them through the workflow.
/// </summary>
public interface ITicketIngestionService
{
    /// <summary>
    /// Ingests tickets from active projects.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The ingestion results grouped by project.</returns>
    Task<IReadOnlyList<ProjectTicketIngestionResult>> IngestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the ingestion results for a single project.
/// </summary>
public sealed class ProjectTicketIngestionResult
{
    /// <summary>
    /// Gets or sets the project identifier.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project name.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of tickets pulled from the source.
    /// </summary>
    public int TicketsPulled { get; set; }

    /// <summary>
    /// Gets or sets the processed ticket results.
    /// </summary>
    public IReadOnlyList<IngestedTicketResult> ProcessedTickets { get; set; } = [];
}

/// <summary>
/// Represents the workflow result for an ingested ticket.
/// </summary>
public sealed class IngestedTicketResult
{
    /// <summary>
    /// Gets or sets the ticket identifier.
    /// </summary>
    public string TicketId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the workflow result for the ingested ticket.
    /// </summary>
    public TicketWorkflowResult Result { get; set; } = new();
}

/// <summary>
/// Coordinates ticket ingestion from external sources and workflow processing.
/// </summary>
public class TicketIngestionService : ITicketIngestionService
{
    private readonly IProjectRepository _projectRepository;
    private readonly SqlTicketConnector _sqlTicketConnector;
    private readonly ApiTicketConnector _apiTicketConnector;
    private readonly ITicketWorkflowOrchestrator _ticketWorkflowOrchestrator;
    private readonly ILogger<TicketIngestionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TicketIngestionService"/> class.
    /// </summary>
    /// <param name="projectRepository">The repository used to load active projects.</param>
    /// <param name="sqlTicketConnector">The connector for SQL-backed ticket sources.</param>
    /// <param name="apiTicketConnector">The connector for API-backed ticket sources.</param>
    /// <param name="ticketWorkflowOrchestrator">The orchestrator that processes ingested tickets.</param>
    public TicketIngestionService(
        IProjectRepository projectRepository,
        SqlTicketConnector sqlTicketConnector,
        ApiTicketConnector apiTicketConnector,
        ITicketWorkflowOrchestrator ticketWorkflowOrchestrator,
        ILogger<TicketIngestionService> logger)
    {
        _projectRepository = projectRepository;
        _sqlTicketConnector = sqlTicketConnector;
        _apiTicketConnector = apiTicketConnector;
        _ticketWorkflowOrchestrator = ticketWorkflowOrchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Ingests tickets from active projects.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The ingestion results grouped by project.</returns>
    public async Task<IReadOnlyList<ProjectTicketIngestionResult>> IngestAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting ingestion run.");
        var activeProjects = await _projectRepository.ListActiveProjects(cancellationToken);
        var results = new List<ProjectTicketIngestionResult>();

        _logger.LogInformation("Found {ActiveProjectCount} active projects for ingestion.", activeProjects.Count);

        foreach (var project in activeProjects)
        {
            _logger.LogInformation("Pulling new tickets for project {ProjectId} ({ProjectName}).", project.ProjectId, project.ProjectName);
            var connector = ResolveConnector(project);
            var newTickets = await connector.GetNewTickets(project, cancellationToken);
            var processedTickets = new List<IngestedTicketResult>();

            _logger.LogInformation("Project {ProjectId} returned {TicketCount} ticket(s).", project.ProjectId, newTickets.Count);

            foreach (var ticket in newTickets)
            {
                var detailedTicket = string.IsNullOrWhiteSpace(ticket.TicketId)
                    ? ticket
                    : await connector.GetTicketDetails(project, ticket.TicketId, cancellationToken) ?? ticket;

                if (string.IsNullOrWhiteSpace(detailedTicket.Title) || string.IsNullOrWhiteSpace(detailedTicket.Description))
                {
                    _logger.LogWarning("Skipping ticket {TicketId} in project {ProjectId} because title/description is missing.", detailedTicket.TicketId, project.ProjectId);
                    continue;
                }

                _logger.LogInformation("Processing ticket {TicketId} for project {ProjectId}.", detailedTicket.TicketId, project.ProjectId);

                var workflowResult = await _ticketWorkflowOrchestrator.ProcessAsync(
                    detailedTicket,
                    cancellationToken);

                processedTickets.Add(new IngestedTicketResult
                {
                    TicketId = detailedTicket.TicketId,
                    Result = workflowResult
                });
            }

            _logger.LogInformation(
                "Completed project {ProjectId}: pulled {PulledCount}, processed {ProcessedCount}.",
                project.ProjectId,
                newTickets.Count,
                processedTickets.Count);

            results.Add(new ProjectTicketIngestionResult
            {
                ProjectId = project.ProjectId,
                ProjectName = project.ProjectName,
                TicketsPulled = newTickets.Count,
                ProcessedTickets = processedTickets
            });
        }

        _logger.LogInformation("Ingestion run completed for {ProjectCount} project(s).", results.Count);

        return results;
    }

    private ITicketSourceConnector ResolveConnector(ProjectConfig project)
    {
        if (project.TicketSourceType.Equals("sql", StringComparison.OrdinalIgnoreCase))
        {
            return _sqlTicketConnector;
        }

        if (project.TicketSourceType.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return _apiTicketConnector;
        }

        throw new InvalidOperationException($"Unsupported ticket source type '{project.TicketSourceType}' for project '{project.ProjectId}'.");
    }
}
