using Npgsql;
using PoolSense.Api.Feedback;

namespace PoolSense.Api.Data;

public interface IFeedbackRepository
{
    Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default);
}

public sealed class FeedbackRepository : IFeedbackRepository
{
    private const double StrongHelpfulWeight = 0.10d;
    private const double WeakHelpfulWeight = 0.05d;
    private const double NotHelpfulPenalty = -0.05d;
    private const double MaxFeedbackWeight = 0.20d;
    private const double MinFeedbackWeight = -0.20d;
    private const double FeedbackHalfLifeSeconds = 45d * 24d * 60d * 60d;

    private readonly IConfiguration _configuration;

    public FeedbackRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO feedback_logs (
                ticket_query,
                suggested_resolution,
                feedback_type,
                was_used,
                comment,
                target_ticket_id,
                retrieved_ticket_ids,
                created_at)
            VALUES (
                @ticketQuery,
                @suggestedResolution,
                @feedbackType,
                @wasUsed,
                @comment,
                @targetTicketId,
                @retrievedTicketIds,
                @createdAt)
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ticketQuery", feedback.TicketQuery);
        command.Parameters.AddWithValue("suggestedResolution", feedback.SuggestedResolution);
        command.Parameters.AddWithValue("feedbackType", feedback.FeedbackType);
        command.Parameters.AddWithValue("wasUsed", feedback.WasUsed);
        command.Parameters.AddWithValue("comment", string.IsNullOrWhiteSpace(feedback.Comment) ? string.Empty : feedback.Comment);
        command.Parameters.AddWithValue("targetTicketId", string.IsNullOrWhiteSpace(feedback.TargetTicketId) ? string.Empty : feedback.TargetTicketId.Trim());
        command.Parameters.AddWithValue("retrievedTicketIds", feedback.RetrievedTicketIds);
        command.Parameters.AddWithValue("createdAt", feedback.CreatedAt == default ? DateTime.UtcNow : feedback.CreatedAt);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : Convert.ToInt32(result);
    }

    public async Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            WITH feedback_events AS (
                SELECT TRIM(target_ticket_id) AS ticket_id,
                       feedback_type,
                       was_used,
                       created_at
                FROM feedback_logs
                WHERE NULLIF(TRIM(target_ticket_id), '') IS NOT NULL

                UNION ALL

                SELECT TRIM(ticket_id) AS ticket_id,
                       feedback_type,
                       was_used,
                       created_at
                FROM feedback_logs
                CROSS JOIN LATERAL UNNEST(string_to_array(retrieved_ticket_ids, ',')) AS ticket_id
                WHERE NULLIF(TRIM(target_ticket_id), '') IS NULL
            )
            SELECT COALESCE(
                GREATEST(
                    @minFeedbackWeight,
                    LEAST(
                        @maxFeedbackWeight,
                        SUM(
                            CASE
                                WHEN feedback_type = 1 AND was_used THEN @strongHelpfulWeight
                                WHEN feedback_type = 1 THEN @weakHelpfulWeight
                                ELSE @notHelpfulPenalty
                            END * POWER(0.5, EXTRACT(EPOCH FROM (NOW() - created_at)) / @feedbackHalfLifeSeconds)
                        )
                    )
                ),
                0)
            FROM feedback_events
            WHERE ticket_id = @ticketId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ticketId", ticketId.Trim());
        command.Parameters.AddWithValue("strongHelpfulWeight", StrongHelpfulWeight);
        command.Parameters.AddWithValue("weakHelpfulWeight", WeakHelpfulWeight);
        command.Parameters.AddWithValue("notHelpfulPenalty", NotHelpfulPenalty);
        command.Parameters.AddWithValue("maxFeedbackWeight", MaxFeedbackWeight);
        command.Parameters.AddWithValue("minFeedbackWeight", MinFeedbackWeight);
        command.Parameters.AddWithValue("feedbackHalfLifeSeconds", FeedbackHalfLifeSeconds);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is double score ? score : Convert.ToDouble(result);
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS feedback_logs (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ticket_query text NOT NULL,
                suggested_resolution text NOT NULL,
                feedback_type integer NOT NULL,
                was_used boolean NOT NULL DEFAULT FALSE,
                comment text NOT NULL DEFAULT '',
                target_ticket_id text NOT NULL DEFAULT '',
                retrieved_ticket_ids text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );

            ALTER TABLE IF EXISTS feedback_logs
                ADD COLUMN IF NOT EXISTS was_used boolean NOT NULL DEFAULT FALSE;

            ALTER TABLE IF EXISTS feedback_logs
                ADD COLUMN IF NOT EXISTS target_ticket_id text NOT NULL DEFAULT '';

            CREATE INDEX IF NOT EXISTS feedback_logs_created_at_idx
                ON feedback_logs (created_at DESC);

            CREATE INDEX IF NOT EXISTS feedback_logs_target_ticket_id_idx
                ON feedback_logs (target_ticket_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
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
