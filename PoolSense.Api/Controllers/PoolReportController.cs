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
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProcessedSourceEventRepository _processedSourceEventRepository;
    private readonly ILLMService _llmService;
    private readonly IPoolTroubleshootEvidenceService _evidenceService;
    private readonly ILogger<PoolReportController> _logger;

    public PoolReportController(
        IProcessedSourceEventRepository processedSourceEventRepository,
        ILLMService llmService,
        IPoolTroubleshootEvidenceService evidenceService,
        ILogger<PoolReportController> logger)
    {
        _processedSourceEventRepository = processedSourceEventRepository;
        _llmService = llmService;
        _evidenceService = evidenceService;
        _logger = logger;
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

        var report = await _processedSourceEventRepository.GetLatestRecordAsync(sourceEventId, cancellationToken);
        if (report is null)
        {
            return Ok(PoolReportResponse.NotReady(
                sourceEventId.Trim(),
                PoolReportStatus.Pending,
                "PoolSense is still preparing the recommendation for this pool. You can wait here and retry, or come back in a few minutes."));
        }

        if (report.WorkflowResult is null)
        {
            return Ok(PoolReportResponse.NotReady(
                report.SourceEventId,
                PoolReportStatus.Processing,
                "PoolSense has started processing this pool, but the recommendation report is not ready yet. Please retry shortly.",
                report));
        }

        return Ok(new PoolReportResponse
        {
            SourceEventId = report.SourceEventId,
            Status = PoolReportStatus.Ready,
            IsReady = true,
            Message = "PoolSense recommendation report is ready.",
            RetryAfterSeconds = 0,
            ProcessingKind = report.ProcessingKind,
            ProcessedAt = report.ProcessedAt,
            EmailSent = report.EmailSent,
            EmailRecipient = report.EmailRecipient,
            ProjectId = report.ProjectId,
            ProjectName = report.ProjectName,
            Application = report.Application,
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

        return Ok(await CreateTroubleshootResponseAsync(report, request.Question.Trim(), cancellationToken));
    }

    [HttpPost("{sourceEventId}/troubleshoot-progress")]
    public async Task<IActionResult> TroubleshootWithProgress(
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

        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        try
        {
            await WriteStreamEventAsync("progress", CreateProgress(
                "pool-report",
                "Loading saved report",
                $"Finding the latest PoolSense report for pool {sourceEventId}.",
                "active",
                1), cancellationToken);

            var report = await _processedSourceEventRepository.GetLatestReportAsync(sourceEventId, cancellationToken);
            if (report?.WorkflowResult is null)
            {
                await WriteStreamEventAsync("error", new { message = $"No PoolSense report was found for pool {sourceEventId}." }, cancellationToken);
                return new EmptyResult();
            }

            await WriteStreamEventAsync("progress", CreateProgress(
                "pool-report",
                "Loading saved report",
                "Saved report context is ready.",
                "completed",
                1), cancellationToken);

            var response = await CreateTroubleshootResponseAsync(
                report,
                request.Question.Trim(),
                cancellationToken,
                async (progress, token) => await WriteStreamEventAsync("progress", progress, token));

            await WriteStreamEventAsync("result", response, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error troubleshooting pool {SourceEventId} through streaming workflow.", sourceEventId);
            var message = $"An error occurred while troubleshooting pool {sourceEventId}: {ex.Message}";

            if (!Response.HasStarted)
            {
                return StatusCode(500, message);
            }

            await WriteStreamEventAsync("error", new { message }, cancellationToken);
            return new EmptyResult();
        }
    }

    private async Task<PoolTroubleshootResponse> CreateTroubleshootResponseAsync(
        ProcessedSourceEventRecord report,
        string question,
        CancellationToken cancellationToken,
        Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback = null)
    {
        await ReportProgressAsync(
            progressCallback,
            "fresh-evidence",
            "Retrieving fresh evidence",
            "Searching recent incidents and configured NYRA Wiki knowledge bases for this follow-up.",
            "active",
            2,
            cancellationToken);

        var evidence = await _evidenceService.RetrieveAsync(question, report.Application, report.ProjectId, cancellationToken);

        await ReportProgressAsync(
            progressCallback,
            "fresh-evidence",
            "Retrieving fresh evidence",
            $"Found {evidence.SimilarIncidents.Count} incident(s) and {evidence.NyraDocuments.Count} NYRA document(s).",
            "completed",
            2,
            cancellationToken);

        await ReportProgressAsync(
            progressCallback,
            "troubleshoot-answer",
            "Preparing answer",
            "Combining saved report context with fresh evidence.",
            "active",
            3,
            cancellationToken);

        var answer = await _llmService.GetResponseAsync(BuildTroubleshootPrompt(report, question, evidence));

        await ReportProgressAsync(
            progressCallback,
            "troubleshoot-answer",
            "Preparing answer",
            "Troubleshooting answer is ready.",
            "completed",
            3,
            cancellationToken);

        return new PoolTroubleshootResponse
        {
            SourceEventId = report.SourceEventId,
            Question = question,
            Answer = answer.Trim(),
            GeneratedAt = DateTime.UtcNow,
            RetrievedSimilarIncidentCount = evidence.SimilarIncidents.Count,
            RetrievedNyraDocumentCount = evidence.NyraDocuments.Count,
            NyraKnowledgeBaseUsed = evidence.NyraKnowledgeBaseUsed,
            NyraKnowledgeBaseNames = evidence.NyraKnowledgeBaseNames,
            NyraDocuments = evidence.NyraDocuments
        };
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

    private static async Task ReportProgressAsync(
        Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback,
        string stage,
        string title,
        string detail,
        string state,
        int order,
        CancellationToken cancellationToken)
    {
        if (progressCallback is null)
        {
            return;
        }

        await progressCallback(CreateProgress(stage, title, detail, state, order), cancellationToken);
    }

    private static TicketWorkflowProgress CreateProgress(string stage, string title, string detail, string state, int order)
    {
        return new TicketWorkflowProgress
        {
            Stage = stage,
            Title = title,
            Detail = detail,
            State = state,
            Order = order,
            TimestampUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildTroubleshootPrompt(ProcessedSourceEventRecord report, string question, PoolTroubleshootEvidence evidence)
    {
        var reportJson = JsonSerializer.Serialize(report.WorkflowResult, JsonOptions);
        var freshIncidentsJson = JsonSerializer.Serialize(evidence.SimilarIncidents.Select(incident => new
        {
            incident.TicketId,
            incident.Problem,
            incident.RootCause,
            incident.Resolution,
            incident.Similarity
        }), JsonOptions);
        var freshNyraDocumentsJson = JsonSerializer.Serialize(evidence.NyraDocuments.Select(document => new
        {
            document.KbName,
            document.Title,
            document.Content,
            document.SourceUrl,
            document.Citation,
            document.Score
        }), JsonOptions);

        return $$"""
            You are PoolSense, an operational troubleshooting assistant for engineering support pools.

            Answer the user's follow-up question using the saved PoolSense report context below, plus any fresh evidence retrieved specifically for this follow-up question.

            Pool Number: {{report.SourceEventId}}
            Processing Kind: {{report.ProcessingKind}}
            Processed At UTC: {{report.ProcessedAt:O}}
            Email Sent: {{report.EmailSent}}
            Email Recipients: {{report.EmailRecipient}}

            Saved PoolSense Report JSON:
            {{reportJson}}

            Fresh Similar Incidents Retrieved For This Follow-Up (Pool DB, most similar first; may be empty):
            {{freshIncidentsJson}}

            Fresh NYRA KB Documents Retrieved For This Follow-Up (wiki, most relevant first; may be empty):
            {{freshNyraDocumentsJson}}

            User Follow-Up Question:
            {{question}}

            Instructions:
            - Treat the saved report as the primary source of truth for this pool.
            - Use the fresh similar incidents and NYRA KB documents above when they add relevant detail not already in the saved report, or when the question needs a fresh lookup (e.g. "what does the wiki say about X", "any other similar tickets").
            - Cite ticket IDs from fresh similar incidents and titles/citations from fresh NYRA documents when you use them.
            - Be specific to this pool and the saved report.
            - Start with the most practical next checks.
            - If asked for a checklist, provide an ordered checklist.
            - If asked for an update or escalation, write concise copy that can be pasted into a ticket/email.
            - If documentation would still be needed beyond what is provided above, state exactly what documentation or data source should be checked next.
            - Do not invent systems, links, people, or documentation not present in the report or fresh evidence.
            - Keep the answer concise and action-oriented.
            """;
    }
}

public sealed class PoolReportResponse
{
    private const int DefaultRetryAfterSeconds = 30;

    public string SourceEventId { get; set; } = string.Empty;
    public string Status { get; set; } = PoolReportStatus.Pending;
    public bool IsReady { get; set; }
    public string Message { get; set; } = string.Empty;
    public int RetryAfterSeconds { get; set; }
    public string ProcessingKind { get; set; } = string.Empty;
    public DateTime? ProcessedAt { get; set; }
    public bool EmailSent { get; set; }
    public string EmailRecipient { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public TicketWorkflowResult? WorkflowResult { get; set; }

    public static PoolReportResponse NotReady(string sourceEventId, string status, string message, ProcessedSourceEventRecord? record = null)
    {
        return new PoolReportResponse
        {
            SourceEventId = sourceEventId,
            Status = status,
            IsReady = false,
            Message = message,
            RetryAfterSeconds = DefaultRetryAfterSeconds,
            ProcessingKind = record?.ProcessingKind ?? string.Empty,
            ProcessedAt = record?.ProcessedAt,
            EmailSent = record?.EmailSent ?? false,
            EmailRecipient = record?.EmailRecipient ?? string.Empty,
            ProjectId = record?.ProjectId ?? string.Empty,
            ProjectName = record?.ProjectName ?? string.Empty,
            Application = record?.Application ?? string.Empty,
            WorkflowResult = null
        };
    }
}

public static class PoolReportStatus
{
    public const string Ready = "Ready";
    public const string Processing = "Processing";
    public const string Pending = "Pending";
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
    public int RetrievedSimilarIncidentCount { get; set; }
    public int RetrievedNyraDocumentCount { get; set; }
    public bool NyraKnowledgeBaseUsed { get; set; }
    public IReadOnlyList<string> NyraKnowledgeBaseNames { get; set; } = [];
    public IReadOnlyList<NyraDocumentResult> NyraDocuments { get; set; } = [];
}
