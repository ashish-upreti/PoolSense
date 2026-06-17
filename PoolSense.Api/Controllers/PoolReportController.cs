using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using System.Text.Json;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/pool")]
public sealed class PoolReportController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IProcessedSourceEventRepository _processedSourceEventRepository;
    private readonly ILLMService _llmService;

    public PoolReportController(IProcessedSourceEventRepository processedSourceEventRepository, ILLMService llmService)
    {
        _processedSourceEventRepository = processedSourceEventRepository;
        _llmService = llmService;
    }

    [HttpGet("reports")]
    public async Task<ActionResult<PoolRecommendationReportListResult>> GetReports(
        [FromQuery] string? projectId,
        [FromQuery] string? q,
        [FromQuery] bool? emailSent,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var reports = await _processedSourceEventRepository.GetRecommendationReportsAsync(new PoolRecommendationReportQuery
        {
            ProjectId = projectId ?? string.Empty,
            SearchTerm = q ?? string.Empty,
            EmailSent = emailSent,
            FromUtc = from,
            ToUtc = to,
            Page = page,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(reports);
    }

    [HttpGet("{sourceEventId}/report")]
    public async Task<ActionResult<PoolReportResponse>> GetReport(string sourceEventId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId))
        {
            return BadRequest("Pool number is required.");
        }

        var report = await _processedSourceEventRepository.GetLatestReportAsync(sourceEventId, cancellationToken);
        if (report?.WorkflowResult is null)
        {
            return NotFound($"No PoolSense report was found for pool {sourceEventId}.");
        }

        return Ok(new PoolReportResponse
        {
            SourceEventId = report.SourceEventId,
            ProcessingKind = report.ProcessingKind,
            ProcessedAt = report.ProcessedAt,
            EmailSent = report.EmailSent,
            EmailRecipient = report.EmailRecipient,
            WorkflowResult = report.WorkflowResult
        });
    }

    [HttpPost("{sourceEventId}/troubleshoot")]
    public async Task<ActionResult<PoolTroubleshootResponse>> Troubleshoot(
        string sourceEventId,
        [FromBody] PoolTroubleshootRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId))
        {
            return BadRequest("Pool number is required.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest("A troubleshooting question is required.");
        }

        var report = await _processedSourceEventRepository.GetLatestReportAsync(sourceEventId, cancellationToken);
        if (report?.WorkflowResult is null)
        {
            return NotFound($"No PoolSense report was found for pool {sourceEventId}.");
        }

        var answer = await _llmService.GetResponseAsync(BuildTroubleshootPrompt(report, request.Question.Trim()));

        return Ok(new PoolTroubleshootResponse
        {
            SourceEventId = report.SourceEventId,
            Question = request.Question.Trim(),
            Answer = answer.Trim(),
            GeneratedAt = DateTime.UtcNow
        });
    }

    private static string BuildTroubleshootPrompt(ProcessedSourceEventRecord report, string question)
    {
        var reportJson = JsonSerializer.Serialize(report.WorkflowResult, JsonOptions);

        return $$"""
            You are PoolSense, an operational troubleshooting assistant for engineering support pools.

            Answer the user's follow-up question using only the saved PoolSense report context below. The report came from an already processed NewRecommendation workflow.

            Pool Number: {{report.SourceEventId}}
            Processing Kind: {{report.ProcessingKind}}
            Processed At UTC: {{report.ProcessedAt:O}}
            Email Sent: {{report.EmailSent}}
            Email Recipients: {{report.EmailRecipient}}

            Saved PoolSense Report JSON:
            {{reportJson}}

            User Follow-Up Question:
            {{question}}

            Instructions:
            - Be specific to this pool and the saved report.
            - Start with the most practical next checks.
            - Use similar incidents only as supporting evidence; identify ticket IDs when helpful.
            - If asked for a checklist, provide an ordered checklist.
            - If asked for an update or escalation, write concise copy that can be pasted into a ticket/email.
            - If documentation would be needed to answer confidently, state exactly what documentation or data source should be checked next.
            - Do not invent systems, links, people, or documentation not present in the report.
            - Keep the answer concise and action-oriented.
            """;
    }
}

public sealed class PoolReportResponse
{
    public string SourceEventId { get; set; } = string.Empty;
    public string ProcessingKind { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public bool EmailSent { get; set; }
    public string EmailRecipient { get; set; } = string.Empty;
    public TicketWorkflowResult WorkflowResult { get; set; } = new();
}

public sealed class PoolTroubleshootRequest
{
    public string Question { get; set; } = string.Empty;
}

public sealed class PoolTroubleshootResponse
{
    public string SourceEventId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
}
