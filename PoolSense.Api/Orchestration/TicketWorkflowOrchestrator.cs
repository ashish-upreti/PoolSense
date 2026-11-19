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
using PoolSense.Api.Services.CodeRAG;
using PoolSense.Application.Models;

namespace PoolSense.Api.Orchestration;

public interface ITicketWorkflowOrchestrator
{
    Task<TicketWorkflowResult> ProcessAsync(string title, string description, string? ticketId = null, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> ProcessAsync(TicketRequest request, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> RecommendForBackgroundPollingAsync(TicketRequest request, CancellationToken cancellationToken = default) =>
        RecommendAsync(request, cancellationToken);
    Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default);
    Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, TicketConversationContext context, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default);
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
    private readonly ICodeKnowledgeService _codeKnowledge;
    private readonly CodeFeedbackPolicy _codeFeedback;

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
        ILogger<TicketWorkflowOrchestrator> logger,
        ICodeKnowledgeService codeKnowledge,
        CodeFeedbackPolicy codeFeedback)
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
        _codeKnowledge = codeKnowledge;
        _codeFeedback = codeFeedback;
        _logger = logger;
    }

    public async Task<TicketWorkflowResult> ProcessAsync(string title, string description, string? ticketId = null, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(new TicketRequest
        {
            TicketId = ticketId ?? string.Empty,
            Title = title,
            Description = description
        }, persistKnowledge: true, progressCallback: null, cancellationToken);
    }

    public async Task<TicketWorkflowResult> ProcessAsync(TicketRequest request, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(request, persistKnowledge: true, progressCallback: null, cancellationToken);
    }

    public async Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(request, persistKnowledge: false, progressCallback: null, cancellationToken);
    }

    public Task<TicketWorkflowResult> RecommendForBackgroundPollingAsync(TicketRequest request, CancellationToken cancellationToken = default) =>
        ProcessInternalAsync(request, persistKnowledge: false, progressCallback: null, cancellationToken, backgroundPolling: true);

    public async Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default)
    {
        return await ProcessInternalAsync(request, persistKnowledge: false, progressCallback, cancellationToken);
    }

    public Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, TicketConversationContext context, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default) =>
        ProcessInternalAsync(request, false, progressCallback, cancellationToken, context);

    private async Task<TicketWorkflowResult> ProcessInternalAsync(TicketRequest request, bool persistKnowledge,
        Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken,
        TicketConversationContext? conversation = null, bool backgroundPolling = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var processingStopwatch = Stopwatch.StartNew();

        var workflowMode = persistKnowledge ? "Persist" : "Recommend";
        _logger.LogInformation(
            "Starting workflow mode {WorkflowMode} for ticket {TicketId} (sourceEventId: {SourceEventId}).",
            workflowMode,
            request.TicketId,
            request.SourceEventId);

        var title = request.GetWorkflowTitle();
        var description = request.GetWorkflowDescription();
        var context = persistKnowledge ? TicketConversationContext.Empty : conversation ?? TicketConversationContext.Empty;

        await ReportProgressAsync(progressCallback, "categorize", "Classifying request", "Determining requested sources and query intent.", "active", 1, cancellationToken);

        QueryCategorizationResult categorization;
        var sourcePlan = TicketSourceRouting.Explicit(title, description);
        var visualizationPlan = VisualizationPolicy.Fallback(title, description);
        if (persistKnowledge || !string.IsNullOrWhiteSpace(request.SourceEventId))
        {
            categorization = new() { Category = "Issue", Reasoning = "Background ticket processing uses incident retrieval." };
            sourcePlan = TicketSourceRouting.Background();
            visualizationPlan = VisualizationPlan.None;
        }
        else if (sourcePlan?.ClarificationMessage is not null)
            categorization = new() { Category = sourcePlan.Intent == ResolutionAnswerIntent.Explanation ? "Info" : "Issue", Reasoning = "Current explicit source instruction takes precedence over classification." };
        else
        {
            var fallback = false;
            try
            {
                var categoryJson = await _queryCategorizationAgent.CategorizeQueryAsync(title, description, context, cancellationToken);
                using var classified = JsonDocument.Parse(AiJsonResponseSanitizer.Normalize(categoryJson));
                var root = classified.RootElement;
                visualizationPlan = VisualizationPolicy.ReadPlan(root.TryGetProperty("visualizationPlan", out var rawPlan) ? rawPlan : null,
                    visualizationPlan, problem => _logger.LogWarning("Visualization planning {PlanProblem}; preserving safe deterministic visual intent.", problem));
                categorization = new()
                {
                    Category = root.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String ? category.GetString()! : "",
                    Reasoning = root.TryGetProperty("reasoning", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString()! : ""
                };
                if (!categorization.Category.Equals("Info", StringComparison.OrdinalIgnoreCase) && !categorization.Category.Equals("Issue", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsupported classification category.");
                categorization.Category = categorization.Category.Equals("Info", StringComparison.OrdinalIgnoreCase) ? "Info" : "Issue";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning("Query classification unavailable ({FailureType}); falling back to Issue/all-source retrieval.", exception.GetType().Name);
                categorization = new() { Category = "Issue", Reasoning = "Classification unavailable or invalid; explicitly falling back to Issue/all-source retrieval." };
                fallback = true;
            }
            if (sourcePlan is null)
                sourcePlan = TicketSourceRouting.Classified(categorization.Category, title, description, fallback);
            else
                categorization = new() { Category = sourcePlan.Intent == ResolutionAnswerIntent.Explanation ? "Info" : "Issue",
                    Reasoning = "Current explicit source instruction takes precedence; classification was used only for visualization planning." };
        }
        var usePool = sourcePlan.Pool;
        var intent = sourcePlan.Intent;
        _logger.LogInformation("Answer intent {Intent} for ticket {TicketId}; Pool retrieval enabled: {UsePool}.", intent, request.TicketId, usePool);
        await ReportProgressAsync(progressCallback, "categorize", "Classifying request", $"{categorization.Category}: {sourcePlan.Label} ({sourcePlan.Origin}). {categorization.Reasoning}", "completed", 1, cancellationToken);
        if (sourcePlan.ClarificationMessage is { } sourceClarification)
        {
            foreach (var (stage, order) in new[] { ("pool-data", 3), ("nyra-wiki", 4), ("code-repositories", 5) })
                await ReportProgressAsync(progressCallback, stage, "Source search skipped", "Conflicting source instructions need clarification; no source was searched.", "skipped", order, cancellationToken);
            return new TicketWorkflowResult
            {
                SuggestedResolution = sourceClarification,
                QueryCategory = categorization.Category,
                QueryCategorizationReasoning = categorization.Reasoning,
                SourcePlan = sourcePlan,
                VisualizationPlan = visualizationPlan,
                VisualizationStatus = visualizationPlan.Needed ? "unavailable" : "not-needed",
                VisualizationMessage = "Conflicting source instructions require clarification before visualization.",
                ProcessDiagramStatus = visualizationPlan.IsGraph ? "unavailable" : "",
                ProcessDiagramMessage = visualizationPlan.IsGraph ? sourceClarification : "",
                CodeKnowledgeStatus = "not-requested",
                ProcessingDurationMs = processingStopwatch.Elapsed.TotalMilliseconds
            };
        }

        await ReportProgressAsync(progressCallback, "analyze", "Analyzing issue", "Extracting problem details, symptoms, and keywords.", "active", 2, cancellationToken);
        _logger.LogInformation("Analyzing ticket {TicketId}.", request.TicketId);
        var analysisJson = context.HasTurns
            ? await _ticketAnalyzerAgent.AnalyzeTicketAsync(title, description, context)
            : await _ticketAnalyzerAgent.AnalyzeTicketAsync(title, description);
        var analysis = JsonSerializer.Deserialize<TicketAnalysisResult>(AiJsonResponseSanitizer.Normalize(analysisJson), JsonOptions)
            ?? throw new InvalidOperationException("The ticket analyzer returned an empty result.");
        await ReportProgressAsync(progressCallback, "analyze", "Analyzing issue", "Problem summary extracted for retrieval.", "completed", 2, cancellationToken);

        var searchText = string.IsNullOrWhiteSpace(analysis.Problem)
            ? $"Title: {title}{Environment.NewLine}Description: {description}"
            : analysis.Problem;
        if (context.HasTurns && analysis.RequiresClarification)
        {
            searchText = $"Title: {title}{Environment.NewLine}Description: {description}";
            analysis.Problem = searchText;
            analysis.RootCause = "";
            analysis.Resolution = "";
            analysis.Keywords = [];
        }
        var nyraScopeTask = sourcePlan.Wiki ? ResolveNyraRetrievalScopeAsync(request, cancellationToken)
            : Task.FromResult(new NyraRetrievalScope([], []));
        float[] searchEmbedding = [];
        CodeQueryEmbedding? reusableCodeEmbedding = null;
        Task<IReadOnlyList<TicketKnowledge>> similarTicketsTask;
        if (usePool)
        {
            await ReportProgressAsync(progressCallback, "pool-data", "Pulling pool data", "Generating an embedding and searching scoped incident history.", "active", 3, cancellationToken);
            var embeddingProfile = _embeddingService.Profile;
            searchEmbedding = await _embeddingService.GenerateEmbedding(searchText);
            reusableCodeEmbedding = embeddingProfile == _embeddingService.Profile ? new(searchText, embeddingProfile, searchEmbedding) : null;
            var limit = request.SimilaritySearchLimitOverride is > 0 ? request.SimilaritySearchLimitOverride.Value : _settings.SimilaritySearchLimit;
            similarTicketsTask = _vectorStore.SearchSimilarTickets(searchEmbedding, limit, request.SelectedGroupIds, cancellationToken);
        }
        else
        {
            await ReportProgressAsync(progressCallback, "pool-data", "Pool incident search", $"Skipped by {sourcePlan.Label}: no Pool embedding or historical search.", "skipped", 3, cancellationToken);
            similarTicketsTask = Task.FromResult<IReadOnlyList<TicketKnowledge>>([]);
        }

        var nyraScope = await nyraScopeTask;
        LogNyraRetrievalScope(request, nyraScope);
        await ReportProgressAsync(
            progressCallback,
            "nyra-wiki",
            "Querying NYRA Wiki",
            !sourcePlan.Wiki ? $"Wiki excluded by {sourcePlan.Label}." : nyraScope.KbNames.Count > 0 ? $"Searching {string.Join(", ", nyraScope.KbNames)}." : "No NYRA Wiki scope matched this request.",
            nyraScope.KbNames.Count > 0 ? "active" : "skipped",
            4,
            cancellationToken);
        var nyraRetrievalTask = sourcePlan.Wiki ? RetrieveNyraDocumentsAsync(searchText, nyraScope.KbNames, cancellationToken)
            : Task.FromResult(new NyraDocumentRetrievalOutcome([], ""));

        await ReportProgressAsync(progressCallback, "code-repositories", "Querying repository code", sourcePlan.Code ? "Checking GitHub access and indexed code." : $"Code excluded by {sourcePlan.Label}.", sourcePlan.Code && !persistKnowledge ? "active" : "skipped", 5, cancellationToken);
        var codeTask = persistKnowledge || !sourcePlan.Code
            ? Task.FromResult(sourcePlan.Code ? CodeRetrievalResult.Skipped : new CodeRetrievalResult("not-requested", []))
            : backgroundPolling
                ? _codeKnowledge.ForBackgroundPollingAsync(!usePool || context.HasTurns ? searchText : $"{title}\n{description}\n{searchText}", request, cancellationToken)
            : _codeKnowledge.ForRequestAsync(!usePool || context.HasTurns ? searchText : $"{title}\n{description}\n{searchText}", request, cancellationToken,
                reusableCodeEmbedding);

        // Fan in: wait for all three sources together; a failure still propagates with its original exception.
        await Task.WhenAll(similarTicketsTask, nyraRetrievalTask, codeTask);
        var similarTickets = similarTicketsTask.Result;
        var nyraRetrieval = nyraRetrievalTask.Result;
        var nyraDocuments = nyraRetrieval.Documents;
        var code = codeTask.Result;

        // Completion events are centralized after the fan-in in stable stage order (pool-data=3, nyra-wiki=4,
        // code-repositories=5). All progress writes stay on the orchestrator's logical thread, keeping the
        // NDJSON response single-writer.
        if (usePool) await ReportProgressAsync(progressCallback, "pool-data", "Pulling pool data", $"Found {similarTickets.Count} similar incident candidate(s).", "completed", 3, cancellationToken);

        await ReportProgressAsync(
            progressCallback,
            "nyra-wiki",
            "Querying NYRA Wiki",
            nyraScope.KbNames.Count > 0 ? $"Retrieved {nyraDocuments.Count} NYRA document(s)." : "NYRA Wiki was not used for this request.",
            nyraScope.KbNames.Count > 0 ? "completed" : "skipped",
            4,
            cancellationToken);

        await ReportProgressAsync(progressCallback, "code-repositories", "Querying repository code", sourcePlan.Code ? $"Code source: {code.Status}. {code.Documents.Count} excerpt(s)." : $"Skipped: code excluded by {sourcePlan.Label}.",
            code.Documents.Count > 0 ? "completed" : "skipped", 5, cancellationToken);

        _logger.LogInformation("Found {SimilarTicketCount} similar tickets for ticket {TicketId}.", similarTickets.Count, request.TicketId);
        _logger.LogInformation("Found {NyraDocumentCount} NYRA document(s) for ticket {TicketId}.", nyraDocuments.Count, request.TicketId);

        var resolutionIncidents = new List<ResolutionIncident>();
        if (usePool)
        {
            await ReportProgressAsync(progressCallback, "enrich", "Enriching context", "Applying feedback evidence and human-validated fixes.", "active", 5, cancellationToken);
            var feedbackEvidenceByTicketId = await _feedbackRepository.GetFeedbackEvidence(
                similarTickets.Select(ticket => ticket.TicketId).ToArray(),
                cancellationToken);

            var validatedResolutionsByTicketId = await _validatedResolutionRepository.GetByTicketIdsAsync(
                similarTickets.Select(ticket => ticket.TicketId).ToArray(),
                cancellationToken);

            resolutionIncidents = similarTickets
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
            await ReportProgressAsync(progressCallback, "enrich", "Enriching context", "Context package is ready for recommendation.", "completed", 5, cancellationToken);
        }
        else await ReportProgressAsync(progressCallback, "enrich", "Incident enrichment", $"Skipped by {sourcePlan.Label}: no historical feedback, validated-resolution or failure-pattern reads.", "skipped", 5, cancellationToken);

        await ReportProgressAsync(progressCallback, "prepare-results", "Preparing results", "Generating root cause, recommended fix, and reasoning.", "active", 6, cancellationToken);
        _logger.LogInformation("Generating resolution for ticket {TicketId}.", request.TicketId);
        ResolutionResult resolution;
        if (context.HasTurns && analysis.RequiresClarification)
        {
            var question = string.IsNullOrWhiteSpace(analysis.ClarificationQuestion)
                ? "Which earlier option or step are you referring to? Please quote it so I can investigate it against fresh evidence."
                : analysis.ClarificationQuestion[..Math.Min(analysis.ClarificationQuestion.Length, 1000)];
            resolution = new ResolutionResult
            {
                SuggestedResolution = question,
                Reasoning = "The follow-up reference is ambiguous; earlier assistant claims are not current evidence.",
                Confidence = 0
            };
        }
        else
        {
            var codeNotice = CodeRetrievalMessages.Notice(code);
            var resolutionDescription = $"{description}\n\nActual source plan (system metadata): {sourcePlan.Label}; Pool={sourcePlan.Pool}, Wiki={sourcePlan.Wiki}, code={sourcePlan.Code}. Do not imply excluded sources were searched.";
            if (codeNotice.Length > 0) resolutionDescription += $"\nCurrent code-search coverage (system metadata, not evidence): {codeNotice}";
            var resolutionJson = await _resolutionAgent.GenerateResolutionAsync(title, resolutionDescription, resolutionIncidents.Take(5).ToList(),
                nyraDocuments.Take(5).ToList(), code.Documents, context, searchText, intent, visualizationPlan, cancellationToken);
            resolution = JsonSerializer.Deserialize<ResolutionResult>(AiJsonResponseSanitizer.Normalize(resolutionJson), JsonOptions)
                ?? throw new InvalidOperationException("The resolution agent returned an empty result.");
        }

        var visualizationStatus = visualizationPlan.Needed ? "unavailable" : "not-needed";
        var visualizationMessage = visualizationPlan.Needed ? "The reference needs clarification before a visualization can be grounded." : "";
        var diagramStatus = visualizationPlan.IsGraph ? "unavailable" : "";
        var diagramMessage = visualizationPlan.IsGraph ? visualizationMessage : "";
        if (visualizationPlan.Needed && !analysis.RequiresClarification)
        {
            var sources = ProcessAnswerPolicy.Sources(resolutionIncidents.Take(5).ToList(), nyraDocuments.Take(5).ToList(), code.Documents);
            var graph = ProcessAnswerPolicy.Parse(resolution.ProcessGraph);
            var diagram = VisualizationPolicy.Build(visualizationPlan, resolution.ProcessGraph, resolution.VisualizationTable, resolution.VisualizationChart, sources);
            if (!diagram.Provided && sources.Count > 0 && _resolutionAgent is IProcessDiagramRepair repair)
            {
                var repairSources = ProcessAnswerPolicy.RepairSources(sources);
                try
                {
                    var repairQuestion = ProcessAnswerPolicy.Plain(title + "\n" + description, 1200);
                    var repairedJson = await repair.RepairVisualizationAsync(repairQuestion, visualizationPlan, repairSources, cancellationToken);
                    var repaired = JsonSerializer.Deserialize<ResolutionResult>(AiJsonResponseSanitizer.Normalize(repairedJson), JsonOptions);
                    resolution.ProcessGraph = repaired?.ProcessGraph;
                    resolution.VisualizationTable = repaired?.VisualizationTable;
                    resolution.VisualizationChart = repaired?.VisualizationChart;
                    graph = ProcessAnswerPolicy.Parse(resolution.ProcessGraph);
                    diagram = VisualizationPolicy.Build(visualizationPlan, resolution.ProcessGraph, resolution.VisualizationTable, resolution.VisualizationChart, repairSources);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _logger.LogWarning("Visualization repair unavailable ({FailureType}); an explicit unavailable message will be returned.", exception.GetType().Name);
                }
            }
            visualizationStatus = diagram.Provided ? diagram.Partial ? "partial" : "provided" : "unavailable";
            visualizationMessage = diagram.Message;
            if (visualizationPlan.IsGraph)
            {
                diagramStatus = diagram.Provided ? "provided" : "unavailable";
                diagramMessage = diagram.Message;
            }
            resolution.DetailedAnalysisMarkdown = ProcessAnswerPolicy.Attach(resolution.DetailedAnalysisMarkdown, diagram,
                visualizationPlan.Kind == "process" ? "Process flow" : "Visualization");
            if (!diagram.Provided)
            {
                resolution.SuggestedRootCause = VisualizationPolicy.RemoveUnverifiedClaims(resolution.SuggestedRootCause);
                resolution.SuggestedResolution = VisualizationPolicy.RemoveUnverifiedClaims(resolution.SuggestedResolution);
                resolution.Reasoning = VisualizationPolicy.RemoveUnverifiedClaims(resolution.Reasoning);
                resolution.SuggestedResolution = (resolution.SuggestedResolution + " Visualization unavailable. " + diagram.Message).Trim();
            }
            if (intent == ResolutionAnswerIntent.Explanation && diagram.Provided && visualizationPlan.IsGraph && graph is { } supportedGraph)
            {
                resolution.SuggestedRootCause = ProcessAnswerPolicy.Plain(supportedGraph.Title, 160);
                resolution.SuggestedResolution = string.IsNullOrWhiteSpace(supportedGraph.Summary)
                    ? "The supported process stages and transitions are shown below. " + diagram.Message
                    : ProcessAnswerPolicy.Plain(supportedGraph.Summary, 600);
                if (diagram.Partial) resolution.SuggestedResolution += " " + diagram.Message;
            }
        }

        if (code.Documents.Count > 0 || !persistKnowledge && (!usePool || intent == ResolutionAnswerIntent.Explanation))
        {
            await ReportProgressAsync(progressCallback, "prepare-results", "Preparing results", "Recommendation is ready.", "completed", 6, cancellationToken);
            return new TicketWorkflowResult
            {
                SuggestedRootCause = resolution.SuggestedRootCause,
                SuggestedResolution = resolution.SuggestedResolution,
                DetailedAnalysisMarkdown = resolution.DetailedAnalysisMarkdown,
                ProcessDiagramStatus = diagramStatus,
                ProcessDiagramMessage = diagramMessage,
                VisualizationPlan = visualizationPlan,
                VisualizationStatus = visualizationStatus,
                VisualizationMessage = visualizationMessage,
                Confidence = resolution.Confidence,
                Reasoning = resolution.Reasoning,
                CodeDocuments = code.Documents,
                CodeKnowledgeStatus = code.Status,
                CodeSearchCoverage = code.Coverage,
                SourcePlan = sourcePlan,
                FeedbackAuthorization = code.Documents.Count == 0 ? _codeFeedback.Permit(resolution.SuggestedResolution) : "",
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
                UsedPoolDatabase = usePool,
                ProcessingDurationMs = processingStopwatch.Elapsed.TotalMilliseconds
            };
        }

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

        await ReportProgressAsync(progressCallback, "prepare-results", "Preparing results", "Recommendation is ready.", "completed", 6, cancellationToken);

        _logger.LogInformation(
            "Completed workflow mode {WorkflowMode} for ticket {TicketId}. Similar incidents: {SimilarTicketCount}.",
            workflowMode,
            resolvedTicketId,
            similarTickets.Count);

        return new TicketWorkflowResult
        {
            SuggestedRootCause = resolution.SuggestedRootCause,
            SuggestedResolution = resolution.SuggestedResolution,
            DetailedAnalysisMarkdown = resolution.DetailedAnalysisMarkdown,
            ProcessDiagramStatus = diagramStatus,
            ProcessDiagramMessage = diagramMessage,
            VisualizationPlan = visualizationPlan,
            VisualizationStatus = visualizationStatus,
            VisualizationMessage = visualizationMessage,
            Confidence = resolution.Confidence,
            CodeKnowledgeStatus = code.Status,
            CodeSearchCoverage = code.Coverage,
            SourcePlan = sourcePlan,
            FeedbackAuthorization = _codeFeedback.Permit(resolution.SuggestedResolution),
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
            ProcessingDurationMs = processingStopwatch.Elapsed.TotalMilliseconds
        };
    }

    private static async Task ReportProgressAsync(
        Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback,
        string stage,
        string title,
        string detail,
        string state,
        int order,
        CancellationToken cancellationToken)
    {
        if (progressCallback is null)
        {
            return;
        }

        await progressCallback(new TicketWorkflowProgress
        {
            Stage = stage,
            Title = title,
            Detail = detail,
            State = state,
            Order = order,
            TimestampUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
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
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = string.Empty;
        public string Problem { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public string[] Keywords { get; set; } = [];
    }

    private sealed class ResolutionResult
    {
        public JsonElement? ProcessGraph { get; set; }
        public JsonElement? VisualizationTable { get; set; }
        public JsonElement? VisualizationChart { get; set; }
        public string SuggestedRootCause { get; set; } = string.Empty;
        public string SuggestedResolution { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Reasoning { get; set; } = string.Empty;
        public string DetailedAnalysisMarkdown { get; set; } = string.Empty;
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
