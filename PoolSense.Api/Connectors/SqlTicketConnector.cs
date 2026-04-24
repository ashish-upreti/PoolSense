using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Models;
using PoolSense.Application.Models;

namespace PoolSense.Api.Connectors;

public class SqlTicketConnector : ITicketSourceConnector
{
    private readonly IConfiguration _configuration;
    private readonly TicketAutomationSettings _settings;

    public SqlTicketConnector(IConfiguration configuration, IOptions<TicketAutomationSettings> settings)
    {
        _configuration = configuration;
        _settings = settings.Value;
    }

    public Task<IReadOnlyList<TicketRequest>> GetNewTickets(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Unscoped ticket queries are no longer supported. Use the project-backed overload.");
    }

    public Task<TicketRequest?> GetTicketDetails(string ticketId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Unscoped ticket queries are no longer supported. Use the project-backed overload.");
    }

    public Task<IReadOnlyList<TicketRequest>> GetNewTickets(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);
        return GetTicketsByProjectAsync(projectConfig, _settings.NewStatusName, cancellationToken);
    }

    public Task<TicketRequest?> GetTicketDetails(ProjectConfig projectConfig, string ticketId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);
        return GetProjectTicketDetailsAsync(projectConfig, ticketId, cancellationToken);
    }

    public Task<IReadOnlyList<TicketRequest>> GetTicketsByStatusAsync(ProjectConfig projectConfig, string status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);
        return GetTicketsByProjectAsync(projectConfig, status, cancellationToken);
    }

    public Task<IReadOnlyList<TicketRequest>> GetTicketsByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Unscoped ticket queries are no longer supported. Use the project-backed overload.");
    }

    private async Task<IReadOnlyList<TicketRequest>> GetTicketsByProjectAsync(ProjectConfig projectConfig, string status, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(GetConnectionString(projectConfig));
        await connection.OpenAsync(cancellationToken);

        var applicationFilter = GetApplicationFilter(projectConfig);
        var isPattern = applicationFilter.Contains('%');
        var appOperator = isPattern ? "LIKE" : "=";

        var sql = BuildBaseSql() + Environment.NewLine + $"""
            WHERE a.Application {appOperator} @application
              AND s.EventStatusName = @status
            """;

        var minimumKnowledgeYear = GetMinimumKnowledgeYear(projectConfig.KnowledgeLookbackYears);
        if (minimumKnowledgeYear is not null)
        {
            sql += Environment.NewLine + """
                            AND COALESCE(e.DateTime_Closed, e.DateTime_Submitted) >= @minimumDate
            """;
        }

        sql += Environment.NewLine + """
            ORDER BY COALESCE(e.DateTime_Closed, e.DateTime_Submitted) DESC, e.LogID DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@application", applicationFilter);
        command.Parameters.AddWithValue("@status", status);

        if (minimumKnowledgeYear is not null)
        {
            command.Parameters.AddWithValue("@minimumDate", new DateTime(minimumKnowledgeYear.Value, 1, 1));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<TicketRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(MapTicket(reader));
        }

        return tickets;
    }

    private async Task<TicketRequest?> GetProjectTicketDetailsAsync(ProjectConfig projectConfig, string ticketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return null;
        }

        await using var connection = new SqlConnection(GetConnectionString(projectConfig));
        await connection.OpenAsync(cancellationToken);

        var applicationFilter = GetApplicationFilter(projectConfig);
        var isPattern = applicationFilter.Contains('%');
        var appOperator = isPattern ? "LIKE" : "=";

        var sql = BuildBaseSql() + Environment.NewLine + $"""
            WHERE e.LogID = @ticketId
              AND a.Application {appOperator} @application
            """;

        var minimumKnowledgeYear = GetMinimumKnowledgeYear(projectConfig.KnowledgeLookbackYears);
        if (minimumKnowledgeYear is not null)
        {
            sql += Environment.NewLine + """
                            AND COALESCE(e.DateTime_Closed, e.DateTime_Submitted) >= @minimumDate
            """;
        }

        sql += Environment.NewLine + """
            ORDER BY COALESCE(e.DateTime_Closed, e.DateTime_Submitted) DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ticketId", ticketId);
        command.Parameters.AddWithValue("@application", applicationFilter);

        if (minimumKnowledgeYear is not null)
        {
            command.Parameters.AddWithValue("@minimumDate", new DateTime(minimumKnowledgeYear.Value, 1, 1));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapTicket(reader);
    }

    private string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("TicketSourceSqlServer")
            ?? _configuration.GetConnectionString("TicketSource")
            ?? _configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("A SQL Server ticket source connection string was not found. Configure ConnectionStrings:TicketSourceSqlServer, ConnectionStrings:TicketSource, or ConnectionStrings:DefaultConnection.");
        }

        return connectionString;
    }

    private string GetConnectionString(ProjectConfig projectConfig)
    {
        return string.IsNullOrWhiteSpace(projectConfig.ConnectionString)
            ? GetConnectionString()
            : projectConfig.ConnectionString;
    }

    private string BuildBaseSql()
    {
        var databasePrefix = string.IsNullOrWhiteSpace(_settings.SourceDatabaseName)
            ? string.Empty
            : $"[{_settings.SourceDatabaseName}].";

        return $$"""
            SELECT CAST(e.LogID AS nvarchar(50)) AS TicketId,
                   CAST(e.LogID AS nvarchar(50)) AS SourceEventId,
                   e.DateTime_Submitted,
                   e.DateTime_Closed,
                   e.Symptom AS Issue,
                   e.Problem AS SourceResolution,
                   e.Solution AS SourceSolution,
                   CAST(e.ApplicationID AS nvarchar(50)) AS ApplicationID,
                   a.Application,
                   CAST(e.EventStatusID AS nvarchar(50)) AS EventStatusID,
                   s.EventStatusName,
                   CAST(e.UpdaterID AS nvarchar(50)) AS SubmitterID,
                   CAST(l.LifeguardID AS nvarchar(50)) AS LifeguardID
            FROM {{databasePrefix}}[dbo].[tbl_EventLog] e
            INNER JOIN {{databasePrefix}}[dbo].[tbl_Application] a
                ON a.ApplicationID = e.ApplicationID
            INNER JOIN {{databasePrefix}}[dbo].[tbl_EventStatus] s
                ON s.EventStatusID = e.EventStatusID
            LEFT JOIN {{databasePrefix}}[dbo].[tbl_EventLifeguard] l
                ON l.LogID = e.LogID
            """;
    }

    private int? GetMinimumKnowledgeYear(int? lookbackYearsOverride = null)
    {
        var configuredLookbackYears = lookbackYearsOverride is > 0
            ? lookbackYearsOverride.Value
            : 0;

        if (configuredLookbackYears <= 0)
        {
            return null;
        }

        var lookbackYears = Math.Max(1, configuredLookbackYears);
        return DateTime.UtcNow.Year - (lookbackYears - 1);
    }

    private string GetApplicationFilter(ProjectConfig projectConfig)
    {
        if (!string.IsNullOrWhiteSpace(projectConfig.ApplicationFilter))
        {
            return projectConfig.ApplicationFilter;
        }

        if (!string.IsNullOrWhiteSpace(projectConfig.ProjectName))
        {
            return projectConfig.ProjectName;
        }

        throw new InvalidOperationException($"Project '{projectConfig.ProjectId}' is missing an application filter.");
    }

    private static TicketRequest MapTicket(SqlDataReader reader)
    {
        var issue = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var sourceSolution = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);

        return new TicketRequest
        {
            TicketId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            SourceEventId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            SubmittedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            ClosedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            Issue = issue,
            Title = issue,
            Description = sourceSolution,
            Resolution = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            SourceResolution = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            SourceSolution = sourceSolution,
            ApplicationId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Application = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            EventStatusId = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            EventStatusName = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            SubmitterId = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            LifeguardId = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
        };
    }
}
