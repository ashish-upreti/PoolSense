using Microsoft.Data.SqlClient;
using PoolSense.Api.Feedback;

namespace PoolSense.Api.Data;

public interface IFeedbackRepository
{
    Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<int> AddApplicationFeedbackAsync(ApplicationFeedbackLog feedback, CancellationToken cancellationToken = default);
    Task<ApplicationFeedbackInsights> GetApplicationFeedbackInsightsAsync(int rangeDays = 30, CancellationToken cancellationToken = default);
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

    public async Task<ApplicationFeedbackInsights> GetApplicationFeedbackInsightsAsync(int rangeDays = 30, CancellationToken cancellationToken = default)
    {
        rangeDays = NormalizeApplicationFeedbackRangeDays(rangeDays);
        var isAllTime = rangeDays == 0;

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureApplicationFeedbackTableAsync(connection, cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var now = DateTime.UtcNow;
        var rangeStart = isAllTime ? DateTime.MinValue : now.AddDays(-rangeDays);
        var previousRangeStart = isAllTime ? DateTime.MinValue : now.AddDays(-(rangeDays * 2));
        var chartDays = isAllTime ? 90 : rangeDays;
        var chartStart = now.Date.AddDays(-(chartDays - 1));

        const string sql = """
            SELECT
                COUNT(*) AS total_feedback,
                ISNULL(SUM(CASE WHEN @isAllTime = 1 OR created_at >= @rangeStart THEN 1 ELSE 0 END), 0) AS feedback_last_30_days,
                ISNULL(SUM(CASE WHEN @isAllTime = 0 AND created_at >= @previousRangeStart AND created_at < @rangeStart THEN 1 ELSE 0 END), 0) AS previous_feedback_30_days
            FROM dbo.application_feedback_logs;

            SELECT
                ISNULL(SUM(CASE WHEN (@isAllTime = 1 OR created_at >= @rangeStart) AND feedback_type = 1 THEN 1 ELSE 0 END), 0) AS helpful_ai_feedback_last_30_days,
                ISNULL(SUM(CASE WHEN @isAllTime = 0 AND created_at >= @previousRangeStart AND created_at < @rangeStart AND feedback_type = 1 THEN 1 ELSE 0 END), 0) AS previous_helpful_ai_feedback_30_days,
                ISNULL(SUM(CASE WHEN @isAllTime = 1 OR created_at >= @rangeStart THEN 1 ELSE 0 END), 0) AS total_ai_feedback_last_30_days,
                ISNULL(SUM(CASE WHEN (@isAllTime = 1 OR created_at >= @rangeStart) AND feedback_type = -1 THEN 1 ELSE 0 END), 0) AS not_helpful_ai_feedback_last_30_days
            FROM dbo.feedback_logs;

            SELECT
                COUNT(DISTINCT CASE WHEN (@isAllTime = 1 OR created_at >= @rangeStart) AND success = 1 THEN NULLIF(LOWER(LTRIM(RTRIM(username))), '') END) AS unique_active_users_last_30_days,
                COUNT(DISTINCT CASE WHEN @isAllTime = 0 AND created_at >= @previousRangeStart AND created_at < @rangeStart AND success = 1 THEN NULLIF(LOWER(LTRIM(RTRIM(username))), '') END) AS previous_unique_active_users_30_days,
                COUNT(DISTINCT CASE WHEN created_at >= CAST(@now AS date) AND success = 1 THEN NULLIF(LOWER(LTRIM(RTRIM(username))), '') END) AS users_today,
                COUNT(DISTINCT CASE WHEN created_at >= DATEADD(day, -1, CAST(@now AS date)) AND created_at < CAST(@now AS date) AND success = 1 THEN NULLIF(LOWER(LTRIM(RTRIM(username))), '') END) AS users_yesterday,
                ISNULL(SUM(CASE WHEN (@isAllTime = 1 OR created_at >= @rangeStart) AND success = 1 THEN 1 ELSE 0 END), 0) AS total_logins_last_30_days,
                ISNULL(SUM(CASE WHEN @isAllTime = 0 AND created_at >= @previousRangeStart AND created_at < @rangeStart AND success = 1 THEN 1 ELSE 0 END), 0) AS previous_total_logins_30_days
            FROM dbo.auth_login_audit;

            SELECT
                ISNULL(SUM(CASE WHEN @isAllTime = 1 OR created_at >= @rangeStart THEN 1 ELSE 0 END), 0) AS recommendations_processed_last_30_days,
                ISNULL(SUM(CASE WHEN @isAllTime = 0 AND created_at >= @previousRangeStart AND created_at < @rangeStart THEN 1 ELSE 0 END), 0) AS previous_recommendations_processed_30_days
            FROM dbo.interaction_logs;

            SELECT TOP (4)
                COALESCE(NULLIF(LTRIM(RTRIM(feedback_type)), ''), 'Unclassified') AS feedback_type,
                COUNT(*) AS feedback_count
            FROM dbo.application_feedback_logs
            WHERE @isAllTime = 1 OR created_at >= @rangeStart
            GROUP BY COALESCE(NULLIF(LTRIM(RTRIM(feedback_type)), ''), 'Unclassified')
            ORDER BY feedback_count DESC, feedback_type ASC;

            WITH days AS (
                SELECT CAST(@chartStart AS date) AS day_start
                UNION ALL
                SELECT DATEADD(day, 1, day_start)
                FROM days
                WHERE day_start < CAST(@now AS date)
            )
            SELECT
                CONVERT(char(10), days.day_start, 23) AS feedback_date,
                COUNT(feedback.id) AS feedback_count
            FROM days
            LEFT JOIN dbo.application_feedback_logs feedback
                ON feedback.created_at >= days.day_start
                AND feedback.created_at < DATEADD(day, 1, days.day_start)
            GROUP BY days.day_start
            ORDER BY days.day_start
            OPTION (MAXRECURSION 100);

            WITH days AS (
                SELECT CAST(@chartStart AS date) AS day_start
                UNION ALL
                SELECT DATEADD(day, 1, day_start)
                FROM days
                WHERE day_start < CAST(@now AS date)
            )
            SELECT
                CONVERT(char(10), days.day_start, 23) AS feedback_date,
                COUNT(feedback.id) AS feedback_count
            FROM days
            LEFT JOIN dbo.feedback_logs feedback
                ON feedback.created_at >= days.day_start
                AND feedback.created_at < DATEADD(day, 1, days.day_start)
            GROUP BY days.day_start
            ORDER BY days.day_start
            OPTION (MAXRECURSION 100);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@rangeStart", rangeStart);
        command.Parameters.AddWithValue("@previousRangeStart", previousRangeStart);
        command.Parameters.AddWithValue("@chartStart", chartStart);
        command.Parameters.AddWithValue("@isAllTime", isAllTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var totalFeedback = 0;
        var feedbackLast30Days = 0;
        var previousFeedback30Days = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            totalFeedback = reader.GetInt32(0);
            feedbackLast30Days = reader.GetInt32(1);
            previousFeedback30Days = reader.GetInt32(2);
        }

        await reader.NextResultAsync(cancellationToken);

        var helpfulAiFeedbackLast30Days = 0;
        var previousHelpfulAiFeedback30Days = 0;
        var totalAiFeedbackLast30Days = 0;
        var notHelpfulAiFeedbackLast30Days = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            helpfulAiFeedbackLast30Days = reader.GetInt32(0);
            previousHelpfulAiFeedback30Days = reader.GetInt32(1);
            totalAiFeedbackLast30Days = reader.GetInt32(2);
            notHelpfulAiFeedbackLast30Days = reader.GetInt32(3);
        }

        await reader.NextResultAsync(cancellationToken);

        var uniqueActiveUsersLast30Days = 0;
        var previousUniqueActiveUsers30Days = 0;
        var usersToday = 0;
        var usersYesterday = 0;
        var totalLoginsLast30Days = 0;
        var previousTotalLogins30Days = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            uniqueActiveUsersLast30Days = reader.GetInt32(0);
            previousUniqueActiveUsers30Days = reader.GetInt32(1);
            usersToday = reader.GetInt32(2);
            usersYesterday = reader.GetInt32(3);
            totalLoginsLast30Days = reader.GetInt32(4);
            previousTotalLogins30Days = reader.GetInt32(5);
        }

        await reader.NextResultAsync(cancellationToken);

        var recommendationsProcessedLast30Days = 0;
        var previousRecommendationsProcessed30Days = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            recommendationsProcessedLast30Days = reader.GetInt32(0);
            previousRecommendationsProcessed30Days = reader.GetInt32(1);
        }

        await reader.NextResultAsync(cancellationToken);

        var feedbackTypes = new List<ApplicationFeedbackTypeSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            feedbackTypes.Add(new ApplicationFeedbackTypeSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        await reader.NextResultAsync(cancellationToken);

        var dailyFeedbackCounts = new List<ApplicationFeedbackDailyCount>();
        while (await reader.ReadAsync(cancellationToken))
        {
            dailyFeedbackCounts.Add(new ApplicationFeedbackDailyCount(reader.GetString(0), reader.GetInt32(1)));
        }

        await reader.NextResultAsync(cancellationToken);

        var dailyAiFeedbackCounts = new List<ApplicationFeedbackDailyCount>();
        while (await reader.ReadAsync(cancellationToken))
        {
            dailyAiFeedbackCounts.Add(new ApplicationFeedbackDailyCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return new ApplicationFeedbackInsights(
            rangeDays,
            totalFeedback,
            feedbackLast30Days,
            previousFeedback30Days,
            uniqueActiveUsersLast30Days,
            previousUniqueActiveUsers30Days,
            usersToday,
            usersYesterday,
            totalLoginsLast30Days,
            previousTotalLogins30Days,
            recommendationsProcessedLast30Days,
            previousRecommendationsProcessed30Days,
            helpfulAiFeedbackLast30Days,
            previousHelpfulAiFeedback30Days,
            totalAiFeedbackLast30Days,
            notHelpfulAiFeedbackLast30Days,
            feedbackTypes,
            dailyFeedbackCounts,
            dailyAiFeedbackCounts,
            now);
    }

    private static int NormalizeApplicationFeedbackRangeDays(int rangeDays) => rangeDays switch
    {
        0 or 7 or 30 or 90 => rangeDays,
        _ => 30
    };

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
                THROW 50001, 'Missing dbo.feedback_logs. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.feedback_logs', 'current_issue_id') IS NULL
                THROW 50002, 'Missing dbo.feedback_logs.current_issue_id. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.feedback_logs', 'was_used') IS NULL
                THROW 50003, 'Missing dbo.feedback_logs.was_used. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.feedback_logs', 'apply_to_target_incident') IS NULL
                THROW 50004, 'Missing dbo.feedback_logs.apply_to_target_incident. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.feedback_logs', 'target_ticket_id') IS NULL
                THROW 50005, 'Missing dbo.feedback_logs.target_ticket_id. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureApplicationFeedbackTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.application_feedback_logs', N'U') IS NULL
                THROW 50006, 'Missing dbo.application_feedback_logs. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}