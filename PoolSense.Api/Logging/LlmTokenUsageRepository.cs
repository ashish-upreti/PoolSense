using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;

namespace PoolSense.Api.Logging;

public interface ILlmTokenUsageRepository
{
    Task LogAsync(LlmTokenUsageRecord record, CancellationToken cancellationToken = default);
}

public sealed class LlmTokenUsageRepository : ILlmTokenUsageRepository
{
    private readonly IPoolSenseSqlConnectionFactory _connectionFactory;

    public LlmTokenUsageRepository(IPoolSenseSqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task LogAsync(LlmTokenUsageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo.llm_token_usage (
                created_at,
                service_type,
                operation_name,
                provider,
                model,
                deployment_name,
                prompt_tokens,
                completion_tokens,
                total_tokens,
                is_estimated,
                input_characters,
                output_characters,
                vector_dimensions,
                latency_ms,
                success,
                error_message,
                correlation_id,
                machine_name,
                user_name,
                process_id)
            VALUES (
                @createdAt,
                @serviceType,
                @operationName,
                @provider,
                @model,
                @deploymentName,
                @promptTokens,
                @completionTokens,
                @totalTokens,
                @isEstimated,
                @inputCharacters,
                @outputCharacters,
                @vectorDimensions,
                @latencyMs,
                @success,
                @errorMessage,
                @correlationId,
                @machineName,
                @userName,
                @processId);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt == default ? DateTime.UtcNow : record.CreatedAt);
        command.Parameters.AddWithValue("@serviceType", record.ServiceType ?? string.Empty);
        command.Parameters.AddWithValue("@operationName", record.OperationName ?? string.Empty);
        command.Parameters.AddWithValue("@provider", record.Provider ?? string.Empty);
        command.Parameters.AddWithValue("@model", record.Model ?? string.Empty);
        command.Parameters.AddWithValue("@deploymentName", record.DeploymentName ?? string.Empty);
        command.Parameters.AddWithValue("@promptTokens", Math.Max(0, record.PromptTokens));
        command.Parameters.AddWithValue("@completionTokens", Math.Max(0, record.CompletionTokens));
        command.Parameters.AddWithValue("@totalTokens", Math.Max(0, record.TotalTokens));
        command.Parameters.AddWithValue("@isEstimated", record.IsEstimated);
        command.Parameters.AddWithValue("@inputCharacters", Math.Max(0, record.InputCharacters));
        command.Parameters.AddWithValue("@outputCharacters", Math.Max(0, record.OutputCharacters));
        command.Parameters.AddWithValue("@vectorDimensions", record.VectorDimensions ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@latencyMs", Math.Max(0, record.LatencyMs));
        command.Parameters.AddWithValue("@success", record.Success);
        command.Parameters.AddWithValue("@errorMessage", record.ErrorMessage ?? string.Empty);
        command.Parameters.AddWithValue("@correlationId", record.CorrelationId ?? string.Empty);
        command.Parameters.AddWithValue("@machineName", Environment.MachineName);
        command.Parameters.AddWithValue("@userName", Environment.UserName);
        command.Parameters.AddWithValue("@processId", Environment.ProcessId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.llm_token_usage', N'U') IS NULL
                THROW 50005, 'Missing dbo.llm_token_usage. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;

            IF COL_LENGTH('dbo.llm_token_usage', 'operation_name') IS NULL
                THROW 50006, 'Missing dbo.llm_token_usage.operation_name. Run database/sqlserver-bootstrap.sql before starting PoolSense.Api.', 1;
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}