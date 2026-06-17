using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;
using System.Text.Json;

namespace PoolSense.Api.Data;

public interface IProcessedSourceEventRepository
{
    Task<bool> HasBeenProcessedAsync(string sourceEventId, string processingKind, CancellationToken cancellationToken = default);
    Task<int> CountProcessedAsync(IReadOnlyCollection<string> sourceEventIds, string processingKind, CancellationToken cancellationToken = default);
    Task<ProcessedSourceEventRecord?> GetLatestReportAsync(string sourceEventId, CancellationToken cancellationToken = default);
    Task<PoolRecommendationReportListResult> GetRecommendationReportsAsync(PoolRecommendationReportQuery query, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(ProcessedSourceEventRecord record, CancellationToken cancellationToken = default);
}

public sealed class ProcessedSourceEventRecord
{
    public string SourceEventId { get; set; } = string.Empty;
    public string ProcessingKind { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    public bool EmailSent { get; set; }
    public string EmailRecipient { get; set; } = string.Empty;
    public TicketWorkflowResult? WorkflowResult { get; set; }
}

public sealed class PoolRecommendationReportQuery
{
    public string ProjectId { get; set; } = string.Empty;
    public string SearchTerm { get; set; } = string.Empty;
    public bool? EmailSent { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed class PoolRecommendationReportListResult
{
    public IReadOnlyList<PoolRecommendationReportListItem> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class PoolRecommendationReportListItem
{
    public string SourceEventId { get; set; } = string.Empty;
    public string ProcessingKind { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public bool EmailSent { get; set; }
    public string EmailRecipient { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int SimilarIncidentCount { get; set; }
    public string FailureType { get; set; } = string.Empty;
    public string ResolutionCategory { get; set; } = string.Empty;
    public string ReportUrl { get; set; } = string.Empty;
}

public class ProcessedSourceEventRepository : IProcessedSourceEventRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public ProcessedSourceEventRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> HasBeenProcessedAsync(string sourceEventId, string processingKind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId) || string.IsNullOrWhiteSpace(processingKind))
        {
            return false;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1) 1
            FROM dbo.processed_source_events
            WHERE source_event_id = @sourceEventId
              AND processing_kind = @processingKind;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceEventId", sourceEventId);
        command.Parameters.AddWithValue("@processingKind", processingKind);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    public async Task<int> CountProcessedAsync(IReadOnlyCollection<string> sourceEventIds, string processingKind, CancellationToken cancellationToken = default)
    {
        var normalizedSourceEventIds = sourceEventIds
            .Where(sourceEventId => !string.IsNullOrWhiteSpace(sourceEventId))
            .Select(sourceEventId => sourceEventId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedSourceEventIds.Length == 0 || string.IsNullOrWhiteSpace(processingKind))
        {
            return 0;
        }

        if (normalizedSourceEventIds.Length > 1000)
        {
            var processedCount = 0;
            foreach (var sourceEventIdChunk in normalizedSourceEventIds.Chunk(1000))
            {
                processedCount += await CountProcessedAsync(sourceEventIdChunk, processingKind, cancellationToken);
            }

            return processedCount;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var sourceEventIdParameters = normalizedSourceEventIds
            .Select((_, index) => $"@sourceEventId{index}")
            .ToArray();

        var sql = $$"""
            SELECT COUNT(*)
            FROM dbo.processed_source_events
            WHERE processing_kind = @processingKind
              AND source_event_id IN ({{string.Join(", ", sourceEventIdParameters)}});
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@processingKind", processingKind);
        for (var index = 0; index < normalizedSourceEventIds.Length; index++)
        {
            command.Parameters.AddWithValue(sourceEventIdParameters[index], normalizedSourceEventIds[index]);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<ProcessedSourceEventRecord?> GetLatestReportAsync(string sourceEventId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId))
        {
            return null;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1)
                   source_event_id,
                   processing_kind,
                   processed_at,
                   email_sent,
                   email_recipient,
                   workflow_result
            FROM dbo.processed_source_events
            WHERE source_event_id = @sourceEventId
              AND NULLIF(workflow_result, '') IS NOT NULL
            ORDER BY CASE WHEN processing_kind = 'NewRecommendation' THEN 0 ELSE 1 END,
                     processed_at DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceEventId", sourceEventId.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapProcessedSourceEventRecord(reader)
            : null;
    }

    public async Task<PoolRecommendationReportListResult> GetRecommendationReportsAsync(PoolRecommendationReportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var offset = (page - 1) * pageSize;
        var whereConditions = BuildRecommendationReportWhereConditions(query);
        var cteSql = BuildRecommendationReportCte(whereConditions);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var countCommand = new SqlCommand($"{cteSql} SELECT COUNT(1) FROM report_rows;", connection);
        AddRecommendationReportParameters(countCommand, query, pageSize, offset);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

        const string selectSql = """
            SELECT source_event_id,
                   processing_kind,
                   processed_at,
                   email_sent,
                   email_recipient,
                   project_id,
                   project_name,
                   application,
                   suggested_root_cause,
                   suggested_resolution,
                   confidence,
                   similar_incident_count,
                   failure_type,
                   resolution_category
            FROM report_rows
            ORDER BY processed_at DESC, source_event_id DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var command = new SqlCommand($"{cteSql} {selectSql}", connection);
        AddRecommendationReportParameters(command, query, pageSize, offset);

        var reports = new List<PoolRecommendationReportListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(MapPoolRecommendationReportListItem(reader));
        }

        return new PoolRecommendationReportListResult
        {
            Items = reports,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task MarkProcessedAsync(ProcessedSourceEventRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE dbo.processed_source_events
            SET processed_at = @processedAt,
                email_sent = @emailSent,
                email_recipient = @emailRecipient,
                workflow_result = @workflowResult
            WHERE source_event_id = @sourceEventId
              AND processing_kind = @processingKind;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.processed_source_events (
                    source_event_id,
                    processing_kind,
                    processed_at,
                    email_sent,
                    email_recipient,
                    workflow_result)
                VALUES (
                    @sourceEventId,
                    @processingKind,
                    @processedAt,
                    @emailSent,
                    @emailRecipient,
                    @workflowResult);
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceEventId", record.SourceEventId);
        command.Parameters.AddWithValue("@processingKind", record.ProcessingKind);
        command.Parameters.AddWithValue("@processedAt", record.ProcessedAt == default ? DateTime.UtcNow : record.ProcessedAt);
        command.Parameters.AddWithValue("@emailSent", record.EmailSent);
        command.Parameters.AddWithValue("@emailRecipient", record.EmailRecipient ?? string.Empty);
        command.Parameters.AddWithValue("@workflowResult", record.WorkflowResult == null ? string.Empty : JsonSerializer.Serialize(record.WorkflowResult, JsonOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProcessedSourceEventRecord MapProcessedSourceEventRecord(SqlDataReader reader)
    {
        var workflowResultJson = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);

        return new ProcessedSourceEventRecord
        {
            SourceEventId = reader.GetString(0),
            ProcessingKind = reader.GetString(1),
            ProcessedAt = reader.GetDateTime(2),
            EmailSent = reader.GetBoolean(3),
            EmailRecipient = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            WorkflowResult = string.IsNullOrWhiteSpace(workflowResultJson)
                ? null
                : JsonSerializer.Deserialize<TicketWorkflowResult>(workflowResultJson, JsonOptions)
        };
    }

    private static IReadOnlyList<string> BuildRecommendationReportWhereConditions(PoolRecommendationReportQuery query)
    {
        var conditions = new List<string>
        {
            "pse.processing_kind = 'NewRecommendation'",
            "NULLIF(pse.workflow_result, '') IS NOT NULL",
            "ISJSON(pse.workflow_result) = 1"
        };

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            conditions.Add("matched_project.project_id = @projectId");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            conditions.Add("""
                (pse.source_event_id LIKE @searchTerm
                 OR payload.application LIKE @searchTerm
                 OR payload.suggested_root_cause LIKE @searchTerm
                 OR payload.suggested_resolution LIKE @searchTerm
                 OR payload.failure_type LIKE @searchTerm
                 OR payload.resolution_category LIKE @searchTerm)
                """);
        }

        if (query.EmailSent.HasValue)
        {
            conditions.Add("pse.email_sent = @emailSent");
        }

        if (query.FromUtc.HasValue)
        {
            conditions.Add("pse.processed_at >= @fromUtc");
        }

        if (query.ToUtc.HasValue)
        {
            conditions.Add("pse.processed_at <= @toUtc");
        }

        return conditions;
    }

    private static string BuildRecommendationReportCte(IReadOnlyList<string> whereConditions)
    {
        var whereClause = string.Join($"{Environment.NewLine}              AND ", whereConditions);

        return $$"""
            WITH report_rows AS (
                SELECT pse.source_event_id,
                       pse.processing_kind,
                       pse.processed_at,
                       pse.email_sent,
                       pse.email_recipient,
                       COALESCE(matched_project.project_id, '') AS project_id,
                       COALESCE(matched_project.project_name, '') AS project_name,
                       COALESCE(payload.application, '') AS application,
                       COALESCE(payload.suggested_root_cause, '') AS suggested_root_cause,
                       COALESCE(payload.suggested_resolution, '') AS suggested_resolution,
                       COALESCE(TRY_CONVERT(float, payload.confidence), 0) AS confidence,
                       COALESCE(similar_incidents.similar_incident_count, 0) AS similar_incident_count,
                       COALESCE(payload.failure_type, '') AS failure_type,
                       COALESCE(payload.resolution_category, '') AS resolution_category
                FROM dbo.processed_source_events pse
                CROSS APPLY (
                    SELECT JSON_VALUE(pse.workflow_result, '$.SuggestedRootCause') AS suggested_root_cause,
                           JSON_VALUE(pse.workflow_result, '$.SuggestedResolution') AS suggested_resolution,
                           JSON_VALUE(pse.workflow_result, '$.Confidence') AS confidence,
                           JSON_VALUE(pse.workflow_result, '$.FailurePattern.Application') AS application,
                           JSON_VALUE(pse.workflow_result, '$.FailurePattern.FailureType') AS failure_type,
                           JSON_VALUE(pse.workflow_result, '$.FailurePattern.ResolutionCategory') AS resolution_category
                ) payload
                OUTER APPLY (
                    SELECT COUNT(1) AS similar_incident_count
                    FROM OPENJSON(pse.workflow_result, '$.SimilarIncidents')
                ) similar_incidents
                OUTER APPLY (
                    SELECT TOP (1) project_id, project_name
                    FROM dbo.project_configs project
                    WHERE NULLIF(project.application_filter, '') IS NOT NULL
                      AND NULLIF(payload.application, '') IS NOT NULL
                      AND (
                          (CHARINDEX('%', project.application_filter) > 0 OR CHARINDEX('_', project.application_filter) > 0)
                              AND payload.application LIKE project.application_filter
                          OR (CHARINDEX('%', project.application_filter) = 0 AND CHARINDEX('_', project.application_filter) = 0)
                              AND payload.application = project.application_filter
                      )
                    ORDER BY CASE WHEN project.application_filter = payload.application THEN 0 ELSE 1 END,
                             project.project_name ASC
                ) matched_project
                WHERE {{whereClause}}
            )
            """;
    }

    private static void AddRecommendationReportParameters(SqlCommand command, PoolRecommendationReportQuery query, int pageSize, int offset)
    {
        command.Parameters.AddWithValue("@pageSize", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            command.Parameters.AddWithValue("@projectId", query.ProjectId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            command.Parameters.AddWithValue("@searchTerm", $"%{query.SearchTerm.Trim()}%");
        }

        if (query.EmailSent.HasValue)
        {
            command.Parameters.AddWithValue("@emailSent", query.EmailSent.Value);
        }

        if (query.FromUtc.HasValue)
        {
            command.Parameters.AddWithValue("@fromUtc", query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            command.Parameters.AddWithValue("@toUtc", query.ToUtc.Value);
        }
    }

    private static PoolRecommendationReportListItem MapPoolRecommendationReportListItem(SqlDataReader reader)
    {
        var sourceEventId = reader.GetString(0);
        var suggestedRootCause = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
        var suggestedResolution = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
        var failureType = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);

        return new PoolRecommendationReportListItem
        {
            SourceEventId = sourceEventId,
            ProcessingKind = reader.GetString(1),
            ProcessedAt = reader.GetDateTime(2),
            EmailSent = reader.GetBoolean(3),
            EmailRecipient = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            ProjectId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ProjectName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            Application = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Summary = ResolveReportSummary(suggestedRootCause, suggestedResolution, failureType),
            Confidence = reader.IsDBNull(10) ? 0 : Convert.ToDouble(reader.GetValue(10)),
            SimilarIncidentCount = reader.IsDBNull(11) ? 0 : Convert.ToInt32(reader.GetValue(11)),
            FailureType = failureType,
            ResolutionCategory = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            ReportUrl = $"/Pool/{Uri.EscapeDataString(sourceEventId)}"
        };
    }

    private static string ResolveReportSummary(params string[] candidates)
    {
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim() ?? string.Empty;
    }
}
