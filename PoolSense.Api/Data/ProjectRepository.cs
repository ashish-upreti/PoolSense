using Npgsql;
using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface IProjectRepository
{
    Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
}

public class ProjectRepository : IProjectRepository
{
    private readonly IConfiguration _configuration;

    public ProjectRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO project_configs (
                project_id,
                project_name,
                knowledge_lookback_years,
                similarity_search_limit,
                send_email,
                pooling_enabled,
                email_recipients,
                ticket_source_type,
                connection_string,
                knowledge_sources,
                application_filter)
            VALUES (
                @projectId,
                @projectName,
                @knowledgeLookbackYears,
                @similaritySearchLimit,
                @sendEmail,
                @poolingEnabled,
                @emailRecipients,
                @ticketSourceType,
                @connectionString,
                @knowledgeSources,
                @applicationFilter)
            RETURNING id, created_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddProjectParameters(command, projectConfig);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            projectConfig.Id = reader.GetInt32(0);
            projectConfig.CreatedAt = reader.GetFieldValue<DateTime>(1);
        }

        return projectConfig;
    }

    public async Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default)
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
                 project_name,
                 knowledge_lookback_years,
                 similarity_search_limit,
                 send_email,
                 pooling_enabled,
                 email_recipients,
                 created_at,
                 ticket_source_type,
                 connection_string,
                 knowledge_sources,
                 application_filter
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

        return MapProjectConfig(reader);
    }

    public async Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT id,
                   project_id,
                   project_name,
                   knowledge_lookback_years,
                   similarity_search_limit,
                   send_email,
                   pooling_enabled,
                   email_recipients,
                   created_at,
                   ticket_source_type,
                   connection_string,
                   knowledge_sources,
                   application_filter
            FROM project_configs
            ORDER BY project_name ASC;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<ProjectConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapProjectConfig(reader));
        }

        return results;
    }

    public async Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE project_configs
            SET project_name = @projectName,
                knowledge_lookback_years = @knowledgeLookbackYears,
                similarity_search_limit = @similaritySearchLimit,
                send_email = @sendEmail,
                pooling_enabled = @poolingEnabled,
                email_recipients = @emailRecipients,
                ticket_source_type = @ticketSourceType,
                connection_string = @connectionString,
                knowledge_sources = @knowledgeSources,
                application_filter = @applicationFilter
            WHERE project_id = @projectId
            RETURNING id, created_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddProjectParameters(command, projectConfig);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        projectConfig.Id = reader.GetInt32(0);
        projectConfig.CreatedAt = reader.GetFieldValue<DateTime>(1);

        return projectConfig;
    }

    private static void AddProjectParameters(NpgsqlCommand command, ProjectConfig projectConfig)
    {
        command.Parameters.AddWithValue("projectId", projectConfig.ProjectId);
        command.Parameters.AddWithValue("projectName", projectConfig.ProjectName);
        command.Parameters.AddWithValue("knowledgeLookbackYears", projectConfig.KnowledgeLookbackYears);
        command.Parameters.AddWithValue("similaritySearchLimit", projectConfig.SimilaritySearchLimit);
        command.Parameters.AddWithValue("sendEmail", projectConfig.SendEmail);
        command.Parameters.AddWithValue("poolingEnabled", projectConfig.PoolingEnabled);
        command.Parameters.AddWithValue("emailRecipients", projectConfig.EmailRecipients ?? string.Empty);
        command.Parameters.AddWithValue("ticketSourceType", projectConfig.TicketSourceType ?? "sql");
        command.Parameters.AddWithValue("connectionString", projectConfig.ConnectionString ?? string.Empty);
        command.Parameters.AddWithValue("knowledgeSources", projectConfig.KnowledgeSources?.ToArray() ?? []);
        command.Parameters.AddWithValue("applicationFilter", projectConfig.ApplicationFilter ?? string.Empty);
    }

    private static ProjectConfig MapProjectConfig(NpgsqlDataReader reader)
    {
        return new ProjectConfig
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            ProjectName = reader.GetString(2),
            KnowledgeLookbackYears = reader.IsDBNull(3) ? 2 : reader.GetInt32(3),
            SimilaritySearchLimit = reader.IsDBNull(4) ? 5 : reader.GetInt32(4),
            SendEmail = reader.IsDBNull(5) || reader.GetBoolean(5),
            PoolingEnabled = reader.IsDBNull(6) || reader.GetBoolean(6),
            EmailRecipients = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            CreatedAt = reader.IsDBNull(8) ? DateTime.UtcNow : reader.GetFieldValue<DateTime>(8),
            TicketSourceType = reader.IsDBNull(9) ? "sql" : reader.GetString(9),
            ConnectionString = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            KnowledgeSources = reader.IsDBNull(11) ? [] : reader.GetFieldValue<string[]>(11).ToList(),
            ApplicationFilter = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
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
