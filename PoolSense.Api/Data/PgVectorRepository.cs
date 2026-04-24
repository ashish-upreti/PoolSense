using Npgsql;
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
    /// null or empty = search across all configured projects; non-empty = filter to those project IDs.
    /// </param>
    Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(float[] embedding, int limit = 5, IReadOnlyList<string>? selectedGroupIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the accumulated feedback score for a retrieved ticket.
    /// </summary>
    Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns monthly incident totals for the requested number of months.
    /// </summary>
    Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(int monthCount = 6, CancellationToken cancellationToken = default);
}

public class PgVectorRepository : IPgVectorRepository
{
    private const int FeedbackRerankMultiplier = 5;
    private const double StrongHelpfulWeight = 0.10d;
    private const double WeakHelpfulWeight = 0.05d;
    private const double NotHelpfulPenalty = -0.05d;

    private readonly IConfiguration _configuration;
    private readonly IProjectRepository _projectRepository;
    private readonly IFeedbackRepository _feedbackRepository;

    public PgVectorRepository(IConfiguration configuration, IProjectRepository projectRepository, IFeedbackRepository feedbackRepository)
    {
        _configuration = configuration;
        _projectRepository = projectRepository;
        _feedbackRepository = feedbackRepository;
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

        if (limit <= 0)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureFeedbackTableAsync(connection, cancellationToken);

        var scopedProjects = await GetScopedProjectsAsync(selectedGroupIds, cancellationToken);
        var requireProjectMatch = selectedGroupIds is { Count: > 0 };

        var candidateLimit = Math.Max(limit, limit * FeedbackRerankMultiplier);

        var sql = """
            WITH ranked_candidates AS (
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
                       1 - (embedding <=> CAST(@embedding AS vector)) AS vector_similarity
                FROM ticket_knowledge
            """ + Environment.NewLine;

            sql += BuildScopedWhereClause(scopedProjects, requireProjectMatch);
        sql += Environment.NewLine + """
                ORDER BY embedding <=> CAST(@embedding AS vector)
                LIMIT @candidateLimit
            ),
            feedback_scores AS (
                SELECT TRIM(ticket_id) AS ticket_id,
                       SUM(
                           CASE
                               WHEN feedback_type = 1 AND was_used THEN @strongHelpfulWeight
                               WHEN feedback_type = 1 THEN @weakHelpfulWeight
                               ELSE @notHelpfulPenalty
                           END) AS feedback_weight
                FROM feedback_logs
                CROSS JOIN LATERAL UNNEST(string_to_array(retrieved_ticket_ids, ',')) AS ticket_id
                GROUP BY TRIM(ticket_id)
            )
            SELECT candidate.id,
                   candidate.ticket_id,
                   candidate.source_event_id,
                   candidate.problem,
                   candidate.root_cause,
                   candidate.resolution,
                   candidate.keywords,
                   candidate.embedding_text,
                   candidate.application,
                   candidate.knowledge_year,
                   candidate.source_status,
                   candidate.source_submitted_at,
                   candidate.source_closed_at,
                   candidate.submitter_id,
                   candidate.lifeguard_id,
                   candidate.source_project,
                   candidate.created_at,
                   candidate.vector_similarity + COALESCE(feedback_scores.feedback_weight, 0) AS similarity_score
            FROM ranked_candidates candidate
            LEFT JOIN feedback_scores
                ON feedback_scores.ticket_id = candidate.ticket_id
            ORDER BY similarity_score DESC, candidate.ticket_id ASC
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("embedding", ToPgVectorLiteral(embedding));
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("candidateLimit", candidateLimit);
        command.Parameters.AddWithValue("strongHelpfulWeight", StrongHelpfulWeight);
        command.Parameters.AddWithValue("weakHelpfulWeight", WeakHelpfulWeight);
        command.Parameters.AddWithValue("notHelpfulPenalty", NotHelpfulPenalty);
        ApplyScopeParameters(command, scopedProjects);

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
    /// Returns the accumulated feedback score for a retrieved ticket.
    /// </summary>
    public Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
    {
        return _feedbackRepository.GetFeedbackScore(ticketId, cancellationToken);
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

        var scopedProjects = await GetScopedProjectsAsync([], cancellationToken);
        var projectScopeCondition = BuildProjectScopeCondition(scopedProjects);

        var sql = """
            SELECT DATE_TRUNC('month', created_at) AS month_start,
                   COUNT(*)::int AS incidents
            FROM ticket_knowledge
            WHERE created_at >= DATE_TRUNC('month', CURRENT_DATE) - (@monthCount - 1) * INTERVAL '1 month'
            GROUP BY DATE_TRUNC('month', created_at)
            ORDER BY month_start ASC;
            """;

        if (!string.IsNullOrWhiteSpace(projectScopeCondition))
        {
            sql = sql.Replace(
                "WHERE created_at >=",
                $"WHERE {projectScopeCondition} AND created_at >=");
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("monthCount", monthCount);
        ApplyScopeParameters(command, scopedProjects);

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

    private static async Task EnsureFeedbackTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS feedback_logs (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ticket_query text NOT NULL,
                suggested_resolution text NOT NULL,
                feedback_type integer NOT NULL,
                was_used boolean NOT NULL DEFAULT FALSE,
                comment text NOT NULL DEFAULT '',
                retrieved_ticket_ids text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );

            ALTER TABLE IF EXISTS feedback_logs
                ADD COLUMN IF NOT EXISTS was_used boolean NOT NULL DEFAULT FALSE;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private string BuildScopedWhereClause(IReadOnlyList<ProjectConfig> scopedProjects, bool requireProjectMatch)
    {
        if (scopedProjects.Count == 0)
        {
            return requireProjectMatch
                ? $"WHERE 1 = 0{Environment.NewLine}"
                : string.Empty;
        }

        var scopeCondition = BuildProjectScopeCondition(scopedProjects);
        return string.IsNullOrWhiteSpace(scopeCondition)
            ? string.Empty
            : $"WHERE {scopeCondition}{Environment.NewLine}";
    }

    private string BuildProjectScopeCondition(IReadOnlyList<ProjectConfig> scopedProjects)
    {
        if (scopedProjects.Count == 0)
        {
            return string.Empty;
        }

        var projectClauses = new List<string>();

        for (var index = 0; index < scopedProjects.Count; index++)
        {
            var project = scopedProjects[index];
            if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
            {
                continue;
            }

            var appOperator = project.ApplicationFilter.Contains('%') ? "ILIKE" : "=";
            var conditions = new List<string>
            {
                $"application {appOperator} @appFilter{index}"
            };

            if (project.KnowledgeLookbackYears > 0)
            {
                conditions.Add($"knowledge_year >= @minimumKnowledgeYear{index}");
            }

            projectClauses.Add(conditions.Count == 1
                ? conditions[0]
                : $"({string.Join(" AND ", conditions)})");
        }

        return projectClauses.Count == 0
            ? string.Empty
            : projectClauses.Count == 1
                ? projectClauses[0]
                : $"({string.Join(" OR ", projectClauses)})";
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

    private async Task<IReadOnlyList<ProjectConfig>> GetScopedProjectsAsync(IReadOnlyList<string>? selectedProjectIds, CancellationToken cancellationToken)
    {
        var projects = (await _projectRepository.GetAllProjectsAsync(cancellationToken))
            .Where(project => !string.IsNullOrWhiteSpace(project.ApplicationFilter))
            .ToList();

        if (selectedProjectIds is not { Count: > 0 })
        {
            return projects;
        }

        var selectedProjectIdSet = selectedProjectIds
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return projects
            .Where(project => selectedProjectIdSet.Contains(project.ProjectId))
            .ToList();
    }

    private string ResolveApplication(string application)
    {
        return string.IsNullOrWhiteSpace(application)
            ? string.Empty
            : application;
    }

    private static int GetMinimumKnowledgeYear(int lookbackYears)
    {
        var normalizedLookbackYears = Math.Max(1, lookbackYears);
        return DateTime.UtcNow.Year - (normalizedLookbackYears - 1);
    }

    private int ResolveKnowledgeYear(int knowledgeYear)
    {
        return knowledgeYear > 0
            ? knowledgeYear
            : DateTime.UtcNow.Year;
    }
}