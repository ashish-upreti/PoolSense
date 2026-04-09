using Npgsql;
using PoolSense.Api.Models;
using System.Text.Json;

namespace PoolSense.Api.Data;

public interface IProcessedSourceEventRepository
{
    Task<bool> HasBeenProcessedAsync(string sourceEventId, string processingKind, CancellationToken cancellationToken = default);
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
    private readonly IConfiguration _configuration;

    public ProcessedSourceEventRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<bool> HasBeenProcessedAsync(string sourceEventId, string processingKind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId) || string.IsNullOrWhiteSpace(processingKind))
        {
            return false;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT 1
            FROM processed_source_events
            WHERE source_event_id = @sourceEventId
              AND processing_kind = @processingKind
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sourceEventId", sourceEventId);
        command.Parameters.AddWithValue("processingKind", processingKind);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    public async Task MarkProcessedAsync(ProcessedSourceEventRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO processed_source_events (source_event_id, processing_kind, processed_at, email_sent, email_recipient, workflow_result)
            VALUES (@sourceEventId, @processingKind, @processedAt, @emailSent, @emailRecipient, @workflowResult)
            ON CONFLICT (source_event_id, processing_kind)
            DO UPDATE SET processed_at = EXCLUDED.processed_at,
                          email_sent = EXCLUDED.email_sent,
                          email_recipient = EXCLUDED.email_recipient,
                          workflow_result = EXCLUDED.workflow_result;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sourceEventId", record.SourceEventId);
        command.Parameters.AddWithValue("processingKind", record.ProcessingKind);
        command.Parameters.AddWithValue("processedAt", record.ProcessedAt == default ? DateTime.UtcNow : record.ProcessedAt);
        command.Parameters.AddWithValue("emailSent", record.EmailSent);
        command.Parameters.AddWithValue("emailRecipient", record.EmailRecipient ?? string.Empty);
        command.Parameters.AddWithValue("workflowResult", record.WorkflowResult == null ? string.Empty : JsonSerializer.Serialize(record.WorkflowResult));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("Postgres")
            ?? _configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("A PostgreSQL connection string was not found. Configure ConnectionStrings:Postgres or ConnectionStrings:DefaultConnection.");
        }

        return connectionString;
    }
}