using Microsoft.Data.SqlClient;
using PoolSense.Api.Feedback;

namespace PoolSense.Api.Data;

public interface IFeedbackRepository
{
    Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, double>> GetFeedbackScores(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default);
}

public sealed class FeedbackRepository : IFeedbackRepository
{
    private const double StrongHelpfulWeight = 0.10d;
    private const double WeakHelpfulWeight = 0.05d;
    private const double NotHelpfulPenalty = -0.05d;
    private const double MaxFeedbackWeight = 0.20d;
    private const double MinFeedbackWeight = -0.20d;
    private const double FeedbackHalfLifeSeconds = 45d * 24d * 60d * 60d;

    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public FeedbackRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo.feedback_logs (
                ticket_query,
                suggested_resolution,
                feedback_type,
                was_used,
                comment,
                target_ticket_id,
                retrieved_ticket_ids,
                created_at)
            OUTPUT INSERTED.id
            VALUES (
                @ticketQuery,
                @suggestedResolution,
                @feedbackType,
                @wasUsed,
                @comment,
                @targetTicketId,
                @retrievedTicketIds,
                @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ticketQuery", feedback.TicketQuery ?? string.Empty);
        command.Parameters.AddWithValue("@suggestedResolution", feedback.SuggestedResolution ?? string.Empty);
        command.Parameters.AddWithValue("@feedbackType", feedback.FeedbackType);
        command.Parameters.AddWithValue("@wasUsed", feedback.WasUsed);
        command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(feedback.Comment) ? string.Empty : feedback.Comment);
        command.Parameters.AddWithValue("@targetTicketId", string.IsNullOrWhiteSpace(feedback.TargetTicketId) ? string.Empty : feedback.TargetTicketId.Trim());
        command.Parameters.AddWithValue("@retrievedTicketIds", feedback.RetrievedTicketIds ?? string.Empty);
        command.Parameters.AddWithValue("@createdAt", feedback.CreatedAt == default ? DateTime.UtcNow : feedback.CreatedAt);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return 0;
        }

        var scores = await GetFeedbackScores([ticketId], cancellationToken);
        return scores.TryGetValue(ticketId.Trim(), out var score) ? score : 0;
    }

    public async Task<IReadOnlyDictionary<string, double>> GetFeedbackScores(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default)
    {
        var normalizedTicketIds = ticketIds
            .Where(ticketId => !string.IsNullOrWhiteSpace(ticketId))
            .Select(ticketId => ticketId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedTicketIds.Length == 0)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        if (normalizedTicketIds.Length > 1000)
        {
            var chunkedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticketIdChunk in normalizedTicketIds.Chunk(1000))
            {
                var chunkScores = await GetFeedbackScores(ticketIdChunk, cancellationToken);
                foreach (var score in chunkScores)
                {
                    chunkedScores[score.Key] = score.Value;
                }
            }

            return chunkedScores;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var ticketIdParameters = normalizedTicketIds
            .Select((_, index) => $"@ticketId{index}")
            .ToArray();

        var sql = $$"""
            WITH feedback_events AS (
                SELECT LTRIM(RTRIM(target_ticket_id)) AS ticket_id,
                       feedback_type,
                       was_used,
                       created_at
                FROM dbo.feedback_logs
                WHERE NULLIF(LTRIM(RTRIM(target_ticket_id)), '') IS NOT NULL

                UNION ALL

                SELECT LTRIM(RTRIM(split.value)) AS ticket_id,
                       feedback.feedback_type,
                       feedback.was_used,
                       feedback.created_at
                FROM dbo.feedback_logs feedback
                CROSS APPLY STRING_SPLIT(feedback.retrieved_ticket_ids, ',') split
                WHERE NULLIF(LTRIM(RTRIM(feedback.target_ticket_id)), '') IS NULL
            )
            SELECT ticket_id,
                   feedback_type,
                   was_used,
                   created_at
            FROM feedback_events
            WHERE ticket_id IN ({{string.Join(", ", ticketIdParameters)}});
            """;

        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < normalizedTicketIds.Length; index++)
        {
            command.Parameters.AddWithValue(ticketIdParameters[index], normalizedTicketIds[index]);
        }

        var accumulatedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var feedbackTicketId = reader.GetString(0);
            var feedbackType = reader.GetInt32(1);
            var wasUsed = reader.GetBoolean(2);
            var createdAt = reader.GetDateTime(3);
            var ageSeconds = Math.Max(0, (now - createdAt).TotalSeconds);
            var weight = GetBaseFeedbackWeight(feedbackType, wasUsed)
                * Math.Pow(0.5, ageSeconds / FeedbackHalfLifeSeconds);

            accumulatedScores[feedbackTicketId] = accumulatedScores.TryGetValue(feedbackTicketId, out var existingScore)
                ? existingScore + weight
                : weight;
        }

        return accumulatedScores.ToDictionary(
            score => score.Key,
            score => Math.Max(MinFeedbackWeight, Math.Min(MaxFeedbackWeight, score.Value)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double GetBaseFeedbackWeight(int feedbackType, bool wasUsed)
    {
        if (feedbackType == 1 && wasUsed)
        {
            return StrongHelpfulWeight;
        }

        return feedbackType == 1
            ? WeakHelpfulWeight
            : NotHelpfulPenalty;
    }

    private static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.feedback_logs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.feedback_logs (
                    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_feedback_logs PRIMARY KEY,
                    ticket_query nvarchar(max) NOT NULL,
                    suggested_resolution nvarchar(max) NOT NULL,
                    feedback_type int NOT NULL,
                    was_used bit NOT NULL CONSTRAINT DF_feedback_logs_was_used DEFAULT 0,
                    comment nvarchar(max) NOT NULL CONSTRAINT DF_feedback_logs_comment DEFAULT '',
                    target_ticket_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_target_ticket_id DEFAULT '',
                    retrieved_ticket_ids nvarchar(max) NOT NULL,
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_feedback_logs_created_at DEFAULT SYSUTCDATETIME()
                );
            END;

            IF COL_LENGTH('dbo.feedback_logs', 'was_used') IS NULL
                ALTER TABLE dbo.feedback_logs ADD was_used bit NOT NULL CONSTRAINT DF_feedback_logs_was_used DEFAULT 0;

            IF COL_LENGTH('dbo.feedback_logs', 'target_ticket_id') IS NULL
                ALTER TABLE dbo.feedback_logs ADD target_ticket_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_target_ticket_id DEFAULT '';

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_created_at' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
                CREATE INDEX IX_feedback_logs_created_at ON dbo.feedback_logs (created_at DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_target_ticket_id' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
                CREATE INDEX IX_feedback_logs_target_ticket_id ON dbo.feedback_logs (target_ticket_id);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}