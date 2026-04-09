using Npgsql;
using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface IProjectRepository
{
    Task RegisterProject(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
    Task<ProjectConfig?> GetProjectConfiguration(string projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectConfig>> ListActiveProjects(CancellationToken cancellationToken = default);
}

public class ProjectRepository : IProjectRepository
{
    private readonly IConfiguration _configuration;

    public ProjectRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task RegisterProject(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO project_configs (project_id, project_name, ticket_source_type, connection_string, knowledge_sources, is_active)
            VALUES (@projectId, @projectName, @ticketSourceType, @connectionString, @knowledgeSources, @isActive);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectConfig.ProjectId);
        command.Parameters.AddWithValue("projectName", projectConfig.ProjectName);
        command.Parameters.AddWithValue("ticketSourceType", projectConfig.TicketSourceType);
        command.Parameters.AddWithValue("connectionString", projectConfig.ConnectionString);
        command.Parameters.AddWithValue("knowledgeSources", projectConfig.KnowledgeSources.ToArray());
        command.Parameters.AddWithValue("isActive", projectConfig.IsActive);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProjectConfig?> GetProjectConfiguration(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT project_id,
                   project_name,
                   ticket_source_type,
                   connection_string,
                   knowledge_sources,
                   is_active
            FROM project_configs
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

        return new ProjectConfig
        {
            ProjectId = reader.GetString(0),
            ProjectName = reader.GetString(1),
            TicketSourceType = reader.GetString(2),
            ConnectionString = reader.GetString(3),
            KnowledgeSources = reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4).ToList(),
            IsActive = reader.GetBoolean(5)
        };
    }

    public async Task<IReadOnlyList<ProjectConfig>> ListActiveProjects(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT project_id,
                   project_name,
                   ticket_source_type,
                   connection_string,
                   knowledge_sources,
                   is_active
            FROM project_configs
            WHERE is_active = TRUE
            ORDER BY project_name ASC;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<ProjectConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ProjectConfig
            {
                ProjectId = reader.GetString(0),
                ProjectName = reader.GetString(1),
                TicketSourceType = reader.GetString(2),
                ConnectionString = reader.GetString(3),
                KnowledgeSources = reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4).ToList(),
                IsActive = reader.GetBoolean(5)
            });
        }

        return results;
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
