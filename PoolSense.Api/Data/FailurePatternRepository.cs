using Microsoft.Data.SqlClient;
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
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;
    private readonly IProjectRepository _projectRepository;

    public FailurePatternRepository(IPoolSenseSqlConnectionFactory connectionFactory, IProjectRepository projectRepository)
    {
        _connectionFactory = connectionFactory;
        _projectRepository = projectRepository;
    }

    public async Task InsertFailurePattern(FailurePattern failurePattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failurePattern);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.failure_patterns (
                system,
                component,
                failure_type,
                resolution_category,
                ticket_id,
                source_event_id,
                application,
                knowledge_year,
                created_at)
            VALUES (
                @system,
                @component,
                @failureType,
                @resolutionCategory,
                @ticketId,
                @sourceEventId,
                @application,
                @knowledgeYear,
                @createdAt);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@system", failurePattern.System ?? string.Empty);
        command.Parameters.AddWithValue("@component", failurePattern.Component ?? string.Empty);
        command.Parameters.AddWithValue("@failureType", failurePattern.FailureType ?? string.Empty);
        command.Parameters.AddWithValue("@resolutionCategory", failurePattern.ResolutionCategory ?? string.Empty);
        command.Parameters.AddWithValue("@ticketId", failurePattern.TicketId ?? string.Empty);
        command.Parameters.AddWithValue("@sourceEventId", failurePattern.SourceEventId ?? string.Empty);
        command.Parameters.AddWithValue("@application", failurePattern.Application ?? string.Empty);
        command.Parameters.AddWithValue("@knowledgeYear", failurePattern.KnowledgeYear > 0 ? failurePattern.KnowledgeYear : DateTime.UtcNow.Year);
        command.Parameters.AddWithValue("@createdAt", failurePattern.CreatedAt == default ? DateTime.UtcNow : failurePattern.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FailurePattern>> GetPatternsBySystem(string system, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return [];
        }

        await using var connection = _connectionFactory.CreateConnection();
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
            FROM dbo.failure_patterns
            WHERE system = @system
            ORDER BY created_at DESC;
            """;

        sql = ApplyScopeToWhereClause(sql, "WHERE system = @system", scopedProjects);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@system", system);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<FailurePattern>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapFailurePattern(reader));
        }

        return results;
    }

    public async Task<int> CountPatternOccurrences(string system, string failureType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(failureType))
        {
            return 0;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT COUNT(*)
            FROM dbo.failure_patterns
            WHERE system = @system
              AND failure_type = @failureType
            """;

        sql = ApplyScopeToWhereClause(sql, "WHERE system = @system\n              AND failure_type = @failureType", scopedProjects);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@system", system);
        command.Parameters.AddWithValue("@failureType", failureType);
        ApplyScopeParameters(command, scopedProjects);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<FailureTypeFrequency>> GetMostFrequentFailureTypes(int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT TOP (@limit) failure_type,
                   COUNT(*) AS occurrence_count
            FROM dbo.failure_patterns
            GROUP BY failure_type
            ORDER BY occurrence_count DESC, failure_type ASC;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<FailureTypeFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FailureTypeFrequency
            {
                FailureType = reader.GetString(0),
                Count = Convert.ToInt32(reader.GetValue(1))
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

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT TOP (@limit) component,
                   COUNT(*) AS occurrence_count
            FROM dbo.failure_patterns
            GROUP BY component
            ORDER BY occurrence_count DESC, component ASC;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<ComponentFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ComponentFrequency
            {
                Component = reader.GetString(0),
                Count = Convert.ToInt32(reader.GetValue(1))
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

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(cancellationToken);

        var sql = """
            SELECT TOP (@limit) system,
                   COUNT(*) AS occurrence_count
            FROM dbo.failure_patterns
            GROUP BY system
            HAVING COUNT(*) >= @minimumIncidentCount
            ORDER BY occurrence_count DESC, system ASC;
            """;

        sql = ApplyScopeToGroupByClause(sql, scopedProjects);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@minimumIncidentCount", minimumIncidentCount);
        command.Parameters.AddWithValue("@limit", limit);
        ApplyScopeParameters(command, scopedProjects);

        var results = new List<SystemIncidentFrequency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SystemIncidentFrequency
            {
                System = reader.GetString(0),
                Count = Convert.ToInt32(reader.GetValue(1))
            });
        }

        return results;
    }

    private static FailurePattern MapFailurePattern(SqlDataReader reader)
    {
        return new FailurePattern
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
            CreatedAt = reader.IsDBNull(9) ? DateTime.UtcNow : reader.GetDateTime(9)
        };
    }

    private string ApplyScopeToWhereClause(string sql, string whereClause, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var scopedConditions = BuildScopeConditions(scopedProjects);
        if (scopedConditions.Count == 0)
        {
            return sql;
        }

        return sql.Replace(whereClause, $"{whereClause} AND {string.Join(" AND ", scopedConditions)}", StringComparison.Ordinal);
    }

    private string ApplyScopeToGroupByClause(string sql, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var scopedConditions = BuildScopeConditions(scopedProjects);
        if (scopedConditions.Count == 0)
        {
            return sql;
        }

        return sql.Replace("GROUP BY", $"WHERE {string.Join(" AND ", scopedConditions)} GROUP BY", StringComparison.Ordinal);
    }

    private static List<string> BuildScopeConditions(IReadOnlyList<ProjectConfig> scopedProjects)
    {
        var conditions = new List<string>();

        for (var index = 0; index < scopedProjects.Count; index++)
        {
            var project = scopedProjects[index];
            if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
            {
                continue;
            }

            var appOperator = project.ApplicationFilter.Contains('%') || project.ApplicationFilter.Contains('_') ? "LIKE" : "=";
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

    private static void ApplyScopeParameters(SqlCommand command, IReadOnlyList<ProjectConfig> scopedProjects)
    {
        for (var index = 0; index < scopedProjects.Count; index++)
        {
            var project = scopedProjects[index];
            if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
            {
                continue;
            }

            var appFilterParameter = $"@appFilter{index}";
            if (!command.Parameters.Contains(appFilterParameter))
            {
                command.Parameters.AddWithValue(appFilterParameter, project.ApplicationFilter);
            }

            if (project.KnowledgeLookbackYears > 0)
            {
                var minimumYearParameter = $"@minimumKnowledgeYear{index}";
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