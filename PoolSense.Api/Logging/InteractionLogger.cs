using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using System.Text;

namespace PoolSense.Api.Logging;

/// <summary>
/// Persists AI pipeline interaction metadata for analysis and improvement.
/// </summary>
public sealed class InteractionLogger
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;
    private readonly ILogger<InteractionLogger> _logger;

    public InteractionLogger(IPoolSenseSqlConnectionFactory connectionFactory, ILogger<InteractionLogger> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Logs the query, retrieved tickets, generated resolution, and processing metrics.
    /// </summary>
    /// <param name="query">The query used for retrieval.</param>
    /// <param name="retrievedTickets">The tickets returned by similarity search.</param>
    /// <param name="resolution">The suggested resolution produced by the pipeline.</param>
    /// <param name="confidence">The model confidence score for the resolution.</param>
    /// <param name="processingTime">The elapsed processing time for the interaction.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    public async Task LogInteractionAsync(
        string query,
        IReadOnlyList<TicketKnowledge> retrievedTickets,
        string resolution,
        double confidence,
        TimeSpan processingTime,
        CancellationToken cancellationToken = default)
    {
        await LogInteractionAsync(
            query,
            retrievedTickets,
            resolution,
            confidence,
            processingTime,
            generatedEmbeddingLength: 0,
            cancellationToken);
    }

    /// <summary>
    /// Logs the query, retrieved tickets, generated resolution, and processing metrics.
    /// </summary>
    /// <param name="query">The query used for retrieval.</param>
    /// <param name="retrievedTickets">The tickets returned by similarity search.</param>
    /// <param name="resolution">The suggested resolution produced by the pipeline.</param>
    /// <param name="confidence">The model confidence score for the resolution.</param>
    /// <param name="processingTime">The elapsed processing time for the interaction.</param>
    /// <param name="generatedEmbeddingLength">The generated embedding length metadata.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    public async Task LogInteractionAsync(
        string query,
        IReadOnlyList<TicketKnowledge> retrievedTickets,
        string resolution,
        double confidence,
        TimeSpan processingTime,
        int generatedEmbeddingLength,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.", nameof(query));
        }

        ArgumentNullException.ThrowIfNull(retrievedTickets);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var interactionLog = new InteractionLog
        {
            Query = query.Trim(),
            GeneratedEmbeddingLength = Math.Max(0, generatedEmbeddingLength),
            RetrievedTicketIds = string.Join(',', retrievedTickets.Select(ticket => ticket.TicketId).Where(ticketId => !string.IsNullOrWhiteSpace(ticketId)).Distinct(StringComparer.OrdinalIgnoreCase)),
            RetrievedContents = BuildRetrievedContents(retrievedTickets),
            SuggestedResolution = resolution ?? string.Empty,
            Confidence = (float)confidence,
            ProcessingTimeMs = processingTime <= TimeSpan.Zero ? 0 : (int)Math.Min(processingTime.TotalMilliseconds, int.MaxValue),
            CreatedAt = DateTime.UtcNow
        };

        const string sql = """
            INSERT INTO dbo.interaction_logs (
                query,
                generated_embedding_length,
                retrieved_ticket_ids,
                retrieved_contents,
                suggested_resolution,
                confidence,
                processing_time_ms,
                created_at)
            VALUES (
                @query,
                @generatedEmbeddingLength,
                @retrievedTicketIds,
                @retrievedContents,
                @suggestedResolution,
                @confidence,
                @processingTimeMs,
                @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@query", interactionLog.Query);
        command.Parameters.AddWithValue("@generatedEmbeddingLength", interactionLog.GeneratedEmbeddingLength);
        command.Parameters.AddWithValue("@retrievedTicketIds", interactionLog.RetrievedTicketIds);
        command.Parameters.AddWithValue("@retrievedContents", interactionLog.RetrievedContents);
        command.Parameters.AddWithValue("@suggestedResolution", interactionLog.SuggestedResolution);
        command.Parameters.AddWithValue("@confidence", interactionLog.Confidence);
        command.Parameters.AddWithValue("@processingTimeMs", interactionLog.ProcessingTimeMs);
        command.Parameters.AddWithValue("@createdAt", interactionLog.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Logged AI interaction for query length {QueryLength} with {RetrievedTicketCount} retrieved tickets.", interactionLog.Query.Length, retrievedTickets.Count);
    }

    private static string BuildRetrievedContents(IReadOnlyList<TicketKnowledge> retrievedTickets)
    {
        if (retrievedTickets.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var ticket in retrievedTickets)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
            }

            builder.Append("TicketId: ").AppendLine(ticket.TicketId ?? string.Empty);
            builder.Append("Problem: ").AppendLine(ticket.Problem ?? string.Empty);
            builder.Append("RootCause: ").AppendLine(ticket.RootCause ?? string.Empty);
            builder.Append("Resolution: ").AppendLine(ticket.Resolution ?? string.Empty);
        }

        return builder.ToString();
    }

    private static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.interaction_logs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.interaction_logs (
                    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_interaction_logs PRIMARY KEY,
                    query nvarchar(max) NOT NULL,
                    generated_embedding_length int NOT NULL CONSTRAINT DF_interaction_logs_generated_embedding_length DEFAULT 0,
                    retrieved_ticket_ids nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_retrieved_ticket_ids DEFAULT '',
                    retrieved_contents nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_retrieved_contents DEFAULT '',
                    suggested_resolution nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_suggested_resolution DEFAULT '',
                    confidence real NOT NULL CONSTRAINT DF_interaction_logs_confidence DEFAULT 0,
                    processing_time_ms int NOT NULL CONSTRAINT DF_interaction_logs_processing_time_ms DEFAULT 0,
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_interaction_logs_created_at DEFAULT SYSUTCDATETIME()
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_interaction_logs_created_at' AND object_id = OBJECT_ID(N'dbo.interaction_logs'))
                CREATE INDEX IX_interaction_logs_created_at ON dbo.interaction_logs (created_at DESC);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}