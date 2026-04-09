using PoolSense.Api.Agents;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Produces suggested resolutions using similar incidents and AI reasoning.
/// </summary>
public interface ITicketResolutionService
{
    /// <summary>
    /// Generates a suggested resolution for the provided ticket details.
    /// </summary>
    /// <param name="title">The ticket title.</param>
    /// <param name="description">The ticket description.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The suggested resolution text.</returns>
    Task<string> GetSuggestedResolutionAsync(string title, string description, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses similar incidents and an AI agent to produce ticket resolutions.
/// </summary>
public class TicketResolutionService : ITicketResolutionService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IPgVectorRepository _pgVectorRepository;
    private readonly IncidentContextBuilder _incidentContextBuilder;
    private readonly IResolutionAgent _resolutionAgent;

    /// <summary>
    /// Initializes a new instance of the <see cref="TicketResolutionService"/> class.
    /// </summary>
    /// <param name="embeddingService">The service that generates embeddings for the ticket.</param>
    /// <param name="pgVectorRepository">The repository used to find similar incidents.</param>
    /// <param name="incidentContextBuilder">The builder that formats historical incident context.</param>
    /// <param name="resolutionAgent">The agent that generates the final resolution suggestion.</param>
    public TicketResolutionService(
        IEmbeddingService embeddingService,
        IPgVectorRepository pgVectorRepository,
        IncidentContextBuilder incidentContextBuilder,
        IResolutionAgent resolutionAgent)
    {
        _embeddingService = embeddingService;
        _pgVectorRepository = pgVectorRepository;
        _incidentContextBuilder = incidentContextBuilder;
        _resolutionAgent = resolutionAgent;
    }

    /// <summary>
    /// Generates a suggested resolution for the provided ticket details.
    /// </summary>
    /// <param name="title">The ticket title.</param>
    /// <param name="description">The ticket description.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The suggested resolution text.</returns>
    public async Task<string> GetSuggestedResolutionAsync(string title, string description, CancellationToken cancellationToken = default)
    {
        var ticketText = $"Title: {title}{Environment.NewLine}Description: {description}";
        var embedding = await _embeddingService.GenerateEmbedding(ticketText);
        var similarTickets = await _pgVectorRepository.SearchSimilarTickets(embedding, 5, cancellationToken: cancellationToken);

        var incidentContext = _incidentContextBuilder.Build(similarTickets.ToList());

        var incidents = similarTickets
            .Select(ticket => new ResolutionIncident
            {
                Problem = ticket.Problem,
                RootCause = ticket.RootCause,
                Resolution = ticket.Resolution
            })
            .ToList();

        var enrichedDescription = $"{description}{Environment.NewLine}{Environment.NewLine}Historical Incident Context:{Environment.NewLine}{incidentContext}";

        return await _resolutionAgent.GenerateResolutionAsync(title, enrichedDescription, incidents);
    }
}
