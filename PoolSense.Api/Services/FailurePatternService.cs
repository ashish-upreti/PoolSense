using System.Text.Json;
using PoolSense.Api.Agents;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Extracts structured failure patterns and provides aggregated insights.
/// </summary>
public interface IFailurePatternService
{
    /// <summary>
    /// Extracts a failure pattern from ticket details and stores the result.
    /// </summary>
    /// <param name="ticketId">The source ticket identifier.</param>
    /// <param name="problem">The ticket problem description.</param>
    /// <param name="rootCause">The identified root cause.</param>
    /// <param name="resolution">The resolution applied to the issue.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The stored failure pattern.</returns>
    Task<FailurePattern> ExtractAndStoreAsync(string ticketId, string problem, string rootCause, string resolution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves aggregated failure pattern insights.
    /// </summary>
    /// <param name="limit">The maximum number of items to return per aggregate.</param>
    /// <param name="minimumIncidentCount">The minimum incident count required for repeated systems.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The aggregated insight data.</returns>
    Task<FailurePatternInsights> GetInsightsAsync(int limit = 10, int minimumIncidentCount = 2, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents aggregated failure pattern insights used by the API.
/// </summary>
public sealed class FailurePatternInsights
{
    /// <summary>
    /// Gets or sets the most common failure types.
    /// </summary>
    public IReadOnlyList<FailureTypeFrequency> MostCommonFailureTypes { get; set; } = [];

    /// <summary>
    /// Gets or sets the most problematic components.
    /// </summary>
    public IReadOnlyList<ComponentFrequency> MostProblematicComponents { get; set; } = [];

    /// <summary>
    /// Gets or sets the systems with repeated incidents.
    /// </summary>
    public IReadOnlyList<SystemIncidentFrequency> SystemsWithRepeatedIncidents { get; set; } = [];
}

/// <summary>
/// Coordinates failure pattern extraction and insight retrieval.
/// </summary>
public class FailurePatternService : IFailurePatternService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IFailurePatternAgent _failurePatternAgent;
    private readonly IFailurePatternRepository _failurePatternRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="FailurePatternService"/> class.
    /// </summary>
    /// <param name="failurePatternAgent">The agent that extracts structured failure patterns.</param>
    /// <param name="failurePatternRepository">The repository used to persist and query failure patterns.</param>
    public FailurePatternService(IFailurePatternAgent failurePatternAgent, IFailurePatternRepository failurePatternRepository)
    {
        _failurePatternAgent = failurePatternAgent;
        _failurePatternRepository = failurePatternRepository;
    }

    /// <summary>
    /// Extracts a failure pattern from ticket details and stores the result.
    /// </summary>
    /// <param name="ticketId">The source ticket identifier.</param>
    /// <param name="problem">The ticket problem description.</param>
    /// <param name="rootCause">The identified root cause.</param>
    /// <param name="resolution">The resolution applied to the issue.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The stored failure pattern.</returns>
    public async Task<FailurePattern> ExtractAndStoreAsync(string ticketId, string problem, string rootCause, string resolution, CancellationToken cancellationToken = default)
    {
        var response = await _failurePatternAgent.ExtractFailurePatternAsync(problem, rootCause, resolution);
        var extractedPattern = JsonSerializer.Deserialize<FailurePatternExtractionResult>(AiJsonResponseSanitizer.Normalize(response), JsonOptions)
            ?? throw new InvalidOperationException("The failure pattern agent returned an empty result.");

        var failurePattern = new FailurePattern
        {
            TicketId = ticketId,
            System = extractedPattern.System,
            Component = extractedPattern.Component,
            FailureType = extractedPattern.FailureType,
            ResolutionCategory = extractedPattern.ResolutionCategory,
            CreatedAt = DateTime.UtcNow
        };

        await _failurePatternRepository.InsertFailurePattern(failurePattern, cancellationToken);
        return failurePattern;
    }

    /// <summary>
    /// Retrieves aggregated failure pattern insights.
    /// </summary>
    /// <param name="limit">The maximum number of items to return per aggregate.</param>
    /// <param name="minimumIncidentCount">The minimum incident count required for repeated systems.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The aggregated insight data.</returns>
    public async Task<FailurePatternInsights> GetInsightsAsync(int limit = 10, int minimumIncidentCount = 2, CancellationToken cancellationToken = default)
    {
        var mostCommonFailureTypes = await _failurePatternRepository.GetMostFrequentFailureTypes(limit, cancellationToken);
        var mostProblematicComponents = await _failurePatternRepository.GetMostProblematicComponents(limit, cancellationToken);
        var systemsWithRepeatedIncidents = await _failurePatternRepository.GetSystemsWithRepeatedIncidents(minimumIncidentCount, limit, cancellationToken);

        return new FailurePatternInsights
        {
            MostCommonFailureTypes = mostCommonFailureTypes,
            MostProblematicComponents = mostProblematicComponents,
            SystemsWithRepeatedIncidents = systemsWithRepeatedIncidents
        };
    }

    private sealed class FailurePatternExtractionResult
    {
        public string System { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string FailureType { get; set; } = string.Empty;
        public string ResolutionCategory { get; set; } = string.Empty;
    }
}
