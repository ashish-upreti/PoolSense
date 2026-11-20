using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeChatHistoryStore
{
    Task<IReadOnlyList<CodeChatSessionSummary>> SessionsAsync(string user, CodeChatScope scope, CancellationToken token);
    Task<CodeChatSession?> SessionAsync(string user, string sessionId, CancellationToken token);
    Task<string> AppendExchangeAsync(string user, string? sessionId, CodeChatScope scope, string question, string answer,
        IReadOnlyList<CodeEvidence> citations, CancellationToken token, double? processingDurationMs = null, string? searchMode = null);
    Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token);
}

/// <summary>
/// Persists per-user CodeRAG conversations in dbo.code_chat_sessions / dbo.code_chat_messages.
/// Single-application sessions keep project_id populated; multi/all-application sessions store the scope in scope_json.
/// </summary>
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

    public async Task<IReadOnlyList<CodeChatSessionSummary>> SessionsAsync(string user, CodeChatScope scope, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (100) s.id, s.project_id, s.scope_json, s.title, s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM dbo.code_chat_messages m WHERE m.session_id = s.id) AS message_count
            FROM dbo.code_chat_sessions s
            WHERE s.username = @user
              AND ((@project IS NOT NULL AND s.project_id = @project AND s.scope_json IS NULL)
                   OR (@project IS NULL AND s.scope_json = @scope))
            ORDER BY s.updated_at DESC
            """, connection);
        command.Parameters.AddWithValue("@user", user);
        AddScopeParameters(command, scope);
        await using var reader = await command.ExecuteReaderAsync(token);
        var sessions = new List<CodeChatSessionSummary>();
        while (await reader.ReadAsync(token))
            sessions.Add(ReadSummary(reader, reader.GetInt32(6)));
        return sessions;
    }

    public async Task<CodeChatSession?> SessionAsync(string user, string sessionId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        CodeChatSessionSummary summary;
        await using (var command = new SqlCommand("SELECT id, project_id, scope_json, title, created_at, updated_at FROM dbo.code_chat_sessions WHERE id=@id AND username=@user", connection))
        {
            command.Parameters.AddWithValue("@id", sessionId);
            command.Parameters.AddWithValue("@user", user);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            summary = ReadSummary(reader, 0);
        }

        await using var messages = new SqlCommand("SELECT role, content, citations_json, created_at FROM dbo.code_chat_messages WHERE session_id=@id ORDER BY id", connection);
        messages.Parameters.AddWithValue("@id", sessionId);
        await using var messageReader = await messages.ExecuteReaderAsync(token);
        var list = new List<CodeChatStoredMessage>();
        while (await messageReader.ReadAsync(token))
        {
            var (citations, processingDurationMs, searchMode) = ParseAssistantPayload(messageReader.IsDBNull(2) ? null : messageReader.GetString(2));
            list.Add(new(messageReader.GetString(0), messageReader.GetString(1), citations, messageReader.GetDateTimeOffset(3))
            {
                ProcessingDurationMs = processingDurationMs,
                SearchMode = searchMode,
            });
        }
        return new(summary with { MessageCount = list.Count }, list);
    }

    public async Task<string> AppendExchangeAsync(string user, string? sessionId, CodeChatScope scope, string question, string answer,
        IReadOnlyList<CodeEvidence> citations, CancellationToken token, double? processingDurationMs = null, string? searchMode = null)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var now = DateTimeOffset.UtcNow;

        string? owned = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await using var find = new SqlCommand("""
                SELECT id FROM dbo.code_chat_sessions
                WHERE id=@id AND username=@user
                  AND ((@project IS NOT NULL AND project_id=@project AND scope_json IS NULL)
                       OR (@project IS NULL AND scope_json=@scope))
                """, connection, transaction);
            find.Parameters.AddWithValue("@id", sessionId);
            find.Parameters.AddWithValue("@user", user);
            AddScopeParameters(find, scope);
            owned = await find.ExecuteScalarAsync(token) as string;
        }

        if (owned is null)
        {
            owned = Guid.NewGuid().ToString("N");
            await using var insert = new SqlCommand("""
                INSERT dbo.code_chat_sessions(id, username, project_id, scope_json, title, created_at, updated_at)
                VALUES(@id, @user, @project, @scope, @title, @now, @now)
                """, connection, transaction);
            insert.Parameters.AddWithValue("@id", owned);
            insert.Parameters.AddWithValue("@user", user);
            AddScopeParameters(insert, scope);
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
        await InsertMessageAsync(connection, transaction, owned, "assistant", answer,
            SerializeAssistantPayload(citations, processingDurationMs, searchMode), now, token);
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

    private static void AddScopeParameters(SqlCommand command, CodeChatScope scope)
    {
        command.Parameters.Add("@project", SqlDbType.NVarChar, 128).Value = (object?)scope.ProjectId ?? DBNull.Value;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, -1).Value = (object?)scope.ScopeJson ?? DBNull.Value;
    }

    private static CodeChatSessionSummary ReadSummary(SqlDataReader reader, int messageCount)
    {
        var projectId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var scope = CodeChatScope.FromStored(projectId, reader.IsDBNull(2) ? null : reader.GetString(2));
        return new(reader.GetString(0), scope.ProjectId ?? "", reader.GetString(3), reader.GetDateTimeOffset(4), reader.GetDateTimeOffset(5), messageCount)
        {
            ProjectIds = scope.ProjectIds
        };
    }

    private sealed record StoredMessageMetadata(List<CodeEvidence>? Citations, double? ProcessingDurationMs, string? SearchMode);

    internal static string SerializeAssistantPayload(IReadOnlyList<CodeEvidence> citations, double? processingDurationMs, string? searchMode)
    {
        var validDuration = processingDurationMs is >= 0 and var ms && double.IsFinite(ms) ? ms : (double?)null;
        var validMode = searchMode is "deep" or "quick" ? searchMode : null;
        if (validDuration is null && validMode is null)
            return JsonSerializer.Serialize(citations, JsonOptions);
        return JsonSerializer.Serialize(new StoredMessageMetadata([.. citations], validDuration, validMode), JsonOptions);
    }

    internal static (IReadOnlyList<CodeEvidence> Citations, double? ProcessingDurationMs, string? SearchMode) ParseAssistantPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], null, null);
        var trimmed = json.AsSpan().TrimStart();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
            return (JsonSerializer.Deserialize<List<CodeEvidence>>(json, JsonOptions) ?? [], null, null);
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            var meta = JsonSerializer.Deserialize<StoredMessageMetadata>(json, JsonOptions);
            var validDuration = meta?.ProcessingDurationMs is >= 0 and var ms && double.IsFinite(ms) ? ms : (double?)null;
            var validMode = meta?.SearchMode is "deep" or "quick" ? meta.SearchMode : null;
            return (meta?.Citations ?? [], validDuration, validMode);
        }
        return ([], null, null);
    }

    private static string MakeTitle(string question)
    {
        var singleLine = string.Join(' ', question.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "...";
    }
}
