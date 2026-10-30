using System.Text.Json;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeRepositoryStore
{
    Task<GitHubConnection?> ConnectionAsync(string user, CancellationToken token);
    Task SaveConnectionAsync(GitHubConnection connection, CancellationToken token);
    Task DisconnectAsync(string user, CancellationToken token);
    Task<IReadOnlyList<CodeRepository>> RepositoriesAsync(CancellationToken token);
    Task AddAsync(CodeRepository repository, CancellationToken token);
    Task<bool> UpdateAsync(CodeRepository repository, int expectedRevision, CancellationToken token);
    Task<bool> DeleteAsync(string id, int revision, CancellationToken token);
}

public sealed class CodeRepositoryStore(IPoolSenseSqlConnectionFactory factory) : ICodeRepositoryStore
{
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

    public async Task<GitHubConnection?> ConnectionAsync(string user, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("SELECT github_id,login,protected_token FROM dbo.github_connections WHERE username=@user", connection);
        command.Parameters.AddWithValue("@user", user);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(user, reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task SaveConnectionAsync(GitHubConnection value, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            MERGE dbo.github_connections WITH (HOLDLOCK) AS target
            USING (SELECT @user AS username) AS source ON target.username=source.username
            WHEN MATCHED THEN UPDATE SET github_id=@github,login=@login,protected_token=@secret
            WHEN NOT MATCHED THEN INSERT(username,github_id,login,protected_token) VALUES(@user,@github,@login,@secret);
            """, connection);
        command.Parameters.AddWithValue("@user", value.UserName);
        command.Parameters.AddWithValue("@github", value.GitHubId);
        command.Parameters.AddWithValue("@login", value.Login);
        command.Parameters.AddWithValue("@secret", value.ProtectedToken);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task DisconnectAsync(string user, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("DELETE FROM dbo.github_connections WHERE username=@user", connection);
        command.Parameters.AddWithValue("@user", user);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<CodeRepository>> RepositoriesAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("SELECT state_json FROM dbo.project_code_repositories ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var records = new List<CodeRepository>();
        while (await reader.ReadAsync(token)) records.Add(JsonSerializer.Deserialize<CodeRepository>(reader.GetString(0))!);
        return records;
    }

    public async Task AddAsync(CodeRepository repository, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            INSERT dbo.project_code_repositories(id,project_id,github_id,branch,revision,state_json)
            VALUES(@id,@project,@github,@branch,@revision,@state)
            """, connection);
        Parameters(command, repository);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<bool> UpdateAsync(CodeRepository repository, int expectedRevision, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("""
            UPDATE dbo.project_code_repositories SET project_id=@project,github_id=@github,branch=@branch,revision=@revision,state_json=@state
            WHERE id=@id AND revision=@expected
            """, connection);
        Parameters(command, repository);
        command.Parameters.AddWithValue("@expected", expectedRevision);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<bool> DeleteAsync(string id, int revision, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand("DELETE dbo.project_code_repositories WHERE id=@id AND revision=@revision", connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@revision", revision);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    private static void Parameters(SqlCommand command, CodeRepository repository)
    {
        command.Parameters.AddWithValue("@id", repository.Id);
        command.Parameters.AddWithValue("@project", repository.ProjectId);
        command.Parameters.AddWithValue("@github", repository.GitHubId);
        command.Parameters.AddWithValue("@branch", repository.Branch);
        command.Parameters.AddWithValue("@revision", repository.Revision);
        command.Parameters.AddWithValue("@state", JsonSerializer.Serialize(repository));
    }
}