using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public interface IApplicationSyncService
{
    Task SyncApplicationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Before each polling iteration, reads [tbl_Application] from the ticket-source database and
/// ensures a matching project_configs row exists in the PoolSense database for every application
/// where PoolSense = 1.  If the flag is 0 the corresponding project is disabled.
/// </summary>
public class ApplicationSyncService : IApplicationSyncService
{
    private readonly IConfiguration _configuration;
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger<ApplicationSyncService> _logger;

    public ApplicationSyncService(
        IConfiguration configuration,
        IProjectRepository projectRepository,
        ILogger<ApplicationSyncService> logger)
    {
        _configuration = configuration;
        _projectRepository = projectRepository;
        _logger = logger;
    }

    private IEnumerable<string> GetBaseRecipients()
        => SplitEmails(_configuration["ApplicationSync:BaseRecipients"] ?? string.Empty);

    public async Task SyncApplicationsAsync(CancellationToken cancellationToken = default)
    {
        var ticketSourceConnectionString = GetTicketSourceConnectionString();
        var applications = await GetApplicationsAsync(ticketSourceConnectionString, cancellationToken);

        _logger.LogInformation("ApplicationSync: {Count} application(s) found in tbl_Application.", applications.Count);

        foreach (var (application, poolSenseEnabled) in applications)
        {
            var existing = await _projectRepository.GetProjectByApplicationFilterAsync(application, cancellationToken);

            if (poolSenseEnabled)
            {
                if (existing is null)
                {
                    var emailRecipients = await BuildEmailRecipientsAsync(
                        ticketSourceConnectionString, application, cancellationToken);

                    var newProject = new ProjectConfig
                    {
                        ProjectId = application,
                        ProjectName = application,
                        KnowledgeLookbackYears = 2,
                        SimilaritySearchLimit = 5,
                        SendEmail = true,
                        PoolingEnabled = true,
                        EmailRecipients = emailRecipients,
                        TicketSourceType = "sql",
                        ConnectionString = string.Empty,
                        KnowledgeSources = [],
                        ApplicationFilter = application
                    };

                    await _projectRepository.CreateProjectAsync(newProject, cancellationToken);
                    _logger.LogInformation(
                        "ApplicationSync: created project_configs for '{Application}' with recipients: {Recipients}.",
                        application, emailRecipients);
                }
                else if (!existing.SendEmail || !existing.PoolingEnabled)
                {
                    existing.SendEmail = true;
                    existing.PoolingEnabled = true;
                    await _projectRepository.UpdateProjectAsync(existing, cancellationToken);
                    _logger.LogInformation("ApplicationSync: enabled project_configs for application '{Application}'.", application);
                }
            }
            else
            {
                if (existing is not null && (existing.SendEmail || existing.PoolingEnabled))
                {
                    existing.SendEmail = false;
                    existing.PoolingEnabled = false;
                    await _projectRepository.UpdateProjectAsync(existing, cancellationToken);
                    _logger.LogInformation("ApplicationSync: disabled project_configs for application '{Application}'.", application);
                }
            }
        }
    }

    /// <summary>
    /// Builds the semicolon-separated recipient list for a newly registered application:
    ///   - Base recipients (ashish.upreti, syed.nasir.mohamed)
    ///   - All EmailAddress values from v_ApplicationLifeguards for the application
    ///   - DefaultEmailCC from v_Application for the application (may itself be semicolon-separated)
    /// Duplicates are removed (case-insensitive).
    /// </summary>
    private async Task<string> BuildEmailRecipientsAsync(
        string connectionString, string application, CancellationToken cancellationToken)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var email in GetBaseRecipients())
            emails.Add(email);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // 1. Lifeguard emails from v_ApplicationLifeguards
        const string lifeguardSql = """
            SELECT EmailAddress
            FROM [dbo].[v_ApplicationLifeguards]
            WHERE ApplicationName = @application
              AND EmailAddress IS NOT NULL
              AND EmailAddress <> '';
            """;

        await using (var cmd = new SqlCommand(lifeguardSql, connection))
        {
            cmd.Parameters.AddWithValue("@application", application);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var raw = reader.GetString(0);
                foreach (var part in SplitEmails(raw))
                    emails.Add(part);
            }
        }

        // 2. DefaultEmailCC from v_Application
        const string ccSql = """
            SELECT DefaultEmailCC
            FROM [dbo].[v_Application]
            WHERE Application = @application
              AND DefaultEmailCC IS NOT NULL
              AND DefaultEmailCC <> '';
            """;

        await using (var cmd = new SqlCommand(ccSql, connection))
        {
            cmd.Parameters.AddWithValue("@application", application);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var raw = reader.GetString(0);
                foreach (var part in SplitEmails(raw))
                    emails.Add(part);
            }
        }

        return string.Join(";", emails);
    }

    private static IEnumerable<string> SplitEmails(string raw)
        => raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
              .Select(e => e.Trim())
              .Where(e => !string.IsNullOrWhiteSpace(e));

    private async Task<IReadOnlyList<(string Application, bool PoolSenseEnabled)>> GetApplicationsAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT Application, ISNULL(PoolSense, 0) AS PoolSense
            FROM [dbo].[tbl_Application]
            WHERE Application IS NOT NULL AND Application <> '';
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<(string, bool)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return results;
    }

    private string GetTicketSourceConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("TicketSourceSqlServer")
            ?? _configuration.GetConnectionString("TicketSource");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Ticket source connection string is not configured. Set ConnectionStrings:TicketSourceSqlServer.");
        }

        return connectionString;
    }
}

