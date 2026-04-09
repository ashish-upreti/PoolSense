using PoolSense.Api.Agents;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Enriches ticket knowledge before embedding and storage.
/// </summary>
public interface IKnowledgeEnrichmentService
{
    /// <summary>
    /// Enriches ticket knowledge with generated search variants and embedding text.
    /// </summary>
    /// <param name="ticketKnowledge">The ticket knowledge to enrich.</param>
    /// <returns>The enriched ticket knowledge payload.</returns>
    Task<EnrichedTicketKnowledge> EnrichAsync(TicketKnowledge ticketKnowledge);
}

/// <summary>
/// Represents the enriched data produced for a ticket before storage.
/// </summary>
public sealed class EnrichedTicketKnowledge
{
    /// <summary>
    /// Gets or sets the enriched ticket knowledge entity.
    /// </summary>
    public TicketKnowledge TicketKnowledge { get; set; } = new();

    /// <summary>
    /// Gets or sets the generated search query variants.
    /// </summary>
    public IReadOnlyList<string> QueryVariants { get; set; } = [];

    /// <summary>
    /// Gets or sets the text used to generate embeddings.
    /// </summary>
    public string EmbeddingText { get; set; } = string.Empty;
}

/// <summary>
/// Uses AI-generated query variants to enrich ticket knowledge.
/// </summary>
public class KnowledgeEnrichmentService : IKnowledgeEnrichmentService
{
    private readonly IQueryVariantGeneratorAgent _queryVariantGeneratorAgent;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeEnrichmentService"/> class.
    /// </summary>
    /// <param name="queryVariantGeneratorAgent">The agent that creates alternative search phrases.</param>
    public KnowledgeEnrichmentService(IQueryVariantGeneratorAgent queryVariantGeneratorAgent)
    {
        _queryVariantGeneratorAgent = queryVariantGeneratorAgent;
    }

    /// <summary>
    /// Enriches ticket knowledge with generated search variants and embedding text.
    /// </summary>
    /// <param name="ticketKnowledge">The ticket knowledge to enrich.</param>
    /// <returns>The enriched ticket knowledge payload.</returns>
    public async Task<EnrichedTicketKnowledge> EnrichAsync(TicketKnowledge ticketKnowledge)
    {
        ArgumentNullException.ThrowIfNull(ticketKnowledge);

        var queryVariants = await _queryVariantGeneratorAgent.GenerateQueryVariantsAsync(
            ticketKnowledge.Problem,
            ticketKnowledge.RootCause,
            ticketKnowledge.Resolution);

        ticketKnowledge.SearchVariants = queryVariants.ToList();

        var embeddingText = $"""
            Problem: {ticketKnowledge.Problem}
            Root Cause: {ticketKnowledge.RootCause}
            Resolution: {ticketKnowledge.Resolution}
            Search Variants: {string.Join(" | ", ticketKnowledge.SearchVariants)}
            """;

        return new EnrichedTicketKnowledge
        {
            TicketKnowledge = ticketKnowledge,
            QueryVariants = queryVariants,
            EmbeddingText = embeddingText
        };
    }
}
