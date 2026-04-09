using Npgsql;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Models;
using System.Globalization;

namespace PoolSense.Api.Data;

public interface IPgVectorRepository
{
    /// <summary>
    /// Stores ticket knowledge in the vector database.
    /// </summary>
    Task InsertTicketKnowledge(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for tickets with embeddings most similar to the supplied vector.
    /// </summary>
    /// <param name="selectedGroupIds">
    /// null = default ApplicationName scope; empty = all groups; non-empty = filter to those group IDs.
    /// </param>
    Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(float[] embedding, int limit = 5, IReadOnlyList<string>? selectedGroupIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns monthly incident totals for the requested number of months.
    /// </summary>
    Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(int monthCount = 6, CancellationToken cancellationToken = default);
}

public class PgVectorRepository : IPgVectorRepository
{
    private readonly IConfiguration _configuration;
    private readonly TicketAutomationSettings _settings;

    public PgVectorRepository(IConfiguration configuration, IOptions<TicketAutomationSettings> settings)
    {
        _configuration = configuration;
        _settings = settings.Value;
    }

    /// <summary>
    /// Stores ticket knowledge in the vector database.
    /// </summary>
    public async Task InsertTicketKnowledge(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticketKnowledge);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO ticket_knowledge (
                ticket_id,
                source_event_id,
                problem,
                root_cause,
                resolution,
                keywords,
                embedding,
                application,
                knowledge_year,
                source_status,
                source_submitted_at,
                source_closed_at,
                submitter_id,
                lifeguard_id,
                source_project,
                created_at)
            VALUES (
                @ticketId,
                @sourceEventId,
                @problem,
                @rootCause,
                @resolution,
                @keywords,
                CAST(@embedding AS vector),
                @application,
                @knowledgeYear,
                @sourceStatus,
                @sourceSubmittedAt,
                @sourceClosedAt,
                @submitterId,
                @lifeguardId,
                @sourceProject,
                @createdAt);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ticketId", ticketKnowledge.TicketId);
        command.Parameters.AddWithValue("sourceEventId", ticketKnowledge.SourceEventId ?? string.Empty);
        command.Parameters.AddWithValue("problem", ticketKnowledge.Problem);
        command.Parameters.AddWithValue("rootCause", ticketKnowledge.RootCause);
        command.Parameters.AddWithValue("resolution", ticketKnowledge.Resolution);
        command.Parameters.AddWithValue("keywords", ticketKnowledge.Keywords);
        command.Parameters.AddWithValue("embedding", ToPgVectorLiteral(ticketKnowledge.Embedding));
        command.Parameters.AddWithValue("application", ResolveApplication(ticketKnowledge.Application));
        command.Parameters.AddWithValue("knowledgeYear", ResolveKnowledgeYear(ticketKnowledge.KnowledgeYear));
        command.Parameters.AddWithValue("sourceStatus", ticketKnowledge.SourceStatus ?? string.Empty);
        command.Parameters.AddWithValue("sourceSubmittedAt", ticketKnowledge.SourceSubmittedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("sourceClosedAt", ticketKnowledge.SourceClosedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("submitterId", ticketKnowledge.SubmitterId ?? string.Empty);
        command.Parameters.AddWithValue("lifeguardId", ticketKnowledge.LifeguardId ?? string.Empty);
        command.Parameters.AddWithValue("sourceProject", ticketKnowledge.SourceProject ?? string.Empty);
        command.Parameters.AddWithValue("createdAt", ticketKnowledge.CreatedAt == default ? DateTime.UtcNow : ticketKnowledge.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Searches for tickets with embeddings most similar to the supplied vector.
    /// </summary>
    public async Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(float[] embedding, int limit = 5, IReadOnlyList<string>? selectedGroupIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var sql = """
            SELECT id,
                   ticket_id,
                   source_event_id,
                   problem,
                   root_cause,
                   resolution,
                   keywords,
                   embedding::text AS embedding_text,
                   application,
                   knowledge_year,
                   source_status,
                   source_submitted_at,
                   source_closed_at,
                   submitter_id,
                   lifeguard_id,
                   source_project,
                   created_at,
                   1 - (embedding <=> CAST(@embedding AS vector)) AS similarity_score
            FROM ticket_knowledge
            """ + Environment.NewLine;

        sql += BuildScopedWhereClause(selectedGroupIds);
        sql += Environment.NewLine + """
            ORDER BY embedding <=> CAST(@embedding AS vector)
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("embedding", ToPgVectorLiteral(embedding));
        command.Parameters.AddWithValue("limit", limit);
        ApplyScopeParameters(command, selectedGroupIds);

        var results = new List<TicketKnowledge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TicketKnowledge
            {
                Id = reader.GetInt32(0),
                TicketId = reader.GetString(1),
                SourceEventId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Problem = reader.GetString(3),
                RootCause = reader.GetString(4),
                Resolution = reader.GetString(5),
                Keywords = reader.IsDBNull(6) ? [] : reader.GetFieldValue<string[]>(6),
                Embedding = reader.IsDBNull(7) ? [] : ParsePgVector(reader.GetString(7)),
                Application = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                KnowledgeYear = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                SourceStatus = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                SourceSubmittedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                SourceClosedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                SubmitterId = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                LifeguardId = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                SourceProject = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                CreatedAt = reader.IsDBNull(16) ? default : reader.GetDateTime(16),
                Similarity = reader.IsDBNull(17) ? 0 : reader.GetDouble(17)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns monthly incident totals for the requested number of months.
    /// </summary>
    public async Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(int monthCount = 6, CancellationToken cancellationToken = default)
    {
        if (monthCount <= 0)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var sql = """
            SELECT DATE_TRUNC('month', created_at) AS month_start,
                   COUNT(*)::int AS incidents
            FROM ticket_knowledge
            WHERE created_at >= DATE_TRUNC('month', CURRENT_DATE) - (@monthCount - 1) * INTERVAL '1 month'
            GROUP BY DATE_TRUNC('month', created_at)
            ORDER BY month_start ASC;
            """;

        if (!string.IsNullOrWhiteSpace(_settings.ApplicationName))
        {
            sql = sql.Replace(
                "WHERE created_at >=",
                "WHERE application = @application AND created_at >=");
        }

        if (_settings.KnowledgeLookbackYears > 0)
        {
            sql = sql.Replace(
                "GROUP BY",
                "AND knowledge_year >= @minimumKnowledgeYear GROUP BY");
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("monthCount", monthCount);
        ApplyScopeParameters(command);

        var results = new List<IncidentTimelinePoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var monthStart = reader.GetDateTime(0);
            results.Add(new IncidentTimelinePoint(monthStart.ToString("MMM", CultureInfo.InvariantCulture), reader.GetInt32(1)));
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

    private static string ToPgVectorLiteral(float[] embedding)
    {
        return $"[{string.Join(',', embedding.Select(value => value.ToString(CultureInfo.InvariantCulture)))}]";
    }

    private static float[] ParsePgVector(string value)
    {
        var trimmed = value.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => float.Parse(item, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private string BuildScopedWhereClause(IReadOnlyList<string>? selectedGroupIds = null)
    {
        var clauses = new List<string>();

        var appClause = BuildApplicationClause(selectedGroupIds);
        if (!string.IsNullOrEmpty(appClause))
        {
            clauses.Add(appClause);
        }

        if (_settings.KnowledgeLookbackYears > 0)
        {
            clauses.Add("knowledge_year >= @minimumKnowledgeYear");
        }

        return clauses.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", clauses)}{Environment.NewLine}";
    }

    /// <summary>
    /// Builds the application part of the WHERE clause.
    /// null selectedGroupIds = use configured ApplicationName (default scope).
    /// empty list = no application filter (All).
    /// non-empty list = OR-combine each matching group's ApplicationFilter.
    /// </summary>
    private string BuildApplicationClause(IReadOnlyList<string>? selectedGroupIds)
    {
        if (selectedGroupIds == null)
        {
            // Default: use single ApplicationName from settings
            return string.IsNullOrWhiteSpace(_settings.ApplicationName)
                ? string.Empty
                : "application = @application";
        }

        if (selectedGroupIds.Count == 0)
        {
            // All groups selected — no application scope
            return string.Empty;
        }

        var subClauses = new List<string>();
        int i = 0;
        foreach (var groupId in selectedGroupIds)
        {
            var group = _settings.ProjectGroups.FirstOrDefault(g => string.Equals(g.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null || string.IsNullOrWhiteSpace(group.ApplicationFilter)) continue;
            var op = group.ApplicationFilter.Contains('%') ? "ILIKE" : "=";
            subClauses.Add($"application {op} @appFilter{i++}");
        }

        return subClauses.Count == 0
            ? string.Empty
            : subClauses.Count == 1
                ? subClauses[0]
                : $"({string.Join(" OR ", subClauses)})";
    }

    private void ApplyScopeParameters(NpgsqlCommand command, IReadOnlyList<string>? selectedGroupIds = null)
    {
        if (selectedGroupIds == null)
        {
            // Default: use ApplicationName
            if (!string.IsNullOrWhiteSpace(_settings.ApplicationName) && !command.Parameters.Contains("application"))
            {
                command.Parameters.AddWithValue("application", _settings.ApplicationName);
            }
        }
        else if (selectedGroupIds.Count > 0)
        {
            int i = 0;
            foreach (var groupId in selectedGroupIds)
            {
                var group = _settings.ProjectGroups.FirstOrDefault(g => string.Equals(g.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
                if (group == null || string.IsNullOrWhiteSpace(group.ApplicationFilter)) continue;
                var paramName = $"appFilter{i++}";
                if (!command.Parameters.Contains(paramName))
                {
                    command.Parameters.AddWithValue(paramName, group.ApplicationFilter);
                }
            }
        }
        // else empty = All = no app params needed

        if (_settings.KnowledgeLookbackYears > 0 && !command.Parameters.Contains("minimumKnowledgeYear"))
        {
            command.Parameters.AddWithValue("minimumKnowledgeYear", GetMinimumKnowledgeYear());
        }
    }

    private int GetMinimumKnowledgeYear()
    {
        var lookbackYears = Math.Max(1, _settings.KnowledgeLookbackYears);
        return DateTime.UtcNow.Year - (lookbackYears - 1);
    }

    private string ResolveApplication(string application)
    {
        return string.IsNullOrWhiteSpace(application)
            ? _settings.ApplicationName
            : application;
    }

    private int ResolveKnowledgeYear(int knowledgeYear)
    {
        return knowledgeYear > 0
            ? knowledgeYear
            : DateTime.UtcNow.Year;
    }
}