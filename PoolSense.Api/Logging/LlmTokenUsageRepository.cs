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
            BEGIN
                CREATE TABLE dbo.llm_token_usage (
                    id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_llm_token_usage PRIMARY KEY,
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_llm_token_usage_created_at DEFAULT SYSUTCDATETIME(),
                    service_type nvarchar(32) NOT NULL,
                    operation_name nvarchar(128) NOT NULL,
                    provider nvarchar(64) NOT NULL CONSTRAINT DF_llm_token_usage_provider DEFAULT '',
                    model nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_model DEFAULT '',
                    deployment_name nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_deployment_name DEFAULT '',
                    prompt_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_prompt_tokens DEFAULT 0,
                    completion_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_completion_tokens DEFAULT 0,
                    total_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_total_tokens DEFAULT 0,
                    is_estimated bit NOT NULL CONSTRAINT DF_llm_token_usage_is_estimated DEFAULT 0,
                    input_characters int NOT NULL CONSTRAINT DF_llm_token_usage_input_characters DEFAULT 0,
                    output_characters int NOT NULL CONSTRAINT DF_llm_token_usage_output_characters DEFAULT 0,
                    vector_dimensions int NULL,
                    latency_ms int NOT NULL CONSTRAINT DF_llm_token_usage_latency_ms DEFAULT 0,
                    success bit NOT NULL CONSTRAINT DF_llm_token_usage_success DEFAULT 1,
                    error_message nvarchar(max) NOT NULL CONSTRAINT DF_llm_token_usage_error_message DEFAULT '',
                    correlation_id nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_correlation_id DEFAULT '',
                    machine_name nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_machine_name DEFAULT '',
                    user_name nvarchar(256) NOT NULL CONSTRAINT DF_llm_token_usage_user_name DEFAULT '',
                    process_id int NOT NULL CONSTRAINT DF_llm_token_usage_process_id DEFAULT 0
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_llm_token_usage_created_at' AND object_id = OBJECT_ID(N'dbo.llm_token_usage'))
                CREATE INDEX IX_llm_token_usage_created_at ON dbo.llm_token_usage (created_at DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_llm_token_usage_service_operation' AND object_id = OBJECT_ID(N'dbo.llm_token_usage'))
                CREATE INDEX IX_llm_token_usage_service_operation ON dbo.llm_token_usage (service_type, operation_name, created_at DESC);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}