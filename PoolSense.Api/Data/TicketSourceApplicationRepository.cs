using Microsoft.Data.SqlClient;

namespace PoolSense.Api.Data;

public interface ITicketSourceApplicationRepository
{
    Task UpdatePoolSenseFlagAsync(string application, bool enabled, CancellationToken cancellationToken = default);
}

public class TicketSourceApplicationRepository : ITicketSourceApplicationRepository
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TicketSourceApplicationRepository> _logger;

    public TicketSourceApplicationRepository(
        IConfiguration configuration,
        ILogger<TicketSourceApplicationRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task UpdatePoolSenseFlagAsync(string application, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(application))
            return;

        var connectionString = _configuration.GetConnectionString("TicketSourceSqlServer")
            ?? _configuration.GetConnectionString("TicketSource")
            ?? throw new InvalidOperationException("ConnectionStrings:TicketSourceSqlServer is not configured.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE [dbo].[tbl_Application]
            SET PoolSense = @poolSense
            WHERE Application = @application;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@poolSense", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@application", application);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation(
            "TicketSource: PoolSense={Enabled} written to tbl_Application for '{Application}' ({Rows} row(s)).",
            enabled, application, rows);
    }
}
