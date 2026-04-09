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
    private readonly IOptionsMonitor<TicketAutomationSettings> _settingsMonitor;
    private readonly ILogger<BackgroundTicketPollingService> _logger;

    public BackgroundTicketPollingService(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<TicketAutomationSettings> settingsMonitor,
        ILogger<BackgroundTicketPollingService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settingsMonitor.CurrentValue;
            var delay = TimeSpan.FromSeconds(Math.Max(10, settings.PollIntervalSeconds));

            if (!settings.PollingEnabled)
            {
                _logger.LogInformation("Polling is paused (PollingEnabled=false). Waiting {DelaySeconds} seconds before the next check.", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                continue;
            }

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ticket polling iteration failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var settings = _settingsMonitor.CurrentValue;
        var connector = scope.ServiceProvider.GetRequiredService<SqlTicketConnector>();
        var processedSourceEventRepository = scope.ServiceProvider.GetRequiredService<IProcessedSourceEventRepository>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITicketWorkflowOrchestrator>();
        var emailService = scope.ServiceProvider.GetRequiredService<ITicketRecommendationEmailService>();

        // Determine which groups to poll: use configured ProjectGroups when available,
        // otherwise fall back to the legacy single ApplicationName.
        var groups = settings.ProjectGroups.Count > 0
            ? settings.ProjectGroups
            : [new ProjectGroupSettings { GroupId = "default", DisplayName = settings.ApplicationName, ApplicationFilter = settings.ApplicationName }];

        _logger.LogInformation("Polling iteration started. Groups: {GroupCount} ({GroupNames}).",
            groups.Count, string.Join(", ", groups.Select(g => g.DisplayName)));

        foreach (var group in groups)
        {
            _logger.LogInformation("Polling group '{GroupName}' (filter: {ApplicationFilter}).", group.DisplayName, group.ApplicationFilter);

            var closedTickets = await connector.GetTicketsByGroupAsync(group, settings.ClosedStatusName, cancellationToken);
            _logger.LogInformation("Group '{GroupName}': fetched {ClosedCount} '{ClosedStatus}' tickets.", group.DisplayName, closedTickets.Count, settings.ClosedStatusName);
            await ProcessClosedTicketsAsync(closedTickets, processedSourceEventRepository, orchestrator, cancellationToken);

            var newTickets = await connector.GetTicketsByGroupAsync(group, settings.NewStatusName, cancellationToken);
            _logger.LogInformation("Group '{GroupName}': fetched {NewCount} '{NewStatus}' tickets.", group.DisplayName, newTickets.Count, settings.NewStatusName);
            await ProcessNewTicketsAsync(newTickets, processedSourceEventRepository, orchestrator, emailService, settings.SendEmail, cancellationToken);
        }

        _logger.LogInformation("Polling iteration completed.");
    }

    private async Task ProcessClosedTicketsAsync(
        IReadOnlyList<TicketRequest> tickets,
        IProcessedSourceEventRepository processedSourceEventRepository,
        ITicketWorkflowOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var skippedCount = 0;

        foreach (var ticket in tickets)
        {
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
        IReadOnlyList<TicketRequest> tickets,
        IProcessedSourceEventRepository processedSourceEventRepository,
        ITicketWorkflowOrchestrator orchestrator,
        ITicketRecommendationEmailService emailService,
        bool sendEmail,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var skippedCount = 0;

        foreach (var ticket in tickets)
        {
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
                if (sendEmail)
                {
                    emailSent = await emailService.SendRecommendationAsync(ticket, workflowResult, cancellationToken);
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
                EmailRecipient = emailService.GetConfiguredRecipient()
            }, cancellationToken);

            processedCount++;
            _logger.LogInformation("Completed new ticket {TicketId}. Recommendation email sent: {EmailSent}.", ticket.TicketId, emailSent);
        }

        _logger.LogInformation("New ticket processing complete. Processed: {ProcessedCount}, Skipped: {SkippedCount}.", processedCount, skippedCount);
    }
}