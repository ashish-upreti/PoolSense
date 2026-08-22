using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Services.Nyra;

namespace PoolSense.Api.Services;

public sealed class PoolTroubleshootEvidence
{
    public IReadOnlyList<TicketKnowledge> SimilarIncidents { get; init; } = [];
    public IReadOnlyList<NyraDocumentResult> NyraDocuments { get; init; } = [];
    public bool NyraKnowledgeBaseUsed { get; init; }
    public IReadOnlyList<string> NyraKnowledgeBaseNames { get; init; } = [];
}

public interface IPoolTroubleshootEvidenceService
{
    Task<PoolTroubleshootEvidence> RetrieveAsync(
        string query,
        string application,
        string projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Retrieves fresh Pool DB similar incidents and NYRA KB documents for a saved pool report follow-up
/// question, so troubleshooting answers are not limited to the evidence captured at the original processing time.
/// </summary>
public sealed class PoolTroubleshootEvidenceService : IPoolTroubleshootEvidenceService
{
    private const int EvidenceLimit = 5;

    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly INyraDocumentRetrievalService _nyraDocumentRetrievalService;
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger<PoolTroubleshootEvidenceService> _logger;

    public PoolTroubleshootEvidenceService(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        INyraDocumentRetrievalService nyraDocumentRetrievalService,
        IProjectRepository projectRepository,
        ILogger<PoolTroubleshootEvidenceService> logger)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _nyraDocumentRetrievalService = nyraDocumentRetrievalService;
        _projectRepository = projectRepository;
        _logger = logger;
    }

    public async Task<PoolTroubleshootEvidence> RetrieveAsync(
        string query,
        string application,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new PoolTroubleshootEvidence();
        }

        var project = await ResolveProjectAsync(application, projectId, cancellationToken);
        var scopedProjectIds = string.IsNullOrWhiteSpace(project?.ProjectId) ? null : new List<string> { project.ProjectId };

        var embedding = await _embeddingService.GenerateEmbedding(query);
        var similarIncidentsTask = _vectorStore.SearchSimilarTickets(embedding, EvidenceLimit, scopedProjectIds, cancellationToken);

        var kbNames = SplitCommaSeparated(project?.NyraKbNames ?? string.Empty);
        var nyraDocuments = kbNames.Count == 0
            ? []
            : await RetrieveNyraDocumentsAsync(query, kbNames, cancellationToken);

        var similarIncidents = await similarIncidentsTask;

        return new PoolTroubleshootEvidence
        {
            SimilarIncidents = similarIncidents,
            NyraDocuments = nyraDocuments,
            NyraKnowledgeBaseUsed = kbNames.Count > 0,
            NyraKnowledgeBaseNames = kbNames
        };
    }

    private async Task<ProjectConfig?> ResolveProjectAsync(string application, string projectId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var byId = await _projectRepository.GetProjectByIdAsync(projectId, cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        return string.IsNullOrWhiteSpace(application)
            ? null
            : await _projectRepository.GetProjectByApplicationFilterAsync(application, cancellationToken);
    }

    private async Task<IReadOnlyList<NyraDocumentResult>> RetrieveNyraDocumentsAsync(
        string query,
        IReadOnlyList<string> kbNames,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _nyraDocumentRetrievalService.RetrieveHybridDocumentsAsync(
                query,
                kbNames,
                limit: EvidenceLimit,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NYRA document retrieval failed during pool troubleshoot follow-up.");
            return [];
        }
    }

    private static IReadOnlyList<string> SplitCommaSeparated(string value) =>
        value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
