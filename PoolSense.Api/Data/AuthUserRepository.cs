using System.Text.Json;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Contracts;

namespace PoolSense.Api.Data;

public interface IAuthUserRepository
{
    Task RecordSuccessfulLoginAsync(AuthenticatedUser user, string clientAddress, CancellationToken cancellationToken = default);

    Task RecordFailedLoginAsync(string? username, int statusCode, string message, string clientAddress, CancellationToken cancellationToken = default);
}

public sealed class AuthUserRepository : IAuthUserRepository
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public AuthUserRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task RecordSuccessfulLoginAsync(AuthenticatedUser user, string clientAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            DECLARE @now datetime2(7) = SYSUTCDATETIME();

            MERGE dbo.auth_users WITH (HOLDLOCK) AS target
            USING (SELECT @username AS username) AS source
                ON target.username = source.username
            WHEN MATCHED THEN
                UPDATE SET auth_principal = @authPrincipal,
                           display_name = @displayName,
                           email = @email,
                           is_admin = @isAdmin,
                           groups_json = @groupsJson,
                           last_login_at = @now,
                           updated_at = @now
            WHEN NOT MATCHED THEN
                INSERT (username, auth_principal, display_name, email, is_admin, groups_json, last_login_at, created_at, updated_at)
                VALUES (@username, @authPrincipal, @displayName, @email, @isAdmin, @groupsJson, @now, @now, @now);

            INSERT INTO dbo.auth_login_audit (username, auth_principal, success, status_code, message, client_address, created_at)
            VALUES (@username, @authPrincipal, 1, 200, @message, @clientAddress, @now);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", user.Username);
        command.Parameters.AddWithValue("@authPrincipal", user.AuthPrincipal);
        command.Parameters.AddWithValue("@displayName", user.DisplayName);
        command.Parameters.AddWithValue("@email", user.Email);
        command.Parameters.AddWithValue("@isAdmin", user.IsAdmin ?? false);
        command.Parameters.AddWithValue("@groupsJson", JsonSerializer.Serialize(user.Groups ?? []));
        command.Parameters.AddWithValue("@message", "Authentication successful");
        command.Parameters.AddWithValue("@clientAddress", clientAddress);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordFailedLoginAsync(string? username, int statusCode, string message, string clientAddress, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.auth_login_audit (username, auth_principal, success, status_code, message, client_address, created_at)
            VALUES (@username, '', 0, @statusCode, @message, @clientAddress, SYSUTCDATETIME());
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", string.IsNullOrWhiteSpace(username) ? string.Empty : username.Trim());
        command.Parameters.AddWithValue("@statusCode", statusCode);
        command.Parameters.AddWithValue("@message", string.IsNullOrWhiteSpace(message) ? "Authentication failed" : message);
        command.Parameters.AddWithValue("@clientAddress", clientAddress);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}