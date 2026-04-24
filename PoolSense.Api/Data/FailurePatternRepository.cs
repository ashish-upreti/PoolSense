using Microsoft.Extensions.Configuration;
using Npgsql;
using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface IFailurePatternRepository
{
    Task InsertFailurePattern(FailurePattern failurePattern, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FailurePattern>> GetPatternsBySystem(string system, CancellationToken cancellationToken = default);
    Task<int> CountPatternOccurrences(string system, string failureType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FailureTypeFrequency>> GetMostFrequentFailureTypes(int limit = 10, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComponentFrequency>> GetMostProblematicComponents(int limit = 10, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemIncidentFrequency>> GetSystemsWithRepeatedIncidents(int minimumIncidentCount = 2, int limit = 10, CancellationToken cancellationToken = default);
}

public sealed class FailureTypeFrequency
{
    public string FailureType { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class ComponentFrequency
{
    public string Component { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class SystemIncidentFrequency
{
    public string System { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class FailurePatternRepository : IFailurePatternRepository
{
    private readonly IConfiguration _configuration;
    private readonly IProjectRepository _projectRepository;

    public FailurePatternRepository(IConfiguration configuration, IProjectRepository projectRepository)
    {
        _configuration = configuration;
        _projectRepository = projectRepository;
    }

    public async Task InsertFailurePattern(FailurePattern failurePattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failurePattern);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO failure_patterns (system, component, failure_type, resolution_category, ticket_id, source_event_id, application, knowledge_year, created_at)
            VALUES (@system, @component, @failureType, @resolutionCategory, @ticketId, @sourceEventId, @application, @knowledgeYear, @createdAt);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("system", failurePattern.System);
        command.Parameters.AddWithValue("component", failurePattern.Component);
        command.Parameters.AddWithValue("failureType", failurePattern.FailureType);
        command.Parameters.AddWithValue("resolutionCategory", failurePattern.ResolutionCategory);
        command.Parameters.AddWithValue("ticketId", failurePattern.TicketId);
        command.Parameters.AddWithValue("sourceEventId", failurePattern.SourceEventId ?? string.Empty);
        command.Parameters.AddWithValue("application", failurePattern.Application ?? string.Empty);
        command.Parameters.AddWithValue("knowledgeYear", failurePattern.KnowledgeYear > 0 ? failurePattern.KnowledgeYear : DateTime.UtcNow.Year);
        command.Parameters.AddWithValue("createdAt", failurePattern.CreatedAt == default ? DateTime.UtcNow : failurePattern.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FailurePattern>> GetPatternsBySystem(string system, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT id,
                   system,
                   component,
                   failure_type,
                   resolution_category,
                   ticket_id,
                 source_event_id,
                 application,
                 knowledge_year,
                 created_at
            FROM failure_patterns
            WHERE system = @system
            ORDER BY created_at DESC;
            """;

        sql = ApplyScopeToWhereClause(sql, "WHERE system = @system", scopedProjects);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("system", system);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<FailurePattern>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FailurePattern
            {
                Id = reader.GetInt32(0),
                System = reader.GetString(1),
                Component = reader.GetString(2),
                FailureType = reader.GetString(3),
                ResolutionCategory = reader.GetString(4),
                TicketId = reader.GetString(5),
                SourceEventId = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Application = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                KnowledgeYear = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                CreatedAt = reader.GetDateTime(9)
            });
        }

        return results;
    }

    public async Task<int> CountPatternOccurrences(string system, string failureType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(failureType))
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT COUNT(*)
            FROM failure_patterns
            WHERE system = @system
              AND failure_type = @failureType
            """;

                sql = ApplyScopeToWhereClause(sql, "WHERE system = @system\n              AND failure_type = @failureType", scopedProjects);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("system", system);
        command.Parameters.AddWithValue("failureType", failureType);
                ApplyScopeParameters(command, scopedProjects);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    public async Task<IReadOnlyList<FailureTypeFrequency>> GetMostFrequentFailureTypes(int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT failure_type,
                   COUNT(*) AS occurrence_count
            FROM failure_patterns
            GROUP BY failure_type
            ORDER BY occurrence_count DESC, failure_type ASC
            LIMIT @limit;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<FailureTypeFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FailureTypeFrequency
            {
                FailureType = reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<ComponentFrequency>> GetMostProblematicComponents(int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT component,
                   COUNT(*) AS occurrence_count
            FROM failure_patterns
            GROUP BY component
            ORDER BY occurrence_count DESC, component ASC
            LIMIT @limit;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<ComponentFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ComponentFrequency
            {
                Component = reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<SystemIncidentFrequency>> GetSystemsWithRepeatedIncidents(int minimumIncidentCount = 2, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (minimumIncidentCount <= 1 || limit <= 0)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT system,
                   COUNT(*) AS occurrence_count
            FROM failure_patterns
            GROUP BY system
            HAVING COUNT(*) >= @minimumIncidentCount
            ORDER BY occurrence_count DESC, system ASC
            LIMIT @limit;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("minimumIncidentCount", minimumIncidentCount);
        command.Parameters.AddWithValue("limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<SystemIncidentFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SystemIncidentFrequency
            {
                System = reader.GetString(0),
                Count = reader.GetInt32(1)
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

    private string ApplyScopeToWhereClause(string sql, string whereClause, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var scopedConditions = BuildScopeConditions(scopedProjects);
        if (scopedConditions.Count == 0)
        {
            return sql;
        }

        return sql.Replace(whereClause, $"{whereClause} AND {string.Join(" AND ", scopedConditions)}");
    }

    private string ApplyScopeToGroupByClause(string sql, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var scopedConditions = BuildScopeConditions(scopedProjects);
        if (scopedConditions.Count == 0)
        {
            return sql;
        }

        return sql.Replace("GROUP BY", $"WHERE {string.Join(" AND ", scopedConditions)} GROUP BY");
    }

    private List<string> BuildScopeConditions(IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var conditions = new List<string>();

        for (var index = 0; index < scopedProjects.Count; index++)
        {
            var project = scopedProjects[index];
            if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
            {
                continue;
            }

            var appOperator = project.ApplicationFilter.Contains('%') ? "ILIKE" : "=";
            var projectConditions = new List<string>
            {
                $"application {appOperator} @appFilter{index}"
            };

            if (project.KnowledgeLookbackYears > 0)
            {
                projectConditions.Add($"knowledge_year >= @minimumKnowledgeYear{index}");
            }

            conditions.Add(projectConditions.Count == 1
                ? projectConditions[0]
                : $"({string.Join(" AND ", projectConditions)})");
        }

        return conditions;
    }

    private void ApplyScopeParameters(NpgsqlCommand command, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        for (var index = 0; index < scopedProjects.Count; index++)
        {
            var project = scopedProjects[index];
            if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
            {
                continue;
            }

            var appFilterParameter = $"appFilter{index}";
            if (!command.Parameters.Contains(appFilterParameter))
            {
                command.Parameters.AddWithValue(appFilterParameter, project.ApplicationFilter);
            }

            if (project.KnowledgeLookbackYears > 0)
            {
                var minimumYearParameter = $"minimumKnowledgeYear{index}";
                if (!command.Parameters.Contains(minimumYearParameter))
                {
                    command.Parameters.AddWithValue(minimumYearParameter, GetMinimumKnowledgeYear(project.KnowledgeLookbackYears));
                }
            }
        }
    }

    private async Task<IReadOnlyList<ProjectConfig>> GetScopedProjectsAsync(CancellationToken cancellationToken)
    {
        return (await _projectRepository.GetAllProjectsAsync(cancellationToken))
            .Where(project => !string.IsNullOrWhiteSpace(project.ApplicationFilter))
            .ToList();
    }

    private static int GetMinimumKnowledgeYear(int lookbackYears)
    {
        var normalizedLookbackYears = Math.Max(1, lookbackYears);
        return DateTime.UtcNow.Year - (normalizedLookbackYears - 1);
    }
}
