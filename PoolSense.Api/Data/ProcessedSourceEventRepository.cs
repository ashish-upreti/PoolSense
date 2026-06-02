using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;
using System.Text.Json;

namespace PoolSense.Api.Data;

public interface IProcessedSourceEventRepository
{
    Task<bool> HasBeenProcessedAsync(string sourceEventId, string processingKind, CancellationToken cancellationToken = default);
    Task<int> CountProcessedAsync(IReadOnlyCollection<string> sourceEventIds, string processingKind, CancellationToken cancellationToken = default);
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

public class ProcessedSourceEventRepository : IProcessedSourceEventRepository
{
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
        command.Parameters.AddWithValue("@workflowResult", record.WorkflowResult == null ? string.Empty : JsonSerializer.Serialize(record.WorkflowResult));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}