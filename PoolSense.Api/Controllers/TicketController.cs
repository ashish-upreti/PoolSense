using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Agents;
using PoolSense.Application.Models;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for analyzing incoming support tickets.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TicketController : ControllerBase
{
    private readonly ITicketAnalyzerAgent _ticketAnalyzerAgent;

    /// <summary>
    /// Initializes a new instance of the <see cref="TicketController"/> class.
    /// </summary>
    /// <param name="ticketAnalyzerAgent">The agent that analyzes ticket content.</param>
    public TicketController(ITicketAnalyzerAgent ticketAnalyzerAgent)
    {
        _ticketAnalyzerAgent = ticketAnalyzerAgent;
    }

    /// <summary>
    /// Analyzes a ticket and returns structured JSON describing the issue.
    /// </summary>
    /// <param name="request">The ticket details to analyze.</param>
    /// <returns>A structured analysis result or an error response.</returns>
    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] TicketRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.GetWorkflowTitle()) || string.IsNullOrWhiteSpace(request.GetWorkflowDescription()))
        {
            return BadRequest("Ticket title or issue and ticket description or source details are required.");
        }

        try
        {
            var analysis = await _ticketAnalyzerAgent.AnalyzeTicketAsync(request.GetWorkflowTitle(), request.GetWorkflowDescription());
            return Content(analysis, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred during analysis: {ex.Message}");
        }
    }
}