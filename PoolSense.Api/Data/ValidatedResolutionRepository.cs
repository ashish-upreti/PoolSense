using Microsoft.Data.SqlClient;
using PoolSense.Api.Feedback;

namespace PoolSense.Api.Data;

public interface IValidatedResolutionRepository
{
    Task<int> AddAsync(ValidatedResolution resolution, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>>> GetByTicketIdsAsync(
        IReadOnlyCollection<string> ticketIds,
        CancellationToken cancellationToken = default);
}

public sealed class ValidatedResolutionRepository : IValidatedResolutionRepository
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public ValidatedResolutionRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> AddAsync(ValidatedResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo.validated_resolutions (
                target_ticket_id, current_issue_id, confirmed_note,
                feedback_type, was_used, created_at)
            OUTPUT INSERTED.id
            VALUES (
                @targetTicketId, @currentIssueId, @confirmedNote,
                @feedbackType, @wasUsed, @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@targetTicketId", resolution.TargetTicketId);
        command.Parameters.AddWithValue("@currentIssueId", resolution.CurrentIssueId);
        command.Parameters.AddWithValue("@confirmedNote", resolution.ConfirmedNote);
        command.Parameters.AddWithValue("@feedbackType", resolution.FeedbackType);
        command.Parameters.AddWithValue("@wasUsed", resolution.WasUsed);
        command.Parameters.AddWithValue("@createdAt", resolution.CreatedAt == default ? DateTime.UtcNow : resolution.CreatedAt);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>>> GetByTicketIdsAsync(
        IReadOnlyCollection<string> ticketIds,
        CancellationToken cancellationToken = default)
    {
        if (ticketIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ValidatedResolution>>(StringComparer.OrdinalIgnoreCase);

        var normalizedIds = ticketIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedIds.Length == 0)
            return new Dictionary<string, IReadOnlyList<ValidatedResolution>>(StringComparer.OrdinalIgnoreCase);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var paramNames = normalizedIds.Select((_, i) => $"@t{i}").ToArray();
        var sql = $"""
            SELECT id, target_ticket_id, current_issue_id, confirmed_note, feedback_type, was_used, created_at
            FROM dbo.validated_resolutions
            WHERE target_ticket_id IN ({string.Join(", ", paramNames)})
            ORDER BY created_at DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < normalizedIds.Length; i++)
            command.Parameters.AddWithValue(paramNames[i], normalizedIds[i]);

        var byTicketId = new Dictionary<string, List<ValidatedResolution>>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var vr = new ValidatedResolution
            {
                Id = reader.GetInt32(0),
                TargetTicketId = reader.GetString(1),
                CurrentIssueId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                ConfirmedNote = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                FeedbackType = reader.GetInt32(4),
                WasUsed = reader.GetBoolean(5),
                CreatedAt = reader.GetDateTime(6)
            };

            if (!byTicketId.TryGetValue(vr.TargetTicketId, out var list))
            {
                list = [];
                byTicketId[vr.TargetTicketId] = list;
            }
            list.Add(vr);
        }

        return byTicketId.ToDictionary(
            e => e.Key,
            e => (IReadOnlyList<ValidatedResolution>)e.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.validated_resolutions', N'U') IS NULL
                THROW 50003, 'Missing dbo.validated_resolutions. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.validated_resolutions', 'target_ticket_id') IS NULL
                THROW 50004, 'Missing dbo.validated_resolutions.target_ticket_id. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.validated_resolutions', 'confirmed_note') IS NULL
                THROW 50005, 'Missing dbo.validated_resolutions.confirmed_note. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
