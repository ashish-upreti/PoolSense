using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Options;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using PoolSense.Application.Models;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeKnowledgeService
{
    Task<CodeRetrievalResult> ForRequestAsync(string query, TicketRequest request, CancellationToken token);
    Task<CodeRetrievalResult> RetrieveAsync(string query, IReadOnlyList<string> projectIds, CancellationToken token);
}

public sealed class CodeKnowledgeService(ICodeRepositoryStore store, IProjectRepository projects, IGitHubCodeClient github,
    CodeIndexFiles files, IHttpContextAccessor context, IOptions<CodeRAGOptions> options) : ICodeKnowledgeService
{
    public async Task<CodeRetrievalResult> ForRequestAsync(string query, TicketRequest request, CancellationToken token)
    {
        if (!options.Value.Enabled || CodeUser.Name(context.HttpContext?.User).Length == 0) return CodeRetrievalResult.Skipped;
        try
        {
            IReadOnlyList<string> ids;
            if (request.SelectedGroupIds is { Count: > 0 }) ids = request.SelectedGroupIds;
            else if (!string.IsNullOrWhiteSpace(request.Application))
            {
                var project = await projects.GetProjectByApplicationFilterAsync(request.Application, token);
                ids = project is null ? [] : [project.ProjectId];
            }
            else if (!string.IsNullOrWhiteSpace(request.ApplicationId)) ids = [request.ApplicationId];
            else ids = (await projects.GetAllProjectsAsync(token)).Select(project => project.ProjectId).ToArray();
            return await RetrieveAsync(query, ids, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return CodeRetrievalResult.Skipped; }
    }

    public async Task<CodeRetrievalResult> RetrieveAsync(string query, IReadOnlyList<string> projectIds, CancellationToken token)
    {
        var user = CodeUser.Name(context.HttpContext?.User);
        if (!options.Value.Enabled || user.Length == 0 || projectIds.Count == 0) return CodeRetrievalResult.Skipped;
        if (context.HttpContext is { Response.HasStarted: false } httpContext) httpContext.Response.Headers.CacheControl = "no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var evidence = new List<CodeEvidence>();
        var stale = false;
        var used = 0;
        try
        {
            var connection = await store.ConnectionAsync(user, deadline.Token);
            if (connection is null) return new("not-connected", []);
            var candidates = (await store.RepositoriesAsync(deadline.Token))
                .Where(repository => repository.Enabled && projectIds.Contains(repository.ProjectId, StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var repository in candidates)
            {
                try
                {
                    var head = await github.CommitAsync(user, repository, deadline.Token);
                    await files.Gate.WaitAsync(deadline.Token);
                    try
                    {
                        var current = (await store.RepositoriesAsync(deadline.Token)).FirstOrDefault(entry => entry.Id == repository.Id);
                        if (current != repository || await store.ConnectionAsync(user, deadline.Token) != connection || !files.Ready(repository)) continue;
                        var remaining = options.Value.ContextCharacters - used;
                        if (remaining < 1000) break;
                        using var database = GraphStore.Open(files.Database(repository), true);
                        var symbols = new Retriever().Retrieve(query, database, files.Source(repository),
                            limit: Math.Max(1, 12 / Math.Max(1, candidates.Length)),
                            maxChars: Math.Min(remaining, Math.Max(2000, options.Value.ContextCharacters / Math.Max(1, candidates.Length))), token: deadline.Token);
                        foreach (var symbol in symbols)
                        {
                            var url = $"https://github.com/{repository.FullName}/blob/{repository.IndexedCommit}/{string.Join('/', symbol.FilePath.Split('/').Select(Uri.EscapeDataString))}#L{symbol.StartLine}-L{symbol.EndLine}";
                            evidence.Add(new(repository.Id, repository.ProjectId, repository.FullName, repository.Branch, repository.IndexedCommit,
                                symbol.FilePath, symbol.QualifiedName, symbol.StartLine, symbol.EndLine, symbol.Source, url));
                            used += symbol.Source.Length + url.Length + symbol.QualifiedName.Length;
                        }
                        stale |= repository.IndexedCommit != head || repository.Status is "error" or "access-required";
                    }
                    finally { files.Gate.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) when (!deadline.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return CodeRetrievalResult.Skipped; }
        return new(evidence.Count == 0 ? "no-accessible-matches" : stale ? "stale" : "ready", evidence);
    }
}