using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoolSense.Api.Data;

public sealed class SqlServerVectorStore : IVectorStore, ITicketKnowledgeEmbeddingStore
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;
    private readonly IProjectRepository _projectRepository;
    private readonly IFeedbackRepository _feedbackRepository;
    private readonly IVectorSimilaritySearch _similaritySearch;
    private readonly InMemoryVectorStoreCache _cache;

    public SqlServerVectorStore(
        IPoolSenseSqlConnectionFactory connectionFactory,
        IProjectRepository projectRepository,
        IFeedbackRepository feedbackRepository,
        IVectorSimilaritySearch similaritySearch,
        InMemoryVectorStoreCache cache)
    {
        _connectionFactory = connectionFactory;
        _projectRepository = projectRepository;
        _feedbackRepository = feedbackRepository;
        _similaritySearch = similaritySearch;
        _cache = cache;
    }

    public async Task InsertTicketKnowledge(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default)
    {
        await AddTicketKnowledgeAsync(ticketKnowledge, cancellationToken);
    }

    public async Task<TicketKnowledge> AddTicketKnowledgeAsync(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticketKnowledge);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.ticket_knowledge (
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
            OUTPUT INSERTED.id, INSERTED.created_at
            VALUES (
                @ticketId,
                @sourceEventId,
                @problem,
                @rootCause,
                @resolution,
                @keywords,
                @embedding,
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

        await using var command = new SqlCommand(sql, connection);
        AddTicketKnowledgeParameters(command, ticketKnowledge);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            ticketKnowledge.Id = reader.GetInt32(0);
            ticketKnowledge.CreatedAt = reader.GetDateTime(1);
        }

        _cache.AddOrReplace(CloneForCache(ticketKnowledge));
        return ticketKnowledge;
    }

    public Task<IReadOnlyList<TicketKnowledge>> GetTicketKnowledgeAsync(CancellationToken cancellationToken = default)
    {
        return _cache.GetOrLoadAsync(LoadTicketKnowledgeAsync, cancellationToken);
    }

    public async Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(
        float[] embedding,
        int limit = 5,
        IReadOnlyList<string>? selectedGroupIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        if (limit <= 0 || embedding.Length == 0)
        {
            return [];
        }

        var allKnowledge = await GetTicketKnowledgeAsync(cancellationToken);
        var scopedProjects = await GetScopedProjectsAsync(selectedGroupIds, cancellationToken);
        var requireProjectMatch = selectedGroupIds is { Count: > 0 };
        var candidates = ApplyProjectScope(allKnowledge, scopedProjects, requireProjectMatch);

        if (candidates.Count == 0)
        {
            return [];
        }

        var feedbackScores = await _feedbackRepository.GetFeedbackScores(
            candidates.Select(candidate => candidate.TicketId).ToArray(),
            cancellationToken);

        return _similaritySearch.Search(embedding, candidates, feedbackScores, limit);
    }

    public Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
    {
        return _feedbackRepository.GetFeedbackScore(ticketId, cancellationToken);
    }

    public async Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(int monthCount = 6, CancellationToken cancellationToken = default)
    {
        return await GetIncidentTimelineAsync(monthCount, cancellationToken);
    }

    public async Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimelineAsync(int monthCount = 6, CancellationToken cancellationToken = default)
    {
        if (monthCount <= 0)
        {
            return [];
        }

        var allKnowledge = await GetTicketKnowledgeAsync(cancellationToken);
        var scopedProjects = await GetScopedProjectsAsync([], cancellationToken);
        var scopedKnowledge = ApplyProjectScope(allKnowledge, scopedProjects, requireProjectMatch: false);
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var minimumMonth = currentMonth.AddMonths(-(monthCount - 1));

        return scopedKnowledge
            .Where(ticket => ticket.CreatedAt >= minimumMonth)
            .GroupBy(ticket => new DateTime(ticket.CreatedAt.Year, ticket.CreatedAt.Month, 1))
            .OrderBy(group => group.Key)
            .Select(group => new IncidentTimelinePoint(group.Key.ToString("MMM", CultureInfo.InvariantCulture), group.Count()))
            .ToList();
    }

    private async Task<IReadOnlyList<TicketKnowledge>> LoadTicketKnowledgeAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT id,
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
                   created_at
            FROM dbo.ticket_knowledge
            WHERE NULLIF(embedding, '') IS NOT NULL
            ORDER BY created_at DESC, id DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<TicketKnowledge>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapTicketKnowledge(reader));
        }

        return results;
    }

    private static void AddTicketKnowledgeParameters(SqlCommand command, TicketKnowledge ticketKnowledge)
    {
        command.Parameters.AddWithValue("@ticketId", ticketKnowledge.TicketId ?? string.Empty);
        command.Parameters.AddWithValue("@sourceEventId", ticketKnowledge.SourceEventId ?? string.Empty);
        command.Parameters.AddWithValue("@problem", ticketKnowledge.Problem ?? string.Empty);
        command.Parameters.AddWithValue("@rootCause", ticketKnowledge.RootCause ?? string.Empty);
        command.Parameters.AddWithValue("@resolution", ticketKnowledge.Resolution ?? string.Empty);
        command.Parameters.AddWithValue("@keywords", JsonSerializer.Serialize(ticketKnowledge.Keywords ?? []));
        command.Parameters.AddWithValue("@embedding", JsonSerializer.Serialize(ticketKnowledge.Embedding ?? []));
        command.Parameters.AddWithValue("@application", ResolveApplication(ticketKnowledge.Application));
        command.Parameters.AddWithValue("@knowledgeYear", ResolveKnowledgeYear(ticketKnowledge.KnowledgeYear));
        command.Parameters.AddWithValue("@sourceStatus", ticketKnowledge.SourceStatus ?? string.Empty);
        command.Parameters.AddWithValue("@sourceSubmittedAt", ticketKnowledge.SourceSubmittedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@sourceClosedAt", ticketKnowledge.SourceClosedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@submitterId", ticketKnowledge.SubmitterId ?? string.Empty);
        command.Parameters.AddWithValue("@lifeguardId", ticketKnowledge.LifeguardId ?? string.Empty);
        command.Parameters.AddWithValue("@sourceProject", ticketKnowledge.SourceProject ?? string.Empty);
        command.Parameters.AddWithValue("@createdAt", ticketKnowledge.CreatedAt == default ? DateTime.UtcNow : ticketKnowledge.CreatedAt);
    }

    private static TicketKnowledge MapTicketKnowledge(SqlDataReader reader)
    {
        return new TicketKnowledge
        {
            Id = reader.GetInt32(0),
            TicketId = reader.GetString(1),
            SourceEventId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Problem = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            RootCause = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Resolution = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            Keywords = reader.IsDBNull(6) ? [] : DeserializeStringArray(reader.GetString(6)),
            Embedding = reader.IsDBNull(7) ? [] : DeserializeFloatArray(reader.GetString(7)),
            Application = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            KnowledgeYear = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            SourceStatus = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            SourceSubmittedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            SourceClosedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            SubmitterId = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            LifeguardId = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            SourceProject = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            CreatedAt = reader.IsDBNull(16) ? DateTime.UtcNow : reader.GetDateTime(16)
        };
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

    private static IReadOnlyList<TicketKnowledge> ApplyProjectScope(
        IReadOnlyList<TicketKnowledge> knowledge,
        IReadOnlyList<ProjectConfig> scopedProjects,
        bool requireProjectMatch)
    {
        if (scopedProjects.Count == 0)
        {
            return requireProjectMatch ? [] : knowledge;
        }

        return knowledge
            .Where(ticket => scopedProjects.Any(project => IsInProjectScope(ticket, project)))
            .ToList();
    }

    private static bool IsInProjectScope(TicketKnowledge ticketKnowledge, ProjectConfig project)
    {
        if (string.IsNullOrWhiteSpace(project.ApplicationFilter))
        {
            return false;
        }

        if (project.KnowledgeLookbackYears > 0
            && ticketKnowledge.KnowledgeYear < GetMinimumKnowledgeYear(project.KnowledgeLookbackYears))
        {
            return false;
        }

        return MatchesApplicationFilter(ticketKnowledge.Application, project.ApplicationFilter);
    }

    private static bool MatchesApplicationFilter(string application, string applicationFilter)
    {
        if (applicationFilter.Contains('%') || applicationFilter.Contains('_'))
        {
            var pattern = "^" + Regex.Escape(applicationFilter)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            return Regex.IsMatch(application ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return string.Equals(application, applicationFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] DeserializeStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    private static float[] DeserializeFloatArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<float[]>(json) ?? [];
    }

    private static TicketKnowledge CloneForCache(TicketKnowledge ticketKnowledge)
    {
        return new TicketKnowledge
        {
            Id = ticketKnowledge.Id,
            TicketId = ticketKnowledge.TicketId,
            SourceEventId = ticketKnowledge.SourceEventId,
            Problem = ticketKnowledge.Problem,
            RootCause = ticketKnowledge.RootCause,
            Resolution = ticketKnowledge.Resolution,
            Keywords = ticketKnowledge.Keywords.ToArray(),
            SearchVariants = ticketKnowledge.SearchVariants.ToList(),
            Embedding = ticketKnowledge.Embedding.ToArray(),
            Application = ticketKnowledge.Application,
            KnowledgeYear = ticketKnowledge.KnowledgeYear,
            SourceStatus = ticketKnowledge.SourceStatus,
            SourceSubmittedAt = ticketKnowledge.SourceSubmittedAt,
            SourceClosedAt = ticketKnowledge.SourceClosedAt,
            SubmitterId = ticketKnowledge.SubmitterId,
            LifeguardId = ticketKnowledge.LifeguardId,
            SourceProject = ticketKnowledge.SourceProject,
            CreatedAt = ticketKnowledge.CreatedAt
        };
    }

    private static string ResolveApplication(string application)
    {
        return string.IsNullOrWhiteSpace(application)
            ? string.Empty
            : application;
    }

    private static int ResolveKnowledgeYear(int knowledgeYear)
    {
        return knowledgeYear > 0
            ? knowledgeYear
            : DateTime.UtcNow.Year;
    }

    private static int GetMinimumKnowledgeYear(int lookbackYears)
    {
        var normalizedLookbackYears = Math.Max(1, lookbackYears);
        return DateTime.UtcNow.Year - (normalizedLookbackYears - 1);
    }
}