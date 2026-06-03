using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Feedback;
using PoolSense.Api.Data;
using System.Net.Mail;

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

        var selectedTicketId = request.SelectedTicketId?.Trim() ?? string.Empty;

        if (retrievedTicketIds.Length == 0)
        {
            return BadRequest("At least one retrieved ticket id is required.");
        }

        if (string.IsNullOrWhiteSpace(selectedTicketId))
        {
            return BadRequest("A selected ticket id is required.");
        }

        if (!retrievedTicketIds.Contains(selectedTicketId, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest("Selected ticket id must be one of the retrieved ticket ids.");
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
                TargetTicketId = selectedTicketId,
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

    /// <summary>
    /// Stores user feedback about an application experience and captures submitter details.
    /// </summary>
    [HttpPost("application")]
    public async Task<IActionResult> PostApplicationFeedback([FromBody] ApplicationFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.UserName)
            || string.IsNullOrWhiteSpace(request.UserEmail)
            || string.IsNullOrWhiteSpace(request.FeedbackType)
            || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest("UserName, UserEmail, FeedbackType, and Message are required.");
        }

        try
        {
            _ = new MailAddress(request.UserEmail.Trim());
        }
        catch (FormatException)
        {
            return BadRequest("UserEmail must be a valid email address.");
        }

        try
        {
            var feedback = new ApplicationFeedbackLog
            {
                UserName = request.UserName.Trim(),
                UserEmail = request.UserEmail.Trim(),
                FeedbackType = request.FeedbackType.Trim(),
                Message = request.Message.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            var id = await _feedbackRepository.AddApplicationFeedbackAsync(feedback, cancellationToken);
            return Ok(new { id });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while storing application feedback: {ex.Message}");
        }
    }
}
