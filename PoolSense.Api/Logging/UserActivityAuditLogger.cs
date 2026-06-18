using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;

namespace PoolSense.Api.Logging;

public interface IUserActivityAuditLogger
{
    Task LogAsync(
        string action,
        string entityType,
        string entityId,
        string details,
        bool success = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists user activity audit records to dbo.user_activity_logs for compliance and traceability.
/// Captures the authenticated HTTP user, action, entity, and request context.
/// </summary>
public sealed class UserActivityAuditLogger : IUserActivityAuditLogger
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<UserActivityAuditLogger> _logger;

    public UserActivityAuditLogger(
        IPoolSenseSqlConnectionFactory connectionFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<UserActivityAuditLogger> logger)
    {
        _connectionFactory = connectionFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
        string action,
        string entityType,
        string entityId,
        string details,
        bool success = true,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        var userName = httpContext?.User?.Identity?.Name
            ?? httpContext?.User?.FindFirst("name")?.Value
            ?? httpContext?.User?.FindFirst("preferred_username")?.Value
            ?? "anonymous";

        var ipAddress = httpContext?.Connection?.RemoteIpAddress?.ToString() ?? string.Empty;
        var httpMethod = httpContext?.Request?.Method ?? string.Empty;
        var requestPath = httpContext?.Request?.Path.ToString() ?? string.Empty;

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                INSERT INTO dbo.user_activity_logs
                    (user_name, action, entity_type, entity_id, details, ip_address, http_method, request_path, success)
                VALUES
                    (@userName, @action, @entityType, @entityId, @details, @ipAddress, @httpMethod, @requestPath, @success);
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@userName", userName);
            command.Parameters.AddWithValue("@action", action);
            command.Parameters.AddWithValue("@entityType", entityType);
            command.Parameters.AddWithValue("@entityId", entityId);
            command.Parameters.AddWithValue("@details", details);
            command.Parameters.AddWithValue("@ipAddress", ipAddress);
            command.Parameters.AddWithValue("@httpMethod", httpMethod);
            command.Parameters.AddWithValue("@requestPath", requestPath);
            command.Parameters.AddWithValue("@success", success);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit log failure must never surface to the caller.
            _logger.LogWarning(ex, "Failed to write user activity audit log for action '{Action}' by '{UserName}'.", action, userName);
        }
    }
}
