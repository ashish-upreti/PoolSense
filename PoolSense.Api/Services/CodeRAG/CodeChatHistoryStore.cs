using System.Text.Json;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeChatHistoryStore
{
    Task<IReadOnlyList<CodeChatSessionSummary>> SessionsAsync(string user, string? projectId, CancellationToken token);
    Task<CodeChatSession?> SessionAsync(string user, string sessionId, CancellationToken token);
    Task<string> AppendExchangeAsync(string user, string? sessionId, string projectId, string question, string answer, IReadOnlyList<CodeEvidence> citations, CancellationToken token);
    Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token);
}

/// <summary>Persists per-user CodeRAG conversations in dbo.code_chat_sessions / dbo.code_chat_messages.</summary>
public sealed class CodeChatHistoryStore(IPoolSenseSqlConnectionFactory factory) : ICodeChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public async Task<IReadOnlyList<CodeChatSessionSummary>> SessionsAsync(string user, string? projectId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (100) s.id, s.project_id, s.title, s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM dbo.code_chat_messages m WHERE m.session_id = s.id) AS message_count
            FROM dbo.code_chat_sessions s
            WHERE s.username = @user AND (@project IS NULL OR s.project_id = @project)
            ORDER BY s.updated_at DESC
            """, connection);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.AddWithValue("@project", string.IsNullOrWhiteSpace(projectId) ? DBNull.Value : projectId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var sessions = new List<CodeChatSessionSummary>();
        while (await reader.ReadAsync(token))
            sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDateTimeOffset(3), reader.GetDateTimeOffset(4), reader.GetInt32(5)));
        return sessions;
    }

    public async Task<CodeChatSession?> SessionAsync(string user, string sessionId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        CodeChatSessionSummary summary;
        await using (var command = new SqlCommand("SELECT id, project_id, title, created_at, updated_at FROM dbo.code_chat_sessions WHERE id=@id AND username=@user", connection))
        {
            command.Parameters.AddWithValue("@id", sessionId);
            command.Parameters.AddWithValue("@user", user);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            summary = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDateTimeOffset(3), reader.GetDateTimeOffset(4), 0);
        }

        await using var messages = new SqlCommand("SELECT role, content, citations_json, created_at FROM dbo.code_chat_messages WHERE session_id=@id ORDER BY id", connection);
        messages.Parameters.AddWithValue("@id", sessionId);
        await using var messageReader = await messages.ExecuteReaderAsync(token);
        var list = new List<CodeChatStoredMessage>();
        while (await messageReader.ReadAsync(token))
        {
            var citations = messageReader.IsDBNull(2) ? [] : JsonSerializer.Deserialize<List<CodeEvidence>>(messageReader.GetString(2), JsonOptions) ?? [];
            list.Add(new(messageReader.GetString(0), messageReader.GetString(1), citations, messageReader.GetDateTimeOffset(3)));
        }
        return new(summary with { MessageCount = list.Count }, list);
    }

    public async Task<string> AppendExchangeAsync(string user, string? sessionId, string projectId, string question, string answer,
        IReadOnlyList<CodeEvidence> citations, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var now = DateTimeOffset.UtcNow;

        string? owned = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await using var find = new SqlCommand("SELECT id FROM dbo.code_chat_sessions WHERE id=@id AND username=@user AND project_id=@project", connection, transaction);
            find.Parameters.AddWithValue("@id", sessionId);
            find.Parameters.AddWithValue("@user", user);
            find.Parameters.AddWithValue("@project", projectId);
            owned = await find.ExecuteScalarAsync(token) as string;
        }

        if (owned is null)
        {
            owned = Guid.NewGuid().ToString("N");
            await using var insert = new SqlCommand("""
                INSERT dbo.code_chat_sessions(id, username, project_id, title, created_at, updated_at)
                VALUES(@id, @user, @project, @title, @now, @now)
                """, connection, transaction);
            insert.Parameters.AddWithValue("@id", owned);
            insert.Parameters.AddWithValue("@user", user);
            insert.Parameters.AddWithValue("@project", projectId);
            insert.Parameters.AddWithValue("@title", MakeTitle(question));
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var touch = new SqlCommand("UPDATE dbo.code_chat_sessions SET updated_at=@now WHERE id=@id", connection, transaction);
            touch.Parameters.AddWithValue("@now", now);
            touch.Parameters.AddWithValue("@id", owned);
            await touch.ExecuteNonQueryAsync(token);
        }

        await InsertMessageAsync(connection, transaction, owned, "user", question, null, now, token);
        await InsertMessageAsync(connection, transaction, owned, "assistant", answer, JsonSerializer.Serialize(citations, JsonOptions), now, token);
        await transaction.CommitAsync(token);
        return owned;
    }

    public async Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("DELETE dbo.code_chat_sessions WHERE id=@id AND username=@user", connection);
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@user", user);
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    private static async Task InsertMessageAsync(SqlConnection connection, SqlTransaction transaction, string sessionId, string role,
        string content, string? citationsJson, DateTimeOffset createdAt, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            INSERT dbo.code_chat_messages(session_id, role, content, citations_json, created_at)
            VALUES(@session, @role, @content, @citations, @createdAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@citations", (object?)citationsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", createdAt);
        await command.ExecuteNonQueryAsync(token);
    }

    private static string MakeTitle(string question)
    {
        var singleLine = string.Join(' ', question.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "...";
    }
}
