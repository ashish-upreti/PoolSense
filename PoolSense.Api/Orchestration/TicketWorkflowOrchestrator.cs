using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using PoolSense.Api.Feedback;
using PoolSense.Api.Logging;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using PoolSense.Api.Services.Nyra;
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
    private readonly IVectorStore _vectorStore;
    private readonly IncidentContextBuilder _incidentContextBuilder;
    private readonly IResolutionAgent _resolutionAgent;
    private readonly IQueryCategorizationAgent _queryCategorizationAgent;
    private readonly INyraDocumentRetrievalService _nyraDocumentRetrievalService;
    private readonly IProjectRepository _projectRepository;
    private readonly IKnowledgeEnrichmentService _knowledgeEnrichmentService;
    private readonly IFailurePatternAgent _failurePatternAgent;
    private readonly IFailurePatternRepository _failurePatternRepository;
    private readonly IFeedbackRepository _feedbackRepository;
    private readonly IValidatedResolutionRepository _validatedResolutionRepository;
    private readonly InteractionLogger _interactionLogger;
    private readonly ILogger<TicketWorkflowOrchestrator> _logger;
    private readonly TicketAutomationSettings _settings;

    public TicketWorkflowOrchestrator(
        ITicketAnalyzerAgent ticketAnalyzerAgent,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IncidentContextBuilder incidentContextBuilder,
        IResolutionAgent resolutionAgent,
        IQueryCategorizationAgent queryCategorizationAgent,
        INyraDocumentRetrievalService nyraDocumentRetrievalService,
        IProjectRepository projectRepository,
        IKnowledgeEnrichmentService knowledgeEnrichmentService,
        IFailurePatternAgent failurePatternAgent,
        IFailurePatternRepository failurePatternRepository,
        IFeedbackRepository feedbackRepository,
        IValidatedResolutionRepository validatedResolutionRepository,
        InteractionLogger interactionLogger,
        IOptions<TicketAutomationSettings> settings,
        ILogger<TicketWorkflowOrchestrator> logger)
    {
        _ticketAnalyzerAgent = ticketAnalyzerAgent;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _incidentContextBuilder = incidentContextBuilder;
        _resolutionAgent = resolutionAgent;
        _queryCategorizationAgent = queryCategorizationAgent;
        _nyraDocumentRetrievalService = nyraDocumentRetrievalService;
        _projectRepository = projectRepository;
        _knowledgeEnrichmentService = knowledgeEnrichmentService;
        _failurePatternAgent = failurePatternAgent;
        _failurePatternRepository = failurePatternRepository;
        _feedbackRepository = feedbackRepository;
        _validatedResolutionRepository = validatedResolutionRepository;
        _interactionLogger = interactionLogger;
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

        var processingStopwatch = Stopwatch.StartNew();

        var workflowMode = persistKnowledge ? "Persist" : "Recommend";
        _logger.LogInformation(
            "Starting workflow mode {WorkflowMode} for ticket {TicketId} (sourceEventId: {SourceEventId}).",
            workflowMode,
            request.TicketId,
            request.SourceEventId);

        var title = request.GetWorkflowTitle();
        var description = request.GetWorkflowDescription();

        _logger.LogInformation("Categorizing query for ticket {TicketId}.", request.TicketId);
        var categorizationJson = await _queryCategorizationAgent.CategorizeQueryAsync(title, description);
        var categorization = JsonSerializer.Deserialize<QueryCategorizationResult>(AiJsonResponseSanitizer.Normalize(categorizationJson), JsonOptions)
            ?? new QueryCategorizationResult { Category = "Issue" };
        _logger.LogInformation(
            "Query categorized as {QueryCategory} for ticket {TicketId}: {QueryCategorizationReasoning}",
            categorization.Category,
            request.TicketId,
            categorization.Reasoning);

        var isInfoOnlyQuery = !persistKnowledge && categorization.Category.Equals("Info", StringComparison.OrdinalIgnoreCase);
        if (isInfoOnlyQuery)
        {
            return await ProcessInfoQueryAsync(request, title, description, categorization, processingStopwatch, cancellationToken);
        }

        _logger.LogInformation("Analyzing ticket {TicketId}.", request.TicketId);
        var analysisJson = await _ticketAnalyzerAgent.AnalyzeTicketAsync(title, description);
        var analysis = JsonSerializer.Deserialize<TicketAnalysisResult>(AiJsonResponseSanitizer.Normalize(analysisJson), JsonOptions)
            ?? throw new InvalidOperationException("The ticket analyzer returned an empty result.");

        var searchText = string.IsNullOrWhiteSpace(analysis.Problem)
            ? $"Title: {title}{Environment.NewLine}Description: {description}"
            : analysis.Problem;
        _logger.LogInformation("Generating search embedding for ticket {TicketId}.", request.TicketId);
        var nyraScopeTask = ResolveNyraRetrievalScopeAsync(request, cancellationToken);
        var searchEmbedding = await _embeddingService.GenerateEmbedding(searchText);
        var similaritySearchLimit = request.SimilaritySearchLimitOverride is > 0
            ? request.SimilaritySearchLimitOverride.Value
            : _settings.SimilaritySearchLimit;
        var similarTicketsTask = _vectorStore.SearchSimilarTickets(searchEmbedding, similaritySearchLimit, request.SelectedGroupIds, cancellationToken);
        var nyraScope = await nyraScopeTask;
        LogNyraRetrievalScope(request, nyraScope);
        var nyraRetrievalTask = RetrieveNyraDocumentsAsync(searchText, nyraScope.KbNames, cancellationToken);

        var similarTickets = await similarTicketsTask;
        var nyraRetrieval = await nyraRetrievalTask;
        var nyraDocuments = nyraRetrieval.Documents;
        _logger.LogInformation("Found {SimilarTicketCount} similar tickets for ticket {TicketId}.", similarTickets.Count, request.TicketId);
        _logger.LogInformation("Found {NyraDocumentCount} NYRA document(s) for ticket {TicketId}.", nyraDocuments.Count, request.TicketId);

        var feedbackEvidenceByTicketId = await _feedbackRepository.GetFeedbackEvidence(
            similarTickets.Select(ticket => ticket.TicketId).ToArray(),
            cancellationToken);

        var validatedResolutionsByTicketId = await _validatedResolutionRepository.GetByTicketIdsAsync(
            similarTickets.Select(ticket => ticket.TicketId).ToArray(),
            cancellationToken);

        var resolutionIncidents = similarTickets
            .Select(ticket =>
            {
                feedbackEvidenceByTicketId.TryGetValue(ticket.TicketId, out var evidence);
                return new ResolutionIncident
                {
                    TicketId = ticket.TicketId,
                    Problem = ticket.Problem,
                    RootCause = ticket.RootCause,
                    Resolution = ticket.Resolution,
                    FeedbackScore = evidence?.Score ?? 0,
                    LatestHumanValidatedFix = GetLatestHumanValidatedFix(validatedResolutionsByTicketId, ticket.TicketId),
                    LatestHumanAvoidanceNote = GetLatestHumanAvoidanceNote(validatedResolutionsByTicketId, ticket.TicketId),
                    LatestHelpfulComment = evidence?.LatestHelpfulComment ?? string.Empty,
                    LatestNotHelpfulComment = evidence?.LatestNotHelpfulComment ?? string.Empty
                };
            })
            .ToList();

        _logger.LogInformation("Generating resolution for ticket {TicketId}.", request.TicketId);
        var resolutionJson = await _resolutionAgent.GenerateResolutionAsync(title, description, resolutionIncidents.Take(5).ToList(), nyraDocuments.Take(5).ToList());
        var resolution = JsonSerializer.Deserialize<ResolutionResult>(AiJsonResponseSanitizer.Normalize(resolutionJson), JsonOptions)
            ?? throw new InvalidOperationException("The resolution agent returned an empty result.");

        await _interactionLogger.LogInteractionAsync(
            searchText,
            similarTickets,
            resolution.SuggestedResolution,
            resolution.Confidence,
            processingStopwatch.Elapsed,
            searchEmbedding.Length,
            cancellationToken);

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
            await _vectorStore.InsertTicketKnowledge(enrichedKnowledge.TicketKnowledge, cancellationToken);
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
            NyraDocuments = nyraDocuments,
            NyraKnowledgeBaseUsed = nyraScope.KbNames.Count > 0,
            NyraKnowledgeBaseStatus = GetNyraKnowledgeBaseStatus(nyraScope, nyraRetrieval),
            NyraKnowledgeBaseMessage = GetNyraKnowledgeBaseMessage(nyraScope, nyraRetrieval),
            NyraKnowledgeBaseNames = nyraScope.KbNames,
            NyraKnowledgeBaseProjects = nyraScope.ProjectLabels,
            QueryCategory = categorization.Category,
            QueryCategorizationReasoning = categorization.Reasoning,
            UsedPoolDatabase = true,
            FailurePattern = failurePattern,
            Reasoning = resolution.Reasoning,
            FailurePatternFrequency = patternFrequency
        };
    }

    private async Task<TicketWorkflowResult> ProcessInfoQueryAsync(
        TicketRequest request,
        string title,
        string description,
        QueryCategorizationResult categorization,
        Stopwatch processingStopwatch,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skipping PoolSense database retrieval for ticket {TicketId}; query categorized as Info.", request.TicketId);

        var searchText = $"Title: {title}{Environment.NewLine}Description: {description}";
        var nyraScope = await ResolveNyraRetrievalScopeAsync(request, cancellationToken);
        LogNyraRetrievalScope(request, nyraScope);
        var nyraRetrieval = await RetrieveNyraDocumentsAsync(searchText, nyraScope.KbNames, cancellationToken);
        var nyraDocuments = nyraRetrieval.Documents;
        _logger.LogInformation("Found {NyraDocumentCount} NYRA document(s) for ticket {TicketId}.", nyraDocuments.Count, request.TicketId);

        var resolutionJson = await _resolutionAgent.GenerateResolutionAsync(title, description, [], nyraDocuments.Take(5).ToList());
        var resolution = JsonSerializer.Deserialize<ResolutionResult>(AiJsonResponseSanitizer.Normalize(resolutionJson), JsonOptions)
            ?? throw new InvalidOperationException("The resolution agent returned an empty result.");

        await _interactionLogger.LogInteractionAsync(
            searchText,
            [],
            resolution.SuggestedResolution,
            resolution.Confidence,
            processingStopwatch.Elapsed,
            0,
            cancellationToken);

        _logger.LogInformation("Completed workflow mode Recommend (Info) for ticket {TicketId}.", request.TicketId);

        return new TicketWorkflowResult
        {
            SuggestedRootCause = resolution.SuggestedRootCause,
            SuggestedResolution = resolution.SuggestedResolution,
            Confidence = resolution.Confidence,
            SimilarIncidents = [],
            NyraDocuments = nyraDocuments,
            NyraKnowledgeBaseUsed = nyraScope.KbNames.Count > 0,
            NyraKnowledgeBaseStatus = GetNyraKnowledgeBaseStatus(nyraScope, nyraRetrieval),
            NyraKnowledgeBaseMessage = GetNyraKnowledgeBaseMessage(nyraScope, nyraRetrieval),
            NyraKnowledgeBaseNames = nyraScope.KbNames,
            NyraKnowledgeBaseProjects = nyraScope.ProjectLabels,
            QueryCategory = categorization.Category,
            QueryCategorizationReasoning = categorization.Reasoning,
            UsedPoolDatabase = false,
            FailurePattern = new FailurePattern(),
            Reasoning = resolution.Reasoning,
            FailurePatternFrequency = 0
        };
    }

    private async Task<NyraDocumentRetrievalOutcome> RetrieveNyraDocumentsAsync(
        string searchText,
        IReadOnlyList<string> nyraKbNames,
        CancellationToken cancellationToken)
    {
        if (nyraKbNames.Count == 0)
        {
            return new NyraDocumentRetrievalOutcome([], string.Empty);
        }

        try
        {
            var documents = await _nyraDocumentRetrievalService.RetrieveHybridDocumentsAsync(
                searchText,
                nyraKbNames,
                limit: 5,
                cancellationToken: cancellationToken);
            return new NyraDocumentRetrievalOutcome(documents, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NYRA document retrieval failed. Continuing with PoolSense historical incidents only.");
            return new NyraDocumentRetrievalOutcome([], ex.Message);
        }
    }

    private static string GetNyraKnowledgeBaseStatus(NyraRetrievalScope scope, NyraDocumentRetrievalOutcome retrieval)
    {
        if (scope.KbNames.Count == 0)
        {
            return "Skipped";
        }

        return string.IsNullOrWhiteSpace(retrieval.ErrorMessage) ? "Queried" : "Failed";
    }

    private static string GetNyraKnowledgeBaseMessage(NyraRetrievalScope scope, NyraDocumentRetrievalOutcome retrieval)
    {
        if (scope.KbNames.Count == 0)
        {
            return "No NYRA KB names are configured for the selected project scope.";
        }

        if (!string.IsNullOrWhiteSpace(retrieval.ErrorMessage))
        {
            return retrieval.ErrorMessage;
        }

        return retrieval.Documents.Count == 0
            ? "NYRA KB was queried but returned no documents."
            : $"NYRA KB returned {retrieval.Documents.Count} document(s).";
    }

    private async Task<NyraRetrievalScope> ResolveNyraRetrievalScopeAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        var projects = await ResolveNyraProjectsAsync(request, cancellationToken);
        var kbNames = projects
            .SelectMany(project => SplitCommaSeparated(project.NyraKbNames))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projectLabels = projects
            .Select(project => string.IsNullOrWhiteSpace(project.ProjectName)
                ? project.ProjectId
                : $"{project.ProjectName} ({project.ProjectId})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new NyraRetrievalScope(projectLabels, kbNames);
    }

    private void LogNyraRetrievalScope(TicketRequest request, NyraRetrievalScope scope)
    {
        if (scope.KbNames.Count == 0)
        {
            _logger.LogInformation(
                "NYRA KB retrieval skipped for ticket {TicketId}. No NYRA KB names are configured for selected projects. Application: {Application}; ApplicationId: {ApplicationId}; SelectedGroupIds: {SelectedGroupIds}.",
                request.TicketId,
                request.Application,
                request.ApplicationId,
                string.Join(", ", request.SelectedGroupIds ?? []));
            return;
        }

        _logger.LogInformation(
            "NYRA KB retrieval enabled for ticket {TicketId}. Projects: {NyraProjects}. KBs: {NyraKbNames}.",
            request.TicketId,
            string.Join(", ", scope.ProjectLabels),
            string.Join(", ", scope.KbNames));
    }

    private async Task<IReadOnlyList<ProjectConfig>> ResolveNyraProjectsAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedGroupIds is { Count: > 0 })
        {
            var projectTasks = request.SelectedGroupIds
                .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(projectId => _projectRepository.GetProjectByIdAsync(projectId, cancellationToken));

            var selectedProjects = await Task.WhenAll(projectTasks);
            return selectedProjects
                .Where(project => project is not null && !string.IsNullOrWhiteSpace(project.NyraKbNames))
                .Cast<ProjectConfig>()
                .ToList();
        }

        var application = request.Application?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(application))
        {
            var project = await _projectRepository.GetProjectByApplicationFilterAsync(application, cancellationToken);
            return project is not null && !string.IsNullOrWhiteSpace(project.NyraKbNames)
                ? [project]
                : [];
        }

        var applicationId = request.ApplicationId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            var project = await _projectRepository.GetProjectByIdAsync(applicationId, cancellationToken);
            return project is not null && !string.IsNullOrWhiteSpace(project.NyraKbNames)
                ? [project]
                : [];
        }

        var projects = await _projectRepository.GetAllProjectsAsync(cancellationToken);
        return projects
            .Where(project => !string.IsNullOrWhiteSpace(project.NyraKbNames))
            .ToList();
    }

    private static IReadOnlyList<string> SplitCommaSeparated(string value) =>
        value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

    private sealed record NyraRetrievalScope(
        IReadOnlyList<string> ProjectLabels,
        IReadOnlyList<string> KbNames);

    private sealed record NyraDocumentRetrievalOutcome(
        IReadOnlyList<NyraDocumentResult> Documents,
        string ErrorMessage);

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

    private sealed class QueryCategorizationResult
    {
        public string Category { get; set; } = "Issue";
        public string Reasoning { get; set; } = string.Empty;
    }

    private static string GetLatestHumanValidatedFix(
        IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>> byTicketId,
        string ticketId)
    {
        if (!byTicketId.TryGetValue(ticketId, out var list))
            return string.Empty;

        // Prefer was_used=true (lifeguard confirmed they used this fix), fall back to any helpful note
        return list.FirstOrDefault(r => r.FeedbackType == 1 && r.WasUsed)?.ConfirmedNote
            ?? list.FirstOrDefault(r => r.FeedbackType == 1)?.ConfirmedNote
            ?? string.Empty;
    }

    private static string GetLatestHumanAvoidanceNote(
        IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>> byTicketId,
        string ticketId)
    {
        if (!byTicketId.TryGetValue(ticketId, out var list))
            return string.Empty;

        return list.FirstOrDefault(r => r.FeedbackType == -1)?.ConfirmedNote ?? string.Empty;
    }
}
