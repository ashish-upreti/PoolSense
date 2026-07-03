using Microsoft.Data.SqlClient;
using PoolSense.Api.Feedback;

namespace PoolSense.Api.Data;

public interface IFeedbackRepository
{
    Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<int> AddApplicationFeedbackAsync(ApplicationFeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, FeedbackEvidence>> GetFeedbackEvidence(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, double>> GetFeedbackScores(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default);
}

public sealed class FeedbackEvidence
{
    public double Score { get; set; }
    public string LatestHelpfulComment { get; set; } = string.Empty;
    public string LatestNotHelpfulComment { get; set; } = string.Empty;
}

public sealed class FeedbackRepository : IFeedbackRepository
{
    private const double StrongHelpfulWeight = 0.10d;
    private const double WeakHelpfulWeight = 0.05d;
    private const double NotHelpfulPenalty = -0.05d;
    private const double HelpfulCommentWeight = 0.10d;
    private const double NotHelpfulCommentPenalty = -0.10d;
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
                current_issue_id,
                feedback_type,
                was_used,
                apply_to_target_incident,
                comment,
                target_ticket_id,
                retrieved_ticket_ids,
                created_at)
            OUTPUT INSERTED.id
            VALUES (
                @ticketQuery,
                @suggestedResolution,
                @currentIssueId,
                @feedbackType,
                @wasUsed,
                @applyToTargetIncident,
                @comment,
                @targetTicketId,
                @retrievedTicketIds,
                @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ticketQuery", feedback.TicketQuery ?? string.Empty);
        command.Parameters.AddWithValue("@suggestedResolution", feedback.SuggestedResolution ?? string.Empty);
        command.Parameters.AddWithValue("@currentIssueId", string.IsNullOrWhiteSpace(feedback.CurrentIssueId) ? string.Empty : feedback.CurrentIssueId.Trim());
        command.Parameters.AddWithValue("@feedbackType", feedback.FeedbackType);
        command.Parameters.AddWithValue("@wasUsed", feedback.WasUsed);
        command.Parameters.AddWithValue("@applyToTargetIncident", feedback.ApplyToTargetIncident);
        command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(feedback.Comment) ? string.Empty : feedback.Comment);
        command.Parameters.AddWithValue("@targetTicketId", string.IsNullOrWhiteSpace(feedback.TargetTicketId) ? string.Empty : feedback.TargetTicketId.Trim());
        command.Parameters.AddWithValue("@retrievedTicketIds", feedback.RetrievedTicketIds ?? string.Empty);
        command.Parameters.AddWithValue("@createdAt", feedback.CreatedAt == default ? DateTime.UtcNow : feedback.CreatedAt);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<int> AddApplicationFeedbackAsync(ApplicationFeedbackLog feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureApplicationFeedbackTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo.application_feedback_logs (
                user_name,
                user_email,
                feedback_type,
                message,
                created_at)
            OUTPUT INSERTED.id
            VALUES (
                @userName,
                @userEmail,
                @feedbackType,
                @message,
                @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userName", feedback.UserName ?? string.Empty);
        command.Parameters.AddWithValue("@userEmail", feedback.UserEmail ?? string.Empty);
        command.Parameters.AddWithValue("@feedbackType", feedback.FeedbackType ?? string.Empty);
        command.Parameters.AddWithValue("@message", feedback.Message ?? string.Empty);
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

        var evidenceByTicketId = await GetFeedbackEvidence([ticketId], cancellationToken);
        return evidenceByTicketId.TryGetValue(ticketId.Trim(), out var evidence) ? evidence.Score : 0;
    }

    public async Task<IReadOnlyDictionary<string, FeedbackEvidence>> GetFeedbackEvidence(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default)
    {
        var normalizedTicketIds = ticketIds
            .Where(ticketId => !string.IsNullOrWhiteSpace(ticketId))
            .Select(ticketId => ticketId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedTicketIds.Length == 0)
        {
            return new Dictionary<string, FeedbackEvidence>(StringComparer.OrdinalIgnoreCase);
        }

        if (normalizedTicketIds.Length > 1000)
        {
            var chunkedEvidence = new Dictionary<string, FeedbackEvidence>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticketIdChunk in normalizedTicketIds.Chunk(1000))
            {
                var chunkEvidence = await GetFeedbackEvidence(ticketIdChunk, cancellationToken);
                foreach (var evidence in chunkEvidence)
                {
                    chunkedEvidence[evidence.Key] = evidence.Value;
                }
            }

            return chunkedEvidence;
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
                       comment,
                       created_at
                FROM dbo.feedback_logs
                                WHERE apply_to_target_incident = 1
                                    AND NULLIF(LTRIM(RTRIM(target_ticket_id)), '') IS NOT NULL

                UNION ALL

                SELECT LTRIM(RTRIM(split.value)) AS ticket_id,
                       feedback.feedback_type,
                       feedback.was_used,
                       feedback.comment,
                       feedback.created_at
                FROM dbo.feedback_logs feedback
                CROSS APPLY STRING_SPLIT(feedback.retrieved_ticket_ids, ',') split
                                WHERE feedback.apply_to_target_incident = 1
                                    AND NULLIF(LTRIM(RTRIM(feedback.target_ticket_id)), '') IS NULL
            )
            SELECT ticket_id,
                   feedback_type,
                   was_used,
                   comment,
                   created_at
            FROM feedback_events
            WHERE ticket_id IN ({{string.Join(", ", ticketIdParameters)}});
            """;

        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < normalizedTicketIds.Length; index++)
        {
            command.Parameters.AddWithValue(ticketIdParameters[index], normalizedTicketIds[index]);
        }

        var now = DateTime.UtcNow;
        var evidenceByTicketId = new Dictionary<string, TicketFeedbackAccumulator>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var feedbackTicketId = reader.GetString(0);
            var feedbackType = reader.GetInt32(1);
            var wasUsed = reader.GetBoolean(2);
            var comment = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
            var createdAt = reader.GetDateTime(4);
            var ageSeconds = Math.Max(0, (now - createdAt).TotalSeconds);
            var recencyFactor = Math.Pow(0.5, ageSeconds / FeedbackHalfLifeSeconds);

            var baseWeight = GetBaseFeedbackWeight(feedbackType, wasUsed) * recencyFactor;
            var commentWeight = GetCommentWeight(feedbackType, comment) * recencyFactor;

            if (!evidenceByTicketId.TryGetValue(feedbackTicketId, out var accumulator))
            {
                accumulator = new TicketFeedbackAccumulator();
                evidenceByTicketId[feedbackTicketId] = accumulator;
            }

            accumulator.Score += baseWeight + commentWeight;

            if (!string.IsNullOrWhiteSpace(comment))
            {
                if (feedbackType == 1 && createdAt >= accumulator.LatestHelpfulCommentCreatedAt)
                {
                    accumulator.LatestHelpfulComment = comment;
                    accumulator.LatestHelpfulCommentCreatedAt = createdAt;
                }
                else if (feedbackType == -1 && createdAt >= accumulator.LatestNotHelpfulCommentCreatedAt)
                {
                    accumulator.LatestNotHelpfulComment = comment;
                    accumulator.LatestNotHelpfulCommentCreatedAt = createdAt;
                }
            }
        }

        return evidenceByTicketId.ToDictionary(
            entry => entry.Key,
            entry => new FeedbackEvidence
            {
                Score = Math.Max(MinFeedbackWeight, Math.Min(MaxFeedbackWeight, entry.Value.Score)),
                LatestHelpfulComment = entry.Value.LatestHelpfulComment,
                LatestNotHelpfulComment = entry.Value.LatestNotHelpfulComment
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, double>> GetFeedbackScores(IReadOnlyCollection<string> ticketIds, CancellationToken cancellationToken = default)
    {
        var evidenceByTicketId = await GetFeedbackEvidence(ticketIds, cancellationToken);
        return evidenceByTicketId.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Score,
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

    private static double GetCommentWeight(int feedbackType, string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return 0;
        }

        return feedbackType == 1
            ? HelpfulCommentWeight
            : NotHelpfulCommentPenalty;
    }

    private sealed class TicketFeedbackAccumulator
    {
        public double Score { get; set; }
        public string LatestHelpfulComment { get; set; } = string.Empty;
        public DateTime LatestHelpfulCommentCreatedAt { get; set; } = DateTime.MinValue;
        public string LatestNotHelpfulComment { get; set; } = string.Empty;
        public DateTime LatestNotHelpfulCommentCreatedAt { get; set; } = DateTime.MinValue;
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
                    current_issue_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_current_issue_id DEFAULT '',
                    feedback_type int NOT NULL,
                    was_used bit NOT NULL CONSTRAINT DF_feedback_logs_was_used DEFAULT 0,
                    apply_to_target_incident bit NOT NULL CONSTRAINT DF_feedback_logs_apply_to_target_incident DEFAULT 1,
                    comment nvarchar(max) NOT NULL CONSTRAINT DF_feedback_logs_comment DEFAULT '',
                    target_ticket_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_target_ticket_id DEFAULT '',
                    retrieved_ticket_ids nvarchar(max) NOT NULL,
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_feedback_logs_created_at DEFAULT SYSUTCDATETIME()
                );
            END;

            IF COL_LENGTH('dbo.feedback_logs', 'current_issue_id') IS NULL
                ALTER TABLE dbo.feedback_logs ADD current_issue_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_current_issue_id DEFAULT '';

            IF COL_LENGTH('dbo.feedback_logs', 'was_used') IS NULL
                ALTER TABLE dbo.feedback_logs ADD was_used bit NOT NULL CONSTRAINT DF_feedback_logs_was_used DEFAULT 0;

            IF COL_LENGTH('dbo.feedback_logs', 'apply_to_target_incident') IS NULL
                ALTER TABLE dbo.feedback_logs ADD apply_to_target_incident bit NOT NULL CONSTRAINT DF_feedback_logs_apply_to_target_incident DEFAULT 1;

            IF COL_LENGTH('dbo.feedback_logs', 'target_ticket_id') IS NULL
                ALTER TABLE dbo.feedback_logs ADD target_ticket_id nvarchar(450) NOT NULL CONSTRAINT DF_feedback_logs_target_ticket_id DEFAULT '';

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_created_at' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
                CREATE INDEX IX_feedback_logs_created_at ON dbo.feedback_logs (created_at DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_target_ticket_id' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
                CREATE INDEX IX_feedback_logs_target_ticket_id ON dbo.feedback_logs (target_ticket_id);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_current_issue_id' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
                CREATE INDEX IX_feedback_logs_current_issue_id ON dbo.feedback_logs (current_issue_id);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureApplicationFeedbackTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.application_feedback_logs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.application_feedback_logs (
                    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_application_feedback_logs PRIMARY KEY,
                    user_name nvarchar(256) NOT NULL CONSTRAINT DF_application_feedback_logs_user_name DEFAULT '',
                    user_email nvarchar(256) NOT NULL CONSTRAINT DF_application_feedback_logs_user_email DEFAULT '',
                    feedback_type nvarchar(64) NOT NULL CONSTRAINT DF_application_feedback_logs_feedback_type DEFAULT '',
                    message nvarchar(max) NOT NULL CONSTRAINT DF_application_feedback_logs_message DEFAULT '',
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_application_feedback_logs_created_at DEFAULT SYSUTCDATETIME()
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_application_feedback_logs_created_at' AND object_id = OBJECT_ID(N'dbo.application_feedback_logs'))
                CREATE INDEX IX_application_feedback_logs_created_at ON dbo.application_feedback_logs (created_at DESC);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}