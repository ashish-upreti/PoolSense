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
                    var newProject = new ProjectConfig
                    {
                        ProjectId = application,
                        ProjectName = application,
                        KnowledgeLookbackYears = 2,
                        SimilaritySearchLimit = 5,
                        SendEmail = true,
                        PoolingEnabled = true,
                        EmailRecipients = "ashish.upreti@intel.com",
                        TicketSourceType = "sql",
                        ConnectionString = string.Empty,
                        KnowledgeSources = [],
                        ApplicationFilter = application
                    };

                    await _projectRepository.CreateProjectAsync(newProject, cancellationToken);
                    _logger.LogInformation("ApplicationSync: created project_configs entry for application '{Application}'.", application);
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
