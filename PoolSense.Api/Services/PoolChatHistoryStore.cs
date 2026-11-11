using System.Text.Json;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public interface IPoolChatHistoryStore
{
    Task EnsureSchemaAsync(CancellationToken token);
    Task<IReadOnlyList<PoolChatSessionSummary>> SessionsAsync(string user, CancellationToken token);
    Task<PoolChatSession?> SessionAsync(string user, string sessionId, CancellationToken token, int? latestMessageLimit = null);
    Task<string> AppendExchangeAsync(string user, string? sessionId, string question, IReadOnlyList<string>? selectedGroupIds, TicketWorkflowResult result, CancellationToken token);
    Task<bool> UpdateSessionAsync(string user, string sessionId, string? title, bool? isPinned, CancellationToken token);
    Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token);
}

/// <summary>
/// Persists per-user PoolSense triage conversations in dbo.pool_chat_sessions / dbo.pool_chat_messages.
/// Every operation is scoped by username so users can only read or change their own history.
/// </summary>
public sealed class PoolChatHistoryStore(IPoolSenseSqlConnectionFactory factory) : IPoolChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _schemaReady;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private async Task<SqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = factory.CreateConnection();
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    /// <summary>Creates the history tables once per process when they do not exist yet (idempotent).</summary>
    public async Task EnsureSchemaAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _schemaReady) == 1)
        {
            return;
        }

        await SchemaGate.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref _schemaReady) == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(token);
            await using var command = new SqlCommand("""
                IF OBJECT_ID(N'dbo.pool_chat_sessions', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.pool_chat_sessions (
                        id nvarchar(32) NOT NULL PRIMARY KEY,
                        username nvarchar(128) NOT NULL,
                        title nvarchar(100) NOT NULL,
                        selected_group_ids_json nvarchar(max) NULL,
                        is_pinned bit NOT NULL DEFAULT 0,
                        created_at datetimeoffset NOT NULL,
                        updated_at datetimeoffset NOT NULL
                    );

                    CREATE INDEX IX_pool_chat_sessions_user ON dbo.pool_chat_sessions (username, is_pinned, updated_at DESC);
                END

                IF OBJECT_ID(N'dbo.pool_chat_messages', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.pool_chat_messages (
                        id int NOT NULL IDENTITY PRIMARY KEY,
                        session_id nvarchar(32) NOT NULL REFERENCES dbo.pool_chat_sessions(id) ON DELETE CASCADE,
                        role nvarchar(16) NOT NULL CHECK (role IN (N'user', N'assistant')),
                        content nvarchar(max) NOT NULL,
                        result_json nvarchar(max) NULL,
                        created_at datetimeoffset NOT NULL
                    );

                    CREATE INDEX IX_pool_chat_messages_session ON dbo.pool_chat_messages (session_id, id);
                END
                """, connection);
            await command.ExecuteNonQueryAsync(token);
            Volatile.Write(ref _schemaReady, 1);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    public async Task<IReadOnlyList<PoolChatSessionSummary>> SessionsAsync(string user, CancellationToken token)
    {
        await EnsureSchemaAsync(token);
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (200) s.id, s.title, s.selected_group_ids_json, s.is_pinned, s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM dbo.pool_chat_messages m WHERE m.session_id = s.id) AS message_count
            FROM dbo.pool_chat_sessions s
            WHERE s.username = @user
            ORDER BY s.is_pinned DESC, s.updated_at DESC
            """, connection);
        command.Parameters.AddWithValue("@user", user);
        await using var reader = await command.ExecuteReaderAsync(token);
        var sessions = new List<PoolChatSessionSummary>();
        while (await reader.ReadAsync(token))
            sessions.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                DeserializeGroupIds(reader.IsDBNull(2) ? null : reader.GetString(2)),
                reader.GetBoolean(3),
                reader.GetDateTimeOffset(4),
                reader.GetDateTimeOffset(5),
                reader.GetInt32(6)));
        return sessions;
    }

    public async Task<PoolChatSession?> SessionAsync(string user, string sessionId, CancellationToken token, int? latestMessageLimit = null)
    {
        if (latestMessageLimit is <= 0) throw new ArgumentOutOfRangeException(nameof(latestMessageLimit));
        await EnsureSchemaAsync(token);
        await using var connection = await OpenAsync(token);
        PoolChatSessionSummary summary;
        await using (var command = new SqlCommand("""
            SELECT id, title, selected_group_ids_json, is_pinned, created_at, updated_at
            FROM dbo.pool_chat_sessions
            WHERE id = @id AND username = @user
            """, connection))
        {
            command.Parameters.AddWithValue("@id", sessionId);
            command.Parameters.AddWithValue("@user", user);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            summary = new(
                reader.GetString(0),
                reader.GetString(1),
                DeserializeGroupIds(reader.IsDBNull(2) ? null : reader.GetString(2)),
                reader.GetBoolean(3),
                reader.GetDateTimeOffset(4),
                reader.GetDateTimeOffset(5),
                0);
        }

        await using var messages = new SqlCommand("""
            SELECT role, content, result_json, created_at
            FROM (
                SELECT TOP (@limit) m.id, m.role, m.content, m.result_json, m.created_at
                FROM dbo.pool_chat_messages m
                INNER JOIN dbo.pool_chat_sessions s ON s.id = m.session_id
                WHERE s.id = @id AND s.username = @user
                ORDER BY m.id DESC
            ) recent
            ORDER BY id
            """, connection);
        messages.Parameters.AddWithValue("@id", sessionId);
        messages.Parameters.AddWithValue("@user", user);
        messages.Parameters.AddWithValue("@limit", latestMessageLimit ?? int.MaxValue);
        await using var messageReader = await messages.ExecuteReaderAsync(token);
        var list = new List<PoolChatStoredMessage>();
        while (await messageReader.ReadAsync(token))
        {
            var result = messageReader.IsDBNull(2)
                ? null
                : JsonSerializer.Deserialize<TicketWorkflowResult>(messageReader.GetString(2), JsonOptions);
            list.Add(new(messageReader.GetString(0), messageReader.GetString(1), result, messageReader.GetDateTimeOffset(3)));
        }

        return new(summary with { MessageCount = list.Count }, list);
    }

    public async Task<string> AppendExchangeAsync(string user, string? sessionId, string question, IReadOnlyList<string>? selectedGroupIds,
        TicketWorkflowResult result, CancellationToken token)
    {
        await EnsureSchemaAsync(token);
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var now = DateTimeOffset.UtcNow;

        string? owned = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await using var find = new SqlCommand("SELECT id FROM dbo.pool_chat_sessions WHERE id = @id AND username = @user", connection, transaction);
            find.Parameters.AddWithValue("@id", sessionId);
            find.Parameters.AddWithValue("@user", user);
            owned = await find.ExecuteScalarAsync(token) as string;
        }

        if (owned is null)
        {
            owned = Guid.NewGuid().ToString("N");
            await using var insert = new SqlCommand("""
                INSERT dbo.pool_chat_sessions(id, username, title, selected_group_ids_json, is_pinned, created_at, updated_at)
                VALUES(@id, @user, @title, @groups, 0, @now, @now)
                """, connection, transaction);
            insert.Parameters.AddWithValue("@id", owned);
            insert.Parameters.AddWithValue("@user", user);
            insert.Parameters.AddWithValue("@title", MakeTitle(question));
            insert.Parameters.AddWithValue("@groups", (object?)SerializeGroupIds(selectedGroupIds) ?? DBNull.Value);
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var touch = new SqlCommand("UPDATE dbo.pool_chat_sessions SET updated_at = @now WHERE id = @id", connection, transaction);
            touch.Parameters.AddWithValue("@now", now);
            touch.Parameters.AddWithValue("@id", owned);
            await touch.ExecuteNonQueryAsync(token);
        }

        await InsertMessageAsync(connection, transaction, owned, "user", question, null, now, token);
        await InsertMessageAsync(connection, transaction, owned, "assistant", result.SuggestedResolution,
            JsonSerializer.Serialize(result, JsonOptions), now, token);
        await transaction.CommitAsync(token);
        return owned;
    }

    public async Task<bool> UpdateSessionAsync(string user, string sessionId, string? title, bool? isPinned, CancellationToken token)
    {
        await EnsureSchemaAsync(token);
        var trimmedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (trimmedTitle is null && isPinned is null)
        {
            return false;
        }

        await using var connection = await OpenAsync(token);
        var updates = new List<string>();
        if (trimmedTitle is not null)
        {
            updates.Add("title = @title");
        }

        if (isPinned is not null)
        {
            updates.Add("is_pinned = @pinned");
        }

        await using var command = new SqlCommand($"UPDATE dbo.pool_chat_sessions SET {string.Join(", ", updates)} WHERE id = @id AND username = @user", connection);
        if (trimmedTitle is not null)
        {
            command.Parameters.AddWithValue("@title", trimmedTitle[..Math.Min(trimmedTitle.Length, 100)]);
        }

        if (isPinned is not null)
        {
            command.Parameters.AddWithValue("@pinned", isPinned.Value);
        }

        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@user", user);
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    public async Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token)
    {
        await EnsureSchemaAsync(token);
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("DELETE dbo.pool_chat_sessions WHERE id = @id AND username = @user", connection);
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@user", user);
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    private static async Task InsertMessageAsync(SqlConnection connection, SqlTransaction transaction, string sessionId, string role,
        string content, string? resultJson, DateTimeOffset createdAt, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            INSERT dbo.pool_chat_messages(session_id, role, content, result_json, created_at)
            VALUES(@session, @role, @content, @result, @createdAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", createdAt);
        await command.ExecuteNonQueryAsync(token);
    }

    private static string? SerializeGroupIds(IReadOnlyList<string>? selectedGroupIds) =>
        selectedGroupIds is { Count: > 0 } ? JsonSerializer.Serialize(selectedGroupIds, JsonOptions) : null;

    private static IReadOnlyList<string> DeserializeGroupIds(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];

    private static string MakeTitle(string question)
    {
        var singleLine = string.Join(' ', question.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "...";
    }
}
