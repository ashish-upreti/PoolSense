using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Feedback;
using PoolSense.Api.Data;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Captures user feedback on AI-generated resolutions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackRepository _feedbackRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeedbackController"/> class.
    /// </summary>
    /// <param name="feedbackRepository">The repository used to store feedback entries.</param>
    public FeedbackController(IFeedbackRepository feedbackRepository)
    {
        _feedbackRepository = feedbackRepository;
    }

    /// <summary>
    /// Stores user feedback for a suggested resolution and its retrieved tickets.
    /// </summary>
    /// <param name="request">The feedback payload to persist.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The identifier of the stored feedback entry.</returns>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] FeedbackRequest request, CancellationToken cancellationToken)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.Query)
            || string.IsNullOrWhiteSpace(request.SuggestedResolution))
        {
            return BadRequest("Query and suggested resolution are required.");
        }

        if (request.FeedbackType is not 1 and not -1)
        {
            return BadRequest("Feedback type must be 1 for helpful or -1 for not helpful.");
        }

        var retrievedTicketIds = request.RetrievedTicketIds
            .Where(ticketId => !string.IsNullOrWhiteSpace(ticketId))
            .Select(ticketId => ticketId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (retrievedTicketIds.Length == 0)
        {
            return BadRequest("At least one retrieved ticket id is required.");
        }

        try
        {
            var feedback = new FeedbackLog
            {
                TicketQuery = request.Query.Trim(),
                SuggestedResolution = request.SuggestedResolution.Trim(),
                FeedbackType = request.FeedbackType,
                WasUsed = request.WasUsed,
                Comment = string.IsNullOrWhiteSpace(request.Comment) ? string.Empty : request.Comment.Trim(),
                RetrievedTicketIds = string.Join(',', retrievedTicketIds),
                CreatedAt = DateTime.UtcNow
            };

            var id = await _feedbackRepository.AddAsync(feedback, cancellationToken);
            return Ok(new { id });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while storing feedback: {ex.Message}");
        }
    }
}
