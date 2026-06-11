using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Models;
using PoolSense.Api.Orchestration;
using PoolSense.Application.Models;
using System.Security.Authentication;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for running the ticket resolution workflow.
/// </summary>
[ApiController]
[Route("api/ticket")]
public class ResolutionController : ControllerBase
{
    private readonly ITicketWorkflowOrchestrator _ticketWorkflowOrchestrator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolutionController"/> class.
    /// </summary>
    /// <param name="ticketWorkflowOrchestrator">The orchestrator that processes tickets end to end.</param>
    public ResolutionController(ITicketWorkflowOrchestrator ticketWorkflowOrchestrator)
    {
        _ticketWorkflowOrchestrator = ticketWorkflowOrchestrator;
    }

    /// <summary>
    /// Processes a ticket through the full workflow and returns the suggested resolution.
    /// </summary>
    /// <param name="request">The ticket details to process.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The workflow result for the submitted ticket.</returns>
    [HttpPost("process")]
    public async Task<ActionResult<TicketWorkflowResult>> Post([FromBody] TicketRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.GetWorkflowTitle()) || string.IsNullOrWhiteSpace(request.GetWorkflowDescription()))
        {
            return BadRequest("Ticket title or issue and ticket description or source details are required.");
        }

        try
        {
            var result = await _ticketWorkflowOrchestrator.ProcessAsync(request, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while processing the ticket: {GetClientErrorMessage(ex)}");
        }
    }

    private static string GetClientErrorMessage(Exception exception)
    {
        var innermostException = GetInnermostException(exception);
        if (innermostException is AuthenticationException)
        {
            return $"The AI service TLS certificate could not be validated by this server. Inner error: {innermostException.Message}";
        }

        return exception.Message;
    }

    private static Exception GetInnermostException(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
