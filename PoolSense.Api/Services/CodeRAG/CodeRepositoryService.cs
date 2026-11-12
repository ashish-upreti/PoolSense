using Microsoft.Extensions.Options;
using PoolSense.Api.Data;
using PoolSense.Api.Logging;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeRepositoryService
{
    Task<IReadOnlyList<CodeRepositoryResponse>> ListAsync(string projectId, CancellationToken token);
    Task<GitHubBranches> BranchesAsync(string url, CancellationToken token);
    Task<CodeRepositoryResponse> SaveAsync(string projectId, string? id, SaveCodeRepositoryRequest request, CancellationToken token);
    Task<CodeRepositoryResponse> RefreshAsync(string projectId, string id, CancellationToken token);
    Task DeleteAsync(string projectId, string id, CancellationToken token);
}

public sealed class CodeRepositoryService(ICodeRepositoryStore store, IProjectRepository projects, IGitHubCodeClient github,
    CodeIndexFiles files, IOptions<CodeRAGOptions> options, IUserActivityAuditLogger audit, IManagedGitHubAccess access) : ICodeRepositoryService
{
    public async Task<IReadOnlyList<CodeRepositoryResponse>> ListAsync(string projectId, CancellationToken token)
    {
        if (!access.IsConfigured)
            throw new CodeAccessException("Server-managed GitHub access is not configured. Configure CodeRAG:GitHubToken.");
        var candidates = (await store.RepositoriesAsync(token)).Where(entry => entry.ProjectId == projectId);
        var visible = new List<CodeRepositoryResponse>();
        var denied = false;
        foreach (var repository in candidates)
        {
            try
            {
                await CheckRepositoryAsync(repository, token);
                visible.Add(Response(repository));
            }
            catch (CodeAccessException) { denied = true; }
        }
        if (denied)
            throw new CodeAccessException("The server-managed PAT cannot access one or more configured repositories. Verify its validity, repository permissions and organization SSO authorization.");
        return visible;
    }

    public Task<GitHubBranches> BranchesAsync(string url, CancellationToken token) =>
        github.BranchesAsync(GitHubCodeClient.ParseUrl(url), token);

    private async Task<CodeRepository> FindAsync(string projectId, string id, CancellationToken token)
    {
        var record = (await store.RepositoriesAsync(token)).FirstOrDefault(entry => entry.Id == id && entry.ProjectId == projectId)
            ?? throw new KeyNotFoundException();
        await CheckRepositoryAsync(record, token);
        return record;
    }

    private async Task CheckRepositoryAsync(CodeRepository repository, CancellationToken token)
    {
        var metadata = await github.RepositoryAsync(repository.FullName, token);
        if (metadata.Id != repository.GitHubId) throw new CodeAccessException();
    }

    public async Task<CodeRepositoryResponse> SaveAsync(string projectId, string? id, SaveCodeRepositoryRequest request, CancellationToken token)
    {
        if (await projects.GetProjectByIdAsync(projectId, token) is null) throw new KeyNotFoundException();
        var old = id is null ? null : await FindAsync(projectId, id, token);
        var metadata = await github.RepositoryAsync(GitHubCodeClient.ParseUrl(request.Url), token);
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "master" : request.Branch.Trim();
        var changed = old is null || old.GitHubId != metadata.Id || old.Branch != branch;
        var record = (old ?? new CodeRepository()) with
        {
            ProjectId = projectId, GitHubId = metadata.Id, FullName = metadata.FullName, Branch = branch,
            Enabled = request.Enabled, Revision = old is null ? 0 : old.Revision + 1,
            Status = request.Enabled ? "queued" : "disabled", Message = "", NextCheckAt = DateTimeOffset.MinValue,
            Generation = changed ? "" : old!.Generation, IndexedCommit = changed ? "" : old!.IndexedCommit,
            LastIndexedAt = changed ? null : old!.LastIndexedAt, LastCheckedAt = changed ? null : old!.LastCheckedAt,
            FileCount = changed ? 0 : old!.FileCount, SymbolCount = changed ? 0 : old!.SymbolCount
        };
        await github.CommitAsync(record, token);
        await files.Gate.WaitAsync(token);
        try
        {
            if (old is null)
            {
                if ((await store.RepositoriesAsync(token)).Count(entry => entry.ProjectId == projectId) >= options.Value.MaxRepositoriesPerProject)
                    throw new ArgumentException("Project repository limit reached.");
                await store.AddAsync(record, token);
            }
            else if (!await store.UpdateAsync(record, old.Revision, token)) throw new InvalidOperationException();
        }
        finally { files.Gate.Release(); }
        await audit.LogAsync(old is null ? "AddCodeRepository" : "UpdateCodeRepository", "CodeRepository", record.Id, "Repository configuration saved.", cancellationToken: token);
        return Response(record);
    }

    public async Task<CodeRepositoryResponse> RefreshAsync(string projectId, string id, CancellationToken token)
    {
        var old = await FindAsync(projectId, id, token);
        if (!old.Enabled || old.Status == "indexing") throw new InvalidOperationException();
        var record = old with { Revision = old.Revision + 1, Status = "queued", NextCheckAt = DateTimeOffset.MinValue, Message = "" };
        if (!await store.UpdateAsync(record, old.Revision, token)) throw new InvalidOperationException();
        await audit.LogAsync("RefreshCodeRepository", "CodeRepository", id, "Repository refresh queued.", cancellationToken: token);
        return Response(record);
    }

    public async Task DeleteAsync(string projectId, string id, CancellationToken token)
    {
        var record = await FindAsync(projectId, id, token);
        await files.Gate.WaitAsync(token);
        try
        {
            if (!await store.DeleteAsync(id, record.Revision, token)) throw new InvalidOperationException();
        }
        finally { files.Gate.Release(); }
        await audit.LogAsync("RemoveCodeRepository", "CodeRepository", id, "Repository removed from query scope.", cancellationToken: token);
    }

    private static CodeRepositoryResponse Response(CodeRepository repository) => new(repository.Id, repository.ProjectId,
        $"https://github.com/{repository.FullName}", repository.Branch, repository.Enabled, repository.Status,
        repository.Message, repository.IndexedCommit, repository.FileCount, repository.SymbolCount, repository.LastCheckedAt, repository.LastIndexedAt);
}