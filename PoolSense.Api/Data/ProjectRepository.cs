using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;
using System.Text.Json;

namespace PoolSense.Api.Data;

public interface IProjectRepository
{
    Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ProjectConfig?> GetProjectByApplicationFilterAsync(string applicationFilter, CancellationToken cancellationToken = default);
    Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
}

public class ProjectRepository : IProjectRepository
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;
    private readonly IVectorStoreCacheInvalidator _vectorStoreCacheInvalidator;

    public ProjectRepository(
        IPoolSenseSqlConnectionFactory connectionFactory,
        IVectorStoreCacheInvalidator vectorStoreCacheInvalidator)
    {
        _connectionFactory = connectionFactory;
        _vectorStoreCacheInvalidator = vectorStoreCacheInvalidator;
    }

    public async Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.project_configs (
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
                application_filter,
                nyra_kb_names)
            OUTPUT INSERTED.id, INSERTED.created_at
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
                @applicationFilter,
                @nyraKbNames);
            """;

        await using var command = new SqlCommand(sql, connection);
        AddProjectParameters(command, projectConfig);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            projectConfig.Id = reader.GetInt32(0);
            projectConfig.CreatedAt = reader.GetDateTime(1);
        }

        _vectorStoreCacheInvalidator.Invalidate();
        return projectConfig;
    }

    public async Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default)
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
                     application_filter,
                     nyra_kb_names
            FROM dbo.project_configs
            WHERE project_id = @projectId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapProjectConfig(reader);
    }

    public async Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
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
                     application_filter,
                     nyra_kb_names
            FROM dbo.project_configs
            ORDER BY project_name ASC;
            """;

        await using var command = new SqlCommand(sql, connection);

        var results = new List<ProjectConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapProjectConfig(reader));
        }

        return results;
    }

    public async Task<ProjectConfig?> GetProjectByApplicationFilterAsync(string applicationFilter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationFilter))
        {
            return null;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1) id,
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
                     application_filter,
                     nyra_kb_names
            FROM dbo.project_configs
            WHERE application_filter = @applicationFilter;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@applicationFilter", applicationFilter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapProjectConfig(reader);
    }

    public async Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE dbo.project_configs
            SET project_name = @projectName,
                knowledge_lookback_years = @knowledgeLookbackYears,
                similarity_search_limit = @similaritySearchLimit,
                send_email = @sendEmail,
                pooling_enabled = @poolingEnabled,
                email_recipients = @emailRecipients,
                ticket_source_type = @ticketSourceType,
                connection_string = @connectionString,
                knowledge_sources = @knowledgeSources,
                application_filter = @applicationFilter,
                nyra_kb_names = @nyraKbNames
            OUTPUT INSERTED.id, INSERTED.created_at
            WHERE project_id = @projectId;
            """;

        await using var command = new SqlCommand(sql, connection);
        AddProjectParameters(command, projectConfig);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        projectConfig.Id = reader.GetInt32(0);
        projectConfig.CreatedAt = reader.GetDateTime(1);
        _vectorStoreCacheInvalidator.Invalidate();

        return projectConfig;
    }

    private static void AddProjectParameters(SqlCommand command, ProjectConfig projectConfig)
    {
        command.Parameters.AddWithValue("@projectId", projectConfig.ProjectId);
        command.Parameters.AddWithValue("@projectName", projectConfig.ProjectName);
        command.Parameters.AddWithValue("@knowledgeLookbackYears", projectConfig.KnowledgeLookbackYears);
        command.Parameters.AddWithValue("@similaritySearchLimit", projectConfig.SimilaritySearchLimit);
        command.Parameters.AddWithValue("@sendEmail", projectConfig.SendEmail);
        command.Parameters.AddWithValue("@poolingEnabled", projectConfig.PoolingEnabled);
        command.Parameters.AddWithValue("@emailRecipients", projectConfig.EmailRecipients ?? string.Empty);
        command.Parameters.AddWithValue("@ticketSourceType", projectConfig.TicketSourceType ?? "sql");
        command.Parameters.AddWithValue("@connectionString", projectConfig.ConnectionString ?? string.Empty);
        command.Parameters.AddWithValue("@knowledgeSources", JsonSerializer.Serialize(projectConfig.KnowledgeSources ?? []));
        command.Parameters.AddWithValue("@applicationFilter", projectConfig.ApplicationFilter ?? string.Empty);
        command.Parameters.AddWithValue("@nyraKbNames", projectConfig.NyraKbNames ?? string.Empty);
    }

    private static ProjectConfig MapProjectConfig(SqlDataReader reader)
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
            CreatedAt = reader.IsDBNull(8) ? DateTime.UtcNow : reader.GetDateTime(8),
            TicketSourceType = reader.IsDBNull(9) ? "sql" : reader.GetString(9),
            ConnectionString = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            KnowledgeSources = reader.IsDBNull(11) ? [] : DeserializeKnowledgeSources(reader.GetString(11)),
            ApplicationFilter = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            NyraKbNames = reader.IsDBNull(13) ? string.Empty : reader.GetString(13)
        };
    }

    private static List<string> DeserializeKnowledgeSources(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}