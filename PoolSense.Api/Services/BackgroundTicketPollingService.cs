using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Connectors;
using PoolSense.Api.Data;
using PoolSense.Api.Orchestration;
using PoolSense.Application.Models;

namespace PoolSense.Api.Services;

public class BackgroundTicketPollingService : BackgroundService
{
    private const string ClosedKnowledgeKind = "ClosedKnowledge";
    private const string NewRecommendationKind = "NewRecommendation";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettingsMonitor;
    private readonly ILogger<BackgroundTicketPollingService> _logger;

    public BackgroundTicketPollingService(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<NyraSettings> nyraSettingsMonitor,
        ILogger<BackgroundTicketPollingService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _nyraSettingsMonitor = nyraSettingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await GetSettingsAsync(stoppingToken);
                var delay = TimeSpan.FromSeconds(Math.Max(10, settings.PollIntervalSeconds));

                if (!settings.PollingEnabled)
                {
                    _logger.LogInformation("Polling is paused (PollingEnabled=false). Waiting {DelaySeconds} seconds before the next check.", delay.TotalSeconds);
                }
                else if (!HasRequiredNyraSettings(_nyraSettingsMonitor.CurrentValue))
                {
                    _logger.LogWarning("Polling is skipped because NYRA settings are incomplete. Configure Nyra:ApiKey, Nyra:GatewayEndpoint, Nyra:Model, Nyra:EmbeddingModel, Nyra:EmbeddingApiVersion, and Nyra:EmbeddingGenerateUrl or Nyra:EmbeddingEndpoint.");
                }
                else
                {
                    await PollOnceAsync(stoppingToken);
                }

                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ticket polling iteration failed.");
            }
        }
    }

    private static bool HasRequiredNyraSettings(NyraSettings nyraSettings)
    {
        return !string.IsNullOrWhiteSpace(nyraSettings.ApiKey)
            && !string.IsNullOrWhiteSpace(nyraSettings.GatewayEndpoint)
            && !string.IsNullOrWhiteSpace(nyraSettings.Model)
            && !string.IsNullOrWhiteSpace(nyraSettings.EmbeddingModel)
            && (!string.IsNullOrWhiteSpace(nyraSettings.EmbeddingGenerateUrl)
                || !string.IsNullOrWhiteSpace(nyraSettings.EmbeddingEndpoint))
            && !string.IsNullOrWhiteSpace(nyraSettings.EmbeddingApiVersion);
    }

    private async Task<TicketAutomationSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<ITicketAutomationSettingsProvider>();
        return await settingsProvider.GetAsync(cancellationToken);
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        // Sync PoolSense flag from tbl_Application into project_configs before processing tickets.
        var applicationSyncService = scope.ServiceProvider.GetRequiredService<IApplicationSyncService>();
        await applicationSyncService.SyncApplicationsAsync(cancellationToken);

        var settingsProvider = scope.ServiceProvider.GetRequiredService<ITicketAutomationSettingsProvider>();
        var settings = await settingsProvider.GetAsync(cancellationToken);
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var connector = scope.ServiceProvider.GetRequiredService<SqlTicketConnector>();
        var processedSourceEventRepository = scope.ServiceProvider.GetRequiredService<IProcessedSourceEventRepository>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITicketWorkflowOrchestrator>();
        var emailService = scope.ServiceProvider.GetRequiredService<ITicketRecommendationEmailService>();

        var projects = (await projectRepository.GetAllProjectsAsync(cancellationToken))
            .Where(project => project.PoolingEnabled
                && project.TicketSourceType.Equals("sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("Polling iteration started. Projects: {ProjectCount} ({ProjectNames}).",
            projects.Count, string.Join(", ", projects.Select(project => project.ProjectName)));

        foreach (var project in projects)
        {
            var currentProject = await projectRepository.GetProjectByIdAsync(project.ProjectId, cancellationToken);
            if (currentProject is null)
            {
                _logger.LogInformation("Skipping polling for project '{ProjectId}' because it no longer exists.", project.ProjectId);
                continue;
            }

            if (!currentProject.PoolingEnabled)
            {
                _logger.LogInformation("Skipping polling for project '{ProjectName}' (projectId: {ProjectId}) because pooling is disabled.",
                    currentProject.ProjectName, currentProject.ProjectId);
                continue;
            }

            _logger.LogInformation("Polling project '{ProjectName}' (projectId: {ProjectId}, filter: {ApplicationFilter}).",
                currentProject.ProjectName, currentProject.ProjectId, currentProject.ApplicationFilter);

            var closedTickets = await connector.GetTicketsByStatusAsync(currentProject, settings.ClosedStatusName, cancellationToken);
            _logger.LogInformation("Project '{ProjectName}': fetched {ClosedCount} '{ClosedStatus}' tickets.", currentProject.ProjectName, closedTickets.Count, settings.ClosedStatusName);
            await ProcessClosedTicketsAsync(currentProject, closedTickets, processedSourceEventRepository, orchestrator, cancellationToken);

            currentProject = await projectRepository.GetProjectByIdAsync(project.ProjectId, cancellationToken);
            if (currentProject is null || !currentProject.PoolingEnabled)
            {
                _logger.LogInformation("Skipping new-ticket polling for project '{ProjectId}' because pooling is disabled or the project was removed.", project.ProjectId);
                continue;
            }

            var newTickets = await connector.GetTicketsByStatusAsync(currentProject, settings.NewStatusName, cancellationToken);
            _logger.LogInformation("Project '{ProjectName}': fetched {NewCount} '{NewStatus}' tickets.", currentProject.ProjectName, newTickets.Count, settings.NewStatusName);
            await ProcessNewTicketsAsync(currentProject, newTickets, processedSourceEventRepository, orchestrator, emailService, settings, cancellationToken);
        }

        _logger.LogInformation("Polling iteration completed.");
    }

    private async Task ProcessClosedTicketsAsync(
        Models.ProjectConfig project,
        IReadOnlyList<TicketRequest> tickets,
        IProcessedSourceEventRepository processedSourceEventRepository,
        ITicketWorkflowOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var skippedCount = 0;

        foreach (var ticket in tickets)
        {
            ApplyProjectScope(ticket, project);

            if (string.IsNullOrWhiteSpace(ticket.SourceEventId)
                || await processedSourceEventRepository.HasBeenProcessedAsync(ticket.SourceEventId, ClosedKnowledgeKind, cancellationToken))
            {
                skippedCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(ticket.GetWorkflowTitle()) || string.IsNullOrWhiteSpace(ticket.GetWorkflowDescription()))
            {
                _logger.LogWarning("Skipping closed ticket {TicketId} because workflow title/description is empty.", ticket.TicketId);
                skippedCount++;
                continue;
            }

            _logger.LogInformation("Processing closed ticket {TicketId} (sourceEventId: {SourceEventId}).", ticket.TicketId, ticket.SourceEventId);

            var workflowResult = await orchestrator.ProcessAsync(ticket, cancellationToken);

            await processedSourceEventRepository.MarkProcessedAsync(new ProcessedSourceEventRecord
            {
                SourceEventId = ticket.SourceEventId,
                ProcessingKind = ClosedKnowledgeKind,
                WorkflowResult = workflowResult,
                ProcessedAt = DateTime.UtcNow
            }, cancellationToken);

            processedCount++;
        }

        _logger.LogInformation("Closed ticket processing complete. Processed: {ProcessedCount}, Skipped: {SkippedCount}.", processedCount, skippedCount);
    }

    private async Task ProcessNewTicketsAsync(
        Models.ProjectConfig project,
        IReadOnlyList<TicketRequest> tickets,
        IProcessedSourceEventRepository processedSourceEventRepository,
        ITicketWorkflowOrchestrator orchestrator,
        ITicketRecommendationEmailService emailService,
        TicketAutomationSettings settings,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var skippedCount = 0;

        foreach (var ticket in tickets)
        {
            ApplyProjectScope(ticket, project);

            if (string.IsNullOrWhiteSpace(ticket.SourceEventId)
                || await processedSourceEventRepository.HasBeenProcessedAsync(ticket.SourceEventId, NewRecommendationKind, cancellationToken))
            {
                skippedCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(ticket.GetWorkflowTitle()) || string.IsNullOrWhiteSpace(ticket.GetWorkflowDescription()))
            {
                _logger.LogWarning("Skipping new ticket {TicketId} because workflow title/description is empty.", ticket.TicketId);
                skippedCount++;
                continue;
            }

            _logger.LogInformation("Processing new ticket {TicketId} (sourceEventId: {SourceEventId}).", ticket.TicketId, ticket.SourceEventId);

            var workflowResult = await orchestrator.RecommendAsync(ticket, cancellationToken);

            var emailSent = false;
            try
            {
                if (!settings.PoolSenseEmail)
                {
                    _logger.LogInformation("Email sending is disabled (PoolSenseEmail=false). Skipping email for ticket {TicketId}.", ticket.TicketId);
                }
                else if (project.SendEmail)
                {
                    emailSent = await emailService.SendRecommendationAsync(project, ticket, workflowResult, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Email sending is disabled (SendEmail=false). Skipping email for ticket {TicketId}.", ticket.TicketId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send recommendation email for ticket {TicketId}. Will retry on next poll.", ticket.TicketId);
                skippedCount++;
                continue;
            }

            await processedSourceEventRepository.MarkProcessedAsync(new ProcessedSourceEventRecord
            {
                SourceEventId = ticket.SourceEventId,
                ProcessingKind = NewRecommendationKind,
                WorkflowResult = workflowResult,
                ProcessedAt = DateTime.UtcNow,
                EmailSent = emailSent,
                EmailRecipient = emailService.GetConfiguredRecipients(project)
            }, cancellationToken);

            processedCount++;
            _logger.LogInformation("Completed new ticket {TicketId}. Recommendation email sent: {EmailSent}.", ticket.TicketId, emailSent);
        }

        _logger.LogInformation("New ticket processing complete. Processed: {ProcessedCount}, Skipped: {SkippedCount}.", processedCount, skippedCount);
    }

    private static void ApplyProjectScope(TicketRequest ticket, Models.ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(project);

        if (ticket.SelectedGroupIds is not { Count: > 0 } && !string.IsNullOrWhiteSpace(project.ProjectId))
        {
            ticket.SelectedGroupIds = [project.ProjectId];
        }

        if (project.SimilaritySearchLimit > 0)
        {
            ticket.SimilaritySearchLimitOverride = project.SimilaritySearchLimit;
        }
    }
}