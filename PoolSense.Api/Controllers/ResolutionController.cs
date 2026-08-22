using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Models;
using PoolSense.Api.Orchestration;
using PoolSense.Application.Models;
using System.Security.Authentication;
using System.Text.Json;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for running the ticket resolution workflow.
/// </summary>
[ApiController]
[Route("api/ticket")]
public class ResolutionController : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITicketWorkflowOrchestrator _ticketWorkflowOrchestrator;
    private readonly ILogger<ResolutionController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolutionController"/> class.
    /// </summary>
    /// <param name="ticketWorkflowOrchestrator">The orchestrator that processes tickets end to end.</param>
    /// <param name="logger">Logger for persisting errors to the database.</param>
    public ResolutionController(ITicketWorkflowOrchestrator ticketWorkflowOrchestrator, ILogger<ResolutionController> logger)
    {
        _ticketWorkflowOrchestrator = ticketWorkflowOrchestrator;
        _logger = logger;
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
            var result = await _ticketWorkflowOrchestrator.RecommendAsync(request, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ticket through workflow.");
            return StatusCode(500, $"An error occurred while processing the ticket: {GetClientErrorMessage(ex)}");
        }
    }

    /// <summary>
    /// Processes a ticket and streams workflow progress events before the final result.
    /// </summary>
    /// <param name="request">The ticket details to process.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>A newline-delimited JSON stream of progress events and the final result.</returns>
    [HttpPost("process-progress")]
    public async Task<IActionResult> PostWithProgress([FromBody] TicketRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.GetWorkflowTitle()) || string.IsNullOrWhiteSpace(request.GetWorkflowDescription()))
        {
            return BadRequest("Ticket title or issue and ticket description or source details are required.");
        }

        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        try
        {
            var result = await _ticketWorkflowOrchestrator.RecommendAsync(
                request,
                async (progress, token) => await WriteStreamEventAsync("progress", progress, token),
                cancellationToken);

            await WriteStreamEventAsync("result", result, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ticket through streaming workflow.");
            var message = $"An error occurred while processing the ticket: {GetClientErrorMessage(ex)}";

            if (!Response.HasStarted)
            {
                return StatusCode(500, message);
            }

            await WriteStreamEventAsync("error", new { message }, cancellationToken);
            return new EmptyResult();
        }
    }

    private async Task WriteStreamEventAsync(string type, object payload, CancellationToken cancellationToken)
    {
        var streamEvent = new Dictionary<string, object?>
        {
            ["type"] = type,
            [type] = payload
        };

        await JsonSerializer.SerializeAsync(Response.Body, streamEvent, StreamJsonOptions, cancellationToken);
        await Response.WriteAsync("\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
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
