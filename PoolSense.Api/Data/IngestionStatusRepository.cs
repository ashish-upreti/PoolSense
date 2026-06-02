using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface IIngestionStatusRepository
{
    Task InitializeStatusAsync(string projectId, int totalTickets, CancellationToken cancellationToken = default);
    Task RefreshStatusAsync(string projectId, int totalTickets, int ingestedTickets, CancellationToken cancellationToken = default);
    Task UpdateProgressAsync(string projectId, int ingestedCount, CancellationToken cancellationToken = default);
    Task<IngestionStatus?> GetStatusAsync(string projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IngestionStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default);
}

public class IngestionStatusRepository : IIngestionStatusRepository
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public IngestionStatusRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeStatusAsync(string projectId, int totalTickets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE dbo.ingestion_status
            SET total_tickets = @totalTickets,
                ingested_tickets = 0,
                last_updated = SYSUTCDATETIME()
            WHERE project_id = @projectId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
                VALUES (@projectId, @totalTickets, 0, SYSUTCDATETIME());
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);
        command.Parameters.AddWithValue("@totalTickets", Math.Max(0, totalTickets));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RefreshStatusAsync(string projectId, int totalTickets, int ingestedTickets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var normalizedTotal = Math.Max(0, totalTickets);
        var normalizedIngested = Math.Min(Math.Max(0, ingestedTickets), normalizedTotal);

        const string sql = """
            UPDATE dbo.ingestion_status
            SET total_tickets = @totalTickets,
                ingested_tickets = @ingestedTickets,
                last_updated = CASE
                    WHEN total_tickets <> @totalTickets OR ingested_tickets <> @ingestedTickets
                    THEN SYSUTCDATETIME()
                    ELSE last_updated
                END
            WHERE project_id = @projectId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
                VALUES (@projectId, @totalTickets, @ingestedTickets, SYSUTCDATETIME());
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);
        command.Parameters.AddWithValue("@totalTickets", normalizedTotal);
        command.Parameters.AddWithValue("@ingestedTickets", normalizedIngested);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProgressAsync(string projectId, int ingestedCount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE dbo.ingestion_status
            SET ingested_tickets = CASE
                    WHEN @ingestedCount > total_tickets THEN total_tickets
                    WHEN @ingestedCount > ingested_tickets THEN @ingestedCount
                    ELSE ingested_tickets
                END,
                last_updated = CASE
                    WHEN CASE
                            WHEN @ingestedCount > total_tickets THEN total_tickets
                            WHEN @ingestedCount > ingested_tickets THEN @ingestedCount
                            ELSE ingested_tickets
                         END > ingested_tickets
                    THEN SYSUTCDATETIME()
                    ELSE last_updated
                END
            WHERE project_id = @projectId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
                VALUES (@projectId, @ingestedCount, @ingestedCount, SYSUTCDATETIME());
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);
        command.Parameters.AddWithValue("@ingestedCount", Math.Max(0, ingestedCount));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IngestionStatus?> GetStatusAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1) id,
                   project_id,
                   total_tickets,
                   ingested_tickets,
                   last_updated
            FROM dbo.ingestion_status
            WHERE project_id = @projectId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapStatus(reader);
    }

    public async Task<IReadOnlyList<IngestionStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT id,
                   project_id,
                   total_tickets,
                   ingested_tickets,
                   last_updated
            FROM dbo.ingestion_status
            ORDER BY project_id ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<IngestionStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapStatus(reader));
        }

        return results;
    }

    private static IngestionStatus MapStatus(SqlDataReader reader)
    {
        return new IngestionStatus
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            TotalTickets = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            IngestedTickets = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            LastUpdated = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4)
        };
    }
}