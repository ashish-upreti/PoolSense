using Npgsql;
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
    private readonly IConfiguration _configuration;

    public IngestionStatusRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task InitializeStatusAsync(string projectId, int totalTickets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
            VALUES (@projectId, @totalTickets, 0, CURRENT_TIMESTAMP)
            ON CONFLICT (project_id) DO UPDATE
            SET total_tickets = EXCLUDED.total_tickets,
                ingested_tickets = 0,
                last_updated = CURRENT_TIMESTAMP;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId);
        command.Parameters.AddWithValue("totalTickets", Math.Max(0, totalTickets));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RefreshStatusAsync(string projectId, int totalTickets, int ingestedTickets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var normalizedTotal = Math.Max(0, totalTickets);
        var normalizedIngested = Math.Min(Math.Max(0, ingestedTickets), normalizedTotal);

        const string sql = """
            INSERT INTO ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
            VALUES (@projectId, @totalTickets, @ingestedTickets, CURRENT_TIMESTAMP)
            ON CONFLICT (project_id) DO UPDATE
            SET total_tickets = EXCLUDED.total_tickets,
                ingested_tickets = EXCLUDED.ingested_tickets,
                last_updated = CASE
                    WHEN ingestion_status.total_tickets IS DISTINCT FROM EXCLUDED.total_tickets
                        OR ingestion_status.ingested_tickets IS DISTINCT FROM EXCLUDED.ingested_tickets
                    THEN CURRENT_TIMESTAMP
                    ELSE ingestion_status.last_updated
                END;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId);
        command.Parameters.AddWithValue("totalTickets", normalizedTotal);
        command.Parameters.AddWithValue("ingestedTickets", normalizedIngested);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProgressAsync(string projectId, int ingestedCount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO ingestion_status (project_id, total_tickets, ingested_tickets, last_updated)
            VALUES (@projectId, @ingestedCount, @ingestedCount, CURRENT_TIMESTAMP)
            ON CONFLICT (project_id) DO UPDATE
            SET ingested_tickets = GREATEST(
                    ingestion_status.ingested_tickets,
                    LEAST(@ingestedCount, ingestion_status.total_tickets)),
                last_updated = CASE
                    WHEN GREATEST(
                        ingestion_status.ingested_tickets,
                        LEAST(@ingestedCount, ingestion_status.total_tickets)) > ingestion_status.ingested_tickets
                    THEN CURRENT_TIMESTAMP
                    ELSE ingestion_status.last_updated
                END;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId);
        command.Parameters.AddWithValue("ingestedCount", Math.Max(0, ingestedCount));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IngestionStatus?> GetStatusAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT id,
                   project_id,
                   total_tickets,
                   ingested_tickets,
                   last_updated
            FROM ingestion_status
            WHERE project_id = @projectId
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapStatus(reader);
    }

    public async Task<IReadOnlyList<IngestionStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT id,
                   project_id,
                   total_tickets,
                   ingested_tickets,
                   last_updated
            FROM ingestion_status
            ORDER BY project_id ASC;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<IngestionStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapStatus(reader));
        }

        return results;
    }

    private static IngestionStatus MapStatus(NpgsqlDataReader reader)
    {
        return new IngestionStatus
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            TotalTickets = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            IngestedTickets = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            LastUpdated = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetFieldValue<DateTime>(4)
        };
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