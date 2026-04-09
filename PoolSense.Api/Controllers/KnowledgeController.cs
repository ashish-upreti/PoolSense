using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Agents;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using PoolSense.Application.Models;
using System.Text.Json;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for storing and searching ticket knowledge.
/// </summary>
[ApiController]
[Route("api/ticket")]
public class KnowledgeController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITicketAnalyzerAgent _ticketAnalyzerAgent;
    private readonly IKnowledgeEnrichmentService _knowledgeEnrichmentService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ISimilaritySearchService _similaritySearchService;
    private readonly IPgVectorRepository _pgVectorRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeController"/> class.
    /// </summary>
    /// <param name="ticketAnalyzerAgent">The agent that produces structured ticket analysis.</param>
    /// <param name="knowledgeEnrichmentService">The service that enriches ticket knowledge before storage.</param>
    /// <param name="embeddingService">The service that generates embeddings for enriched knowledge.</param>
    /// <param name="similaritySearchService">The service that searches for similar tickets.</param>
    /// <param name="pgVectorRepository">The repository used to persist ticket knowledge.</param>
    public KnowledgeController(
        ITicketAnalyzerAgent ticketAnalyzerAgent,
        IKnowledgeEnrichmentService knowledgeEnrichmentService,
        IEmbeddingService embeddingService,
        ISimilaritySearchService similaritySearchService,
        IPgVectorRepository pgVectorRepository)
    {
        _ticketAnalyzerAgent = ticketAnalyzerAgent;
        _knowledgeEnrichmentService = knowledgeEnrichmentService;
        _embeddingService = embeddingService;
        _similaritySearchService = similaritySearchService;
        _pgVectorRepository = pgVectorRepository;
    }

    /// <summary>
    /// Analyzes, enriches, embeds, and stores ticket knowledge for future retrieval.
    /// </summary>
    /// <param name="request">The ticket details and resolution to store.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The stored ticket knowledge or an error response.</returns>
    [HttpPost("store")]
    public async Task<IActionResult> Store([FromBody] TicketRequest request, CancellationToken cancellationToken)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.GetWorkflowTitle())
            || string.IsNullOrWhiteSpace(request.GetWorkflowDescription())
            || string.IsNullOrWhiteSpace(request.Resolution))
        {
            return BadRequest("Ticket title or issue, ticket description or source details, and resolution are required.");
        }

        try
        {
            var analysis = await _ticketAnalyzerAgent.AnalyzeTicketAsync(request.GetWorkflowTitle(), request.GetWorkflowDescription());
            var structuredKnowledge = JsonSerializer.Deserialize<TicketAnalysisResult>(AiJsonResponseSanitizer.Normalize(analysis), JsonOptions);

            if (structuredKnowledge == null)
            {
                return StatusCode(500, "The analyzer returned an empty result.");
            }

            var ticketKnowledge = new TicketKnowledge
            {
                TicketId = string.IsNullOrWhiteSpace(request.TicketId) ? $"INC-{Random.Shared.Next(10000, 99999)}" : request.TicketId,
                SourceEventId = request.SourceEventId,
                Problem = structuredKnowledge.Problem,
                RootCause = structuredKnowledge.RootCause,
                Resolution = string.IsNullOrWhiteSpace(request.Resolution)
                    ? structuredKnowledge.Resolution
                    : request.Resolution,
                Keywords = structuredKnowledge.Keywords ?? [],
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

            enrichedKnowledge.TicketKnowledge.Embedding = await _embeddingService.GenerateEmbedding(enrichedKnowledge.EmbeddingText);
            await _pgVectorRepository.InsertTicketKnowledge(enrichedKnowledge.TicketKnowledge, cancellationToken);

            return Ok(enrichedKnowledge.TicketKnowledge);
        }
        catch (JsonException ex)
        {
            return StatusCode(500, $"Unable to parse analyzer output: {ex.Message}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while storing ticket knowledge: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for tickets that are similar to the provided ticket details.
    /// </summary>
    /// <param name="request">The ticket details used to search for similar incidents.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>A list of similar tickets or an error response.</returns>
    [HttpPost("similar")]
    public async Task<IActionResult> Similar([FromBody] TicketRequest request, CancellationToken cancellationToken)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.GetWorkflowTitle())
            || string.IsNullOrWhiteSpace(request.GetWorkflowDescription()))
        {
            return BadRequest("Ticket title or issue and ticket description or source details are required.");
        }

        try
        {
            var searchText = $"Title: {request.GetWorkflowTitle()}{Environment.NewLine}Description: {request.GetWorkflowDescription()}";
            var similarTickets = await _similaritySearchService.SearchSimilarTickets(searchText, cancellationToken);

            return Ok(new
            {
                similarTickets = similarTickets.Select(ticket => new
                {
                    ticketId = ticket.TicketId,
                    problem = ticket.Problem,
                    resolution = ticket.Resolution
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while searching for similar tickets: {ex.Message}");
        }
    }

    private sealed class TicketAnalysisResult
    {
        public string Problem { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public string[] Keywords { get; set; } = [];
    }
}