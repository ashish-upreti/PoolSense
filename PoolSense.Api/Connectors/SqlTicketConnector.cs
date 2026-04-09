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
        return GetTicketsByStatusAsync(_settings.NewStatusName, cancellationToken);
    }

    public Task<TicketRequest?> GetTicketDetails(string ticketId, CancellationToken cancellationToken = default)
    {
        return GetTicketDetails(GetConnectionString(), ticketId, cancellationToken);
    }

    public Task<IReadOnlyList<TicketRequest>> GetNewTickets(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);
        return GetTicketsByStatusAsync(GetConnectionString(projectConfig), _settings.NewStatusName, cancellationToken);
    }

    public Task<TicketRequest?> GetTicketDetails(ProjectConfig projectConfig, string ticketId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);
        return GetTicketDetails(GetConnectionString(projectConfig), ticketId, cancellationToken);
    }

    public Task<IReadOnlyList<TicketRequest>> GetTicketsByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        return GetTicketsByStatusAsync(GetConnectionString(), status, cancellationToken);
    }

    /// <summary>
    /// Fetches tickets for a specific project group, applying the group's ApplicationFilter
    /// as LIKE (when it contains %) or exact match.
    /// </summary>
    public Task<IReadOnlyList<TicketRequest>> GetTicketsByGroupAsync(ProjectGroupSettings group, string status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        return GetTicketsByGroupAsync(GetConnectionString(), group, status, cancellationToken);
    }

    private async Task<IReadOnlyList<TicketRequest>> GetTicketsByGroupAsync(string connectionString, ProjectGroupSettings group, string status, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var isPattern = group.ApplicationFilter.Contains('%');
        var appOperator = isPattern ? "LIKE" : "=";

        var sql = BuildBaseSql() + Environment.NewLine + $"""
            WHERE a.Application {appOperator} @application
              AND s.EventStatusName = @status
            """;

        if (_settings.KnowledgeLookbackYears > 0)
        {
            sql += Environment.NewLine + """
                            AND COALESCE(e.DateTime_Closed, e.DateTime_Submitted) >= @minimumDate
            """;
        }

        sql += Environment.NewLine + """
            ORDER BY COALESCE(e.DateTime_Closed, e.DateTime_Submitted) DESC, e.LogID DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@application", group.ApplicationFilter);
        command.Parameters.AddWithValue("@status", status);

        if (_settings.KnowledgeLookbackYears > 0)
        {
            command.Parameters.AddWithValue("@minimumDate", new DateTime(GetMinimumKnowledgeYear(), 1, 1));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<TicketRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(MapTicket(reader));
        }
        return tickets;
    }

    private async Task<IReadOnlyList<TicketRequest>> GetTicketsByStatusAsync(string connectionString, string status, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = BuildBaseSql() + Environment.NewLine + """
            WHERE a.Application = @application
              AND s.EventStatusName = @status
            """;

        if (_settings.KnowledgeLookbackYears > 0)
        {
            sql += Environment.NewLine + """
                            AND COALESCE(e.DateTime_Closed, e.DateTime_Submitted) >= @minimumDate
            """;
        }

        sql += Environment.NewLine + """
            ORDER BY COALESCE(e.DateTime_Closed, e.DateTime_Submitted) DESC, e.LogID DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@application", _settings.ApplicationName);
        command.Parameters.AddWithValue("@status", status);

        if (_settings.KnowledgeLookbackYears > 0)
        {
            command.Parameters.AddWithValue("@minimumDate", new DateTime(GetMinimumKnowledgeYear(), 1, 1));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tickets = new List<TicketRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(MapTicket(reader));
        }

        return tickets;
    }

    private async Task<TicketRequest?> GetTicketDetails(string connectionString, string ticketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return null;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = BuildBaseSql() + Environment.NewLine + """
            WHERE e.LogID = @ticketId
              AND a.Application = @application
            """;

        if (_settings.KnowledgeLookbackYears > 0)
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
        command.Parameters.AddWithValue("@application", _settings.ApplicationName);

        if (_settings.KnowledgeLookbackYears > 0)
        {
            command.Parameters.AddWithValue("@minimumDate", new DateTime(GetMinimumKnowledgeYear(), 1, 1));
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

    private static string GetConnectionString(ProjectConfig projectConfig)
    {
        if (string.IsNullOrWhiteSpace(projectConfig.ConnectionString))
        {
            throw new InvalidOperationException($"Project '{projectConfig.ProjectId}' is missing a ticket source connection string.");
        }

        return projectConfig.ConnectionString;
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

    private int GetMinimumKnowledgeYear()
    {
        var lookbackYears = Math.Max(1, _settings.KnowledgeLookbackYears);
        return DateTime.UtcNow.Year - (lookbackYears - 1);
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
