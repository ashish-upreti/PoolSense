using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Searches stored ticket knowledge for similar incidents.
/// </summary>
public interface ISimilaritySearchService
{
    /// <summary>
    /// Searches for tickets similar to the provided text.
    /// </summary>
    /// <param name="text">The text used to search for similar incidents.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The similar tickets that were found.</returns>
    Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses embeddings and pgvector to search for similar ticket knowledge.
/// </summary>
public class SimilaritySearchService : ISimilaritySearchService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IPgVectorRepository _pgVectorRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimilaritySearchService"/> class.
    /// </summary>
    /// <param name="embeddingService">The service that generates search embeddings.</param>
    /// <param name="pgVectorRepository">The repository used to perform similarity search.</param>
    public SimilaritySearchService(IEmbeddingService embeddingService, IPgVectorRepository pgVectorRepository)
    {
        _embeddingService = embeddingService;
        _pgVectorRepository = pgVectorRepository;
    }

    /// <summary>
    /// Searches for tickets similar to the provided text.
    /// </summary>
    /// <param name="text">The text used to search for similar incidents.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The similar tickets that were found.</returns>
    public async Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var embedding = await _embeddingService.GenerateEmbedding(text);
        return await _pgVectorRepository.SearchSimilarTickets(embedding, 5, cancellationToken: cancellationToken);
    }
}