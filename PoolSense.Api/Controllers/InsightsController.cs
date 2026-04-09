using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Data;
using PoolSense.Api.Services;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for aggregated incident and failure pattern insights.
/// </summary>
[ApiController]
[Route("api/insights")]
public class InsightsController : ControllerBase
{
    private readonly IFailurePatternService _failurePatternService;
    private readonly IPgVectorRepository _pgVectorRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="InsightsController"/> class.
    /// </summary>
    /// <param name="failurePatternService">The service that supplies failure pattern aggregates.</param>
    /// <param name="pgVectorRepository">The repository that supplies incident timeline data.</param>
    public InsightsController(IFailurePatternService failurePatternService, IPgVectorRepository pgVectorRepository)
    {
        _failurePatternService = failurePatternService;
        _pgVectorRepository = pgVectorRepository;
    }

    /// <summary>
    /// Returns a combined insights payload containing failures, components, systems, and timeline data.
    /// </summary>
    /// <param name="limit">The maximum number of items to return per aggregate.</param>
    /// <param name="minimumIncidentCount">The minimum incident count required for repeated systems.</param>
    /// <param name="monthCount">The number of months to include in the timeline.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>A combined insights payload.</returns>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 10, [FromQuery] int minimumIncidentCount = 2, [FromQuery] int monthCount = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var insights = await _failurePatternService.GetInsightsAsync(limit, minimumIncidentCount, cancellationToken);
            var timeline = await _pgVectorRepository.GetIncidentTimeline(monthCount, cancellationToken);

            return Ok(new
            {
                topFailures = insights.MostCommonFailureTypes.Select(item => new
                {
                    failureType = item.FailureType,
                    occurrences = item.Count
                }),
                topComponents = insights.MostProblematicComponents.Select(item => new
                {
                    component = item.Component,
                    occurrences = item.Count
                }),
                repeatedSystems = insights.SystemsWithRepeatedIncidents.Select(item => new
                {
                    system = item.System,
                    occurrences = item.Count
                }),
                timeline = timeline.Select(item => new
                {
                    month = item.Month,
                    incidents = item.Incidents
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving insights: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the most frequent failure types.
    /// </summary>
    /// <param name="limit">The maximum number of failure types to return.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The most frequent failure types.</returns>
    [HttpGet("failures")]
    public async Task<IActionResult> GetFailures([FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        try
        {
            var insights = await _failurePatternService.GetInsightsAsync(limit, cancellationToken: cancellationToken);

            return Ok(new
            {
                topFailures = insights.MostCommonFailureTypes.Select(item => new
                {
                    failureType = item.FailureType,
                    occurrences = item.Count
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving failure insights: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the components with the highest incident counts.
    /// </summary>
    /// <param name="limit">The maximum number of components to return.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The most problematic components.</returns>
    [HttpGet("components")]
    public async Task<IActionResult> GetComponents([FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        try
        {
            var insights = await _failurePatternService.GetInsightsAsync(limit, cancellationToken: cancellationToken);

            return Ok(new
            {
                topComponents = insights.MostProblematicComponents.Select(item => new
                {
                    component = item.Component,
                    occurrences = item.Count
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving component insights: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns systems that have repeated incidents above the specified threshold.
    /// </summary>
    /// <param name="limit">The maximum number of systems to return.</param>
    /// <param name="minimumIncidentCount">The minimum incident count required to include a system.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The systems with repeated incidents.</returns>
    [HttpGet("systems")]
    public async Task<IActionResult> GetSystems([FromQuery] int limit = 10, [FromQuery] int minimumIncidentCount = 2, CancellationToken cancellationToken = default)
    {
        try
        {
            var insights = await _failurePatternService.GetInsightsAsync(limit, minimumIncidentCount, cancellationToken);

            return Ok(new
            {
                repeatedSystems = insights.SystemsWithRepeatedIncidents.Select(item => new
                {
                    system = item.System,
                    occurrences = item.Count
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving system insights: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the incident timeline for the requested month range.
    /// </summary>
    /// <param name="monthCount">The number of months to include in the timeline.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The incident timeline.</returns>
    [HttpGet("timeline")]
    public async Task<IActionResult> GetTimeline([FromQuery] int monthCount = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var timeline = await _pgVectorRepository.GetIncidentTimeline(monthCount, cancellationToken);

            return Ok(new
            {
                timeline = timeline.Select(item => new
                {
                    month = item.Month,
                    incidents = item.Incidents
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving the incident timeline: {ex.Message}");
        }
    }
}
