using Microsoft.Data.SqlClient;
using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface ITicketAutomationSettingsRepository
{
    Task<RuntimeTicketAutomationSettings?> GetAsync(CancellationToken cancellationToken = default);
    Task<RuntimeTicketAutomationSettings> UpsertAsync(RuntimeTicketAutomationSettings settings, CancellationToken cancellationToken = default);
}

public sealed class TicketAutomationSettingsRepository : ITicketAutomationSettingsRepository
{
    private const string SettingsKey = "master_polling";
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public TicketAutomationSettingsRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<RuntimeTicketAutomationSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1) polling_enabled,
                           poll_interval_seconds
            FROM dbo.ticket_automation_settings
            WHERE settings_key = @settingsKey;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@settingsKey", SettingsKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RuntimeTicketAutomationSettings
        {
            PollingEnabled = reader.IsDBNull(0) || reader.GetBoolean(0),
            PollIntervalSeconds = reader.IsDBNull(1) ? 30 : reader.GetInt32(1)
        };
    }

    public async Task<RuntimeTicketAutomationSettings> UpsertAsync(RuntimeTicketAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            MERGE dbo.ticket_automation_settings AS target
            USING (SELECT @settingsKey AS settings_key) AS source
            ON target.settings_key = source.settings_key
            WHEN MATCHED THEN
                UPDATE SET polling_enabled = @pollingEnabled,
                           poll_interval_seconds = @pollIntervalSeconds,
                           updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (settings_key, polling_enabled, poll_interval_seconds)
                VALUES (@settingsKey, @pollingEnabled, @pollIntervalSeconds)
            OUTPUT INSERTED.polling_enabled, INSERTED.poll_interval_seconds;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@settingsKey", SettingsKey);
        command.Parameters.AddWithValue("@pollingEnabled", settings.PollingEnabled);
        command.Parameters.AddWithValue("@pollIntervalSeconds", settings.PollIntervalSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            settings.PollingEnabled = reader.IsDBNull(0) || reader.GetBoolean(0);
            settings.PollIntervalSeconds = reader.IsDBNull(1) ? settings.PollIntervalSeconds : reader.GetInt32(1);
        }

        return settings;
    }
}