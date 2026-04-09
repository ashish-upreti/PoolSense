using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using PoolSense.Application.Models;

namespace PoolSense.Api.Orchestration;

public interface ITicketWorkflowOrchestrator
{
    Task<TicketWorkflowResult> ProcessAsync(string title, string description, string? ticketId = null, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> ProcessAsync(TicketRequest request, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, CancellationToken cancellationToken = default);
}

public class TicketWorkflowOrchestrator : ITicketWorkflowOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITicketAnalyzerAgent _ticketAnalyzerAgent;
    private readonly IEmbeddingService _embeddingService;
    private readonly IPgVectorRepository _pgVectorRepository;
    private readonly IncidentContextBuilder _incidentContextBuilder;
    private readonly IResolutionAgent _resolutionAgent;
    private readonly IKnowledgeEnrichmentService _knowledgeEnrichmentService;
    private readonly IFailurePatternAgent _failurePatternAgent;
    private readonly IFailurePatternRepository _failurePatternRepository;
    private readonly ILogger<TicketWorkflowOrchestrator> _logger;
    private readonly TicketAutomationSettings _settings;

    public TicketWorkflowOrchestrator(
        ITicketAnalyzerAgent ticketAnalyzerAgent,
        IEmbeddingService embeddingService,
        IPgVectorRepository pgVectorRepository,
        IncidentContextBuilder incidentContextBuilder,
        IResolutionAgent resolutionAgent,
        IKnowledgeEnrichmentService knowledgeEnrichmentService,
        IFailurePatternAgent failurePatternAgent,
        IFailurePatternRepository failurePatternRepository,
        IOptions<TicketAutomationSettings> settings,
        ILogger<TicketWorkflowOrchestrator> logger)
    {
        _ticketAnalyzerAgent = ticketAnalyzerAgent;
        _embeddingService = embeddingService;
        _pgVectorRepository = pgVectorRepository;
        _incidentContextBuilder = incidentContextBuilder;
        _resolutionAgent = resolutionAgent;
        _knowledgeEnrichmentService = knowledgeEnrichmentService;
        _failurePatternAgent = failurePatternAgent;
        _failurePatternRepository = failurePatternRepository;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<TicketWorkflowResult> ProcessAsync(string title, string description, string? ticketId = null, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(new TicketRequest
        {
            TicketId = ticketId ?? string.Empty,
            Title = title,
            Description = description
        }, persistKnowledge: true, cancellationToken);
    }

    public async Task<TicketWorkflowResult> ProcessAsync(TicketRequest request, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(request, persistKnowledge: true, cancellationToken);
    }

    public async Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(request, persistKnowledge: false, cancellationToken);
    }

    private async Task<TicketWorkflowResult> ProcessInternalAsync(TicketRequest request, bool persistKnowledge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowMode = persistKnowledge ? "Persist" : "Recommend";
        _logger.LogInformation(
            "Starting workflow mode {WorkflowMode} for ticket {TicketId} (sourceEventId: {SourceEventId}).",
            workflowMode,
            request.TicketId,
            request.SourceEventId);

        var title = request.GetWorkflowTitle();
        var description = request.GetWorkflowDescription();

        _logger.LogInformation("Analyzing ticket {TicketId}.", request.TicketId);
        var analysisJson = await _ticketAnalyzerAgent.AnalyzeTicketAsync(title, description);
        var analysis = JsonSerializer.Deserialize<TicketAnalysisResult>(AiJsonResponseSanitizer.Normalize(analysisJson), JsonOptions)
            ?? throw new InvalidOperationException("The ticket analyzer returned an empty result.");

        var searchText = string.IsNullOrWhiteSpace(analysis.Problem)
            ? $"Title: {title}{Environment.NewLine}Description: {description}"
            : analysis.Problem;
        _logger.LogInformation("Generating search embedding for ticket {TicketId}.", request.TicketId);
        var searchEmbedding = await _embeddingService.GenerateEmbedding(searchText);
        var similarTickets = await _pgVectorRepository.SearchSimilarTickets(searchEmbedding, _settings.SimilaritySearchLimit, request.SelectedGroupIds, cancellationToken);
        _logger.LogInformation("Found {SimilarTicketCount} similar tickets for ticket {TicketId}.", similarTickets.Count, request.TicketId);

        var resolutionIncidents = similarTickets
            .Select(ticket => new ResolutionIncident
            {
                TicketId = ticket.TicketId,
                Problem = ticket.Problem,
                RootCause = ticket.RootCause,
                Resolution = ticket.Resolution
            })
            .ToList();

        _logger.LogInformation("Generating resolution for ticket {TicketId}.", request.TicketId);
        var resolutionJson = await _resolutionAgent.GenerateResolutionAsync(title, description, resolutionIncidents);
        var resolution = JsonSerializer.Deserialize<ResolutionResult>(AiJsonResponseSanitizer.Normalize(resolutionJson), JsonOptions)
            ?? throw new InvalidOperationException("The resolution agent returned an empty result.");

        var resolvedTicketId = string.IsNullOrWhiteSpace(request.TicketId)
            ? $"Issue-{Random.Shared.Next(10000, 99999)}"
            : request.TicketId;

        var ticketKnowledge = new TicketKnowledge
        {
            TicketId = resolvedTicketId,
            SourceEventId = request.SourceEventId,
            Problem = analysis.Problem,
            RootCause = string.IsNullOrWhiteSpace(resolution.SuggestedRootCause) ? analysis.RootCause : resolution.SuggestedRootCause,
            Resolution = string.IsNullOrWhiteSpace(resolution.SuggestedResolution) ? analysis.Resolution : resolution.SuggestedResolution,
            Keywords = analysis.Keywords ?? [],
            Application = request.Application,
            KnowledgeYear = request.GetKnowledgeYear(),
            SourceStatus = request.EventStatusName,
            SourceSubmittedAt = request.SubmittedAt,
            SourceClosedAt = request.ClosedAt,
            SubmitterId = request.SubmitterId,
            LifeguardId = request.LifeguardId,
            SourceProject = request.Project,
            CreatedAt = DateTime.UtcNow
        };

        var enrichedKnowledge = await _knowledgeEnrichmentService.EnrichAsync(ticketKnowledge);
        _logger.LogInformation("Generating storage embedding for ticket {TicketId}.", resolvedTicketId);
        enrichedKnowledge.TicketKnowledge.Embedding = await _embeddingService.GenerateEmbedding(enrichedKnowledge.EmbeddingText);

        var failurePatternJson = await _failurePatternAgent.ExtractFailurePatternAsync(
            enrichedKnowledge.TicketKnowledge.Problem,
            enrichedKnowledge.TicketKnowledge.RootCause,
            enrichedKnowledge.TicketKnowledge.Resolution);

        var failurePatternData = JsonSerializer.Deserialize<FailurePatternResult>(AiJsonResponseSanitizer.Normalize(failurePatternJson), JsonOptions)
            ?? throw new InvalidOperationException("The failure pattern agent returned an empty result.");

        var failurePattern = new FailurePattern
        {
            TicketId = resolvedTicketId,
            SourceEventId = request.SourceEventId,
            Application = request.Application,
            KnowledgeYear = request.GetKnowledgeYear(),
            System = failurePatternData.System,
            Component = failurePatternData.Component,
            FailureType = failurePatternData.FailureType,
            ResolutionCategory = failurePatternData.ResolutionCategory,
            CreatedAt = DateTime.UtcNow
        };

        if (persistKnowledge)
        {
            _logger.LogInformation("Persisting knowledge and failure pattern for ticket {TicketId}.", resolvedTicketId);
            await _pgVectorRepository.InsertTicketKnowledge(enrichedKnowledge.TicketKnowledge, cancellationToken);
            await _failurePatternRepository.InsertFailurePattern(failurePattern, cancellationToken);
        }

        var patternFrequency = await _failurePatternRepository.CountPatternOccurrences(
            failurePattern.System, failurePattern.FailureType, cancellationToken);

        _logger.LogInformation(
            "Completed workflow mode {WorkflowMode} for ticket {TicketId}. Similar incidents: {SimilarTicketCount}.",
            workflowMode,
            resolvedTicketId,
            similarTickets.Count);

        return new TicketWorkflowResult
        {
            SuggestedRootCause = resolution.SuggestedRootCause,
            SuggestedResolution = resolution.SuggestedResolution,
            Confidence = resolution.Confidence,
            SimilarIncidents = similarTickets.Select(ticket => new SimilarIncidentResult
            {
                TicketId = ticket.TicketId,
                Problem = ticket.Problem,
                RootCause = ticket.RootCause,
                Resolution = ticket.Resolution,
                Similarity = ticket.Similarity
            }).ToList(),
            FailurePattern = failurePattern,
            Reasoning = resolution.Reasoning,
            FailurePatternFrequency = patternFrequency
        };
    }

    private sealed class TicketAnalysisResult
    {
        public string Problem { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public string[] Keywords { get; set; } = [];
    }

    private sealed class ResolutionResult
    {
        public string SuggestedRootCause { get; set; } = string.Empty;
        public string SuggestedResolution { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Reasoning { get; set; } = string.Empty;
    }

    private sealed class FailurePatternResult
    {
        public string System { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string FailureType { get; set; } = string.Empty;
        public string ResolutionCategory { get; set; } = string.Empty;
    }
}
