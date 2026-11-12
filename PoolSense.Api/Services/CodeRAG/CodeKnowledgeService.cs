using System.Collections.Concurrent;
using System.Security.Claims;
using CodeRAG.Core.Retrieval;
using CodeRAG.Core.Storage;
using Microsoft.Extensions.Options;
using PoolSense.Api.Data;
using PoolSense.Application.Models;

namespace PoolSense.Api.Services.CodeRAG;

public interface ICodeKnowledgeService
{
    Task<CodeRetrievalResult> ForRequestAsync(string query, TicketRequest request, CancellationToken token, CodeQueryEmbedding? reusable = null);
    Task<CodeRetrievalResult> ForBackgroundPollingAsync(string query, TicketRequest request, CancellationToken token);
    Task<CodeRetrievalResult> RetrieveAsync(string query, IReadOnlyList<string> projectIds, CancellationToken token);
}

public sealed class CodeKnowledgeService(ICodeRepositoryStore store, IProjectRepository projects, IGitHubCodeClient github, IManagedGitHubAccess access,
    CodeIndexFiles files, IHttpContextAccessor context, IOptions<CodeRAGOptions> options,
    ILogger<CodeKnowledgeService>? logger = null, CodeSemanticIndex? semantic = null, IOptions<OkfOptions>? knowledgeOptions = null) : ICodeKnowledgeService
{
    private int queryEmbeddingRequests;

    private Task<CodeSemanticQueryResult> CreateSemanticQueryAsync(string query, CodeQueryEmbedding? reusable,
        string cacheScope, CancellationToken token)
    {
        // Request-scoped budget also covers repository-chat's lexical retry.
        if (Interlocked.Increment(ref queryEmbeddingRequests) > 1)
            return Task.FromResult(new CodeSemanticQueryResult("unavailable", null, "QueryBudgetExhausted"));
        return semantic!.CreateQueryAsync(query, reusable, token, cacheScope);
    }

    public async Task<CodeRetrievalResult> ForRequestAsync(string query, TicketRequest request, CancellationToken token, CodeQueryEmbedding? reusable = null)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Value.Enabled) return new("disabled", []);
        if (CodeUser.Name(context.HttpContext?.User).Length == 0) return new("not-authenticated", []);
        return await ForScopedRequestAsync(query, request, token, reusable, backgroundPolling: false);
    }

    public async Task<CodeRetrievalResult> ForBackgroundPollingAsync(string query, TicketRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Value.Enabled) return new("disabled", []);
        if (!access.IsConfigured)
            return new("not-configured", []) { Coverage = new() { Warnings = ["Configure the server-managed GitHub PAT to enable code retrieval for new-pool polling."] } };
        return await ForScopedRequestAsync(query, request, token, reusable: null, backgroundPolling: true);
    }

    private async Task<CodeRetrievalResult> ForScopedRequestAsync(string query, TicketRequest request, CancellationToken token,
        CodeQueryEmbedding? reusable, bool backgroundPolling)
    {
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
            else ids = backgroundPolling ? [] : (await projects.GetAllProjectsAsync(token)).Select(project => project.ProjectId).ToArray();
            return await RetrieveCoreAsync(query, ids, reusable, token, backgroundPolling);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning("Code scope resolution failed ({FailureType}).", exception.GetType().Name);
            return new("failed", []) { Coverage = new() { Warnings = ["Code scope could not be resolved."] } };
        }
    }

    public Task<CodeRetrievalResult> RetrieveAsync(string query, IReadOnlyList<string> projectIds, CancellationToken token) =>
        RetrieveCoreAsync(query, projectIds, null, token, backgroundPolling: false);

    private async Task<CodeRetrievalResult> RetrieveCoreAsync(string query, IReadOnlyList<string> projectIds, CodeQueryEmbedding? reusable,
        CancellationToken token, bool backgroundPolling)
    {
        token.ThrowIfCancellationRequested();
        var user = backgroundPolling ? SharedGitHubConnection.ServiceIdentity : CodeUser.Name(context.HttpContext?.User);
        var settings = options.Value;
        if (!settings.Enabled) return new("disabled", []);
        if (user.Length == 0) return new("not-authenticated", []);
        if (projectIds.Count == 0) return new("not-scoped", []);
        token.ThrowIfCancellationRequested();
        if (context.HttpContext is { Response.HasStarted: false } httpContext) httpContext.Response.Headers.CacheControl = "no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.SearchDeadlineSeconds));
        var candidates = new CodeCandidateBuffer(query, settings.GlobalCandidateLimit);
        var authorized = new ConcurrentDictionary<string, CodeRepository>();
        var searched = new ConcurrentDictionary<string, byte>();
        var deferred = new ConcurrentBag<CodeRepository>();
        var semanticStates = new ConcurrentDictionary<string, byte>();
        var semanticCoverage = new ConcurrentDictionary<string, CodeSemanticCoverage>();
        var knowledgeStates = new ConcurrentDictionary<string, byte>();
        var warnings = new ConcurrentDictionary<string, byte>();
        var failed = 0;
        var notReady = 0;
        var shortlisted = 0;
        var stale = 0;
        var authorizationComplete = false;
        var expanded = false;
        var timedOut = false;
        ManagedGitHubIdentity? connection;
        CodeRepository[] repositories;
        try
        {
            connection = await access.GetIdentityAsync(deadline.Token);
            repositories = (await store.RepositoriesAsync(deadline.Token))
                .Where(repository => repository.Enabled && projectIds.Contains(repository.ProjectId, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new("timed-out", []) { Coverage = new() { DeadlineExceeded = true, AuthorizationComplete = false } };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (CodeAccessException exception)
        {
            var accessStatus = access.IsConfigured ? "not-connected" : "not-configured";
            return new(accessStatus, []) { Coverage = new() { Warnings = [exception.Message] } };
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Code repository catalog unavailable ({FailureType}).", exception.GetType().Name);
            return new("failed", []) { Coverage = new() { Warnings = ["The code repository catalog is unavailable."] } };
        }
        if (connection is null) return new("not-connected", []);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = settings.SearchConcurrency, CancellationToken = deadline.Token };
        var semanticQuery = new Lazy<Task<CodeSemanticQueryResult>>(
            () => CreateSemanticQueryAsync(query, reusable,
                QueryCacheScope(backgroundPolling ? null : context.HttpContext?.User, user), deadline.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);

        async ValueTask SearchRepository(CodeRepository repository, bool authorize, bool allowDeferral, CancellationToken ct)
        {
            var accessible = !authorize;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (authorize)
                {
                    // This is a CURRENT permission check, not a head/freshness lookup.
                    // No OKF, embeddings, files, names or routing metadata are consumed before it.
                    var repositoryAccess = await github.RepositoryAsync(repository.FullName, ct);
                    if (repositoryAccess.Id != repository.GitHubId) throw new CodeAccessException();
                    accessible = true;
                    authorized[repository.Id] = repository;
                }
                using var lease = repository.Generation.Length > 0 ? files.Lease(repository) : null;
                if (lease is null || !files.Ready(repository))
                {
                    Interlocked.Increment(ref notReady);
                    return;
                }
                if (repository.Status is "error" or "access-required" or "indexing" or "queued" || repository.LastCheckedAt is null
                    || DateTimeOffset.UtcNow - repository.LastCheckedAt > TimeSpan.FromMinutes(settings.RefreshMinutes * 2))
                    Interlocked.Exchange(ref stale, 1);
                OkfSearchResult? okf = null;
                var knownVectorCount = -1;
                try
                {
                    var statePath = Path.Combine(files.Folder(repository), CodeSnapshotPreparation.FileName);
                    if (File.Exists(statePath) && new FileInfo(statePath).Length <= 16384 &&
                        System.Text.Json.JsonSerializer.Deserialize<CodeSnapshotPreparation>(File.ReadAllText(statePath)) is { } preparation)
                    {
                        if (!preparation.Complete)
                        {
                            warnings.TryAdd("Optional project knowledge or semantic preparation is incomplete; current indexed source remains searchable.", 0);
                            knowledgeStates.TryAdd(preparation.KnowledgeStatus, 0);
                        }
                        if (preparation.KnowledgeLimitations.Count > 0) knowledgeStates.TryAdd("bounded", 0);
                        if (preparation.SemanticSelectionPolicy.Length > 0) knownVectorCount = preparation.SemanticIndexedChunks;
                    }
                    if (knowledgeOptions?.Value.Enabled == true)
                        okf = OkfBundle.SearchExisting(repository, repository.IndexedCommit, files.Database(repository), files.Source(repository),
                            files.Folder(repository), query, knowledgeOptions.Value, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    knowledgeStates.TryAdd("failed", 0);
                    warnings.TryAdd("Project knowledge could not be read; lexical and graph search is retained.", 0);
                    logger?.LogWarning("Project knowledge unavailable for {RepositoryId} ({FailureType}).", repository.Id, exception.GetType().Name);
                }
                if (okf is not null)
                {
                    knowledgeStates.TryAdd(okf.Status, 0);
                    if (okf.Errors.Count > 0)
                        warnings.TryAdd("Some project knowledge is incomplete, stale or invalid; current indexed source remains authoritative.", 0);
                }
                var routeMatch = CodeCandidateSelection.Terms(query).Any(term => repository.FullName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || okf?.References.Count > 0;
                if (allowDeferral && settings.RepositoryRoutingEnabled &&
                    (!routeMatch || Interlocked.Increment(ref shortlisted) > settings.RepositoryShortlistSize))
                {
                    deferred.Add(repository);
                    return;
                }
                if (okf is not null)
                    foreach (var reference in okf.References)
                        candidates.Add(new(repository, reference.Symbol, KnowledgeReference: true) { KnowledgeRelevance = reference.Relevance });
                using var database = GraphStore.Open(files.Database(repository), true);
                // SQLite supports this cross-thread interrupt; dispose registration BEFORE the connection.
                using var interrupt = ct.Register(() => { if (database.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle); });
                var spans = new Retriever().Retrieve(query, database, files.Source(repository), settings.CandidatesPerRepository,
                    Retriever.MaxContextChars, token: ct);
                for (var index = 0; index < spans.Count; index++) candidates.Add(new(repository, spans[index], LexicalRank: index + 1));
                searched.TryAdd(repository.Id, 0);
                if (semantic?.Enabled == true)
                {
                    if (knownVectorCount != 0 && File.Exists(Path.Combine(files.Folder(repository), CodeSemanticIndex.ArtifactName))) _ = semanticQuery.Value;
                    else semanticStates.TryAdd(knownVectorCount == 0 ? "empty-index" : "missing-index", 0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (CodeAccessException)
            {
                warnings.TryAdd("The server-managed PAT could not access one or more configured repositories. Verify its validity, repository permissions and organization SSO authorization.", 0);
                logger?.LogWarning("GitHub repository access denied for record {RepositoryId}.", repository.Id);
            }
            catch (Exception exception)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (accessible) Interlocked.Increment(ref failed);
                logger?.LogWarning("Code repository search unavailable for record {RepositoryId} ({FailureType}).", repository.Id, exception.GetType().Name);
            }
        }

        try
        {
            await Parallel.ForEachAsync(repositories, parallel, (repository, ct) => SearchRepository(repository, true, true, ct));
            authorizationComplete = true;
            var crossRepository = System.Text.RegularExpressions.Regex.IsMatch(query, @"\b(workflow|across|integration)\b|end.to.end",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var terms = CodeCandidateSelection.Terms(query);
            var coveredTerms = terms.Count(term => candidates.Any(candidate =>
                $"{candidate.Symbol.QualifiedName} {candidate.Symbol.FilePath} {candidate.Symbol.Source}".Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (!deferred.IsEmpty && (crossRepository || !candidates.Any(candidate => CodeCandidateSelection.Relevance(query, candidate) >= 1)
                || terms.Length > 1 && (double)coveredTerms / terms.Length < .8))
            {
                expanded = true;
                await Parallel.ForEachAsync(deferred.DistinctBy(repository => repository.Id), parallel,
                    (repository, ct) => SearchRepository(repository, false, false, ct));
            }
            if (semanticQuery.IsValueCreated)
            {
                var queryResult = await semanticQuery.Value;
                semanticStates.TryAdd(queryResult.Status, 0);
                if (queryResult.Embedding is { } embedding)
                {
                    await Parallel.ForEachAsync(authorized.Values.Where(repository => searched.ContainsKey(repository.Id)), parallel, (repository, ct) =>
                    {
                        try
                        {
                            using var lease = files.Lease(repository);
                            if (lease is null) { semanticStates.TryAdd("snapshot-changed", 0); return ValueTask.CompletedTask; }
                            var result = semantic!.Search(files.Folder(repository), embedding, ct, repository.IndexedCommit);
                            semanticStates.TryAdd(result.Status, 0);
                            if (result.Coverage is { } coverage) semanticCoverage[repository.Id] = coverage;
                            using var database = GraphStore.Open(files.Database(repository), true);
                            for (var index = 0; index < result.Matches.Count; index++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var match = result.Matches[index];
                                if (match.Score < .45) continue;
                                var symbol = GraphStore.Query(database, "SELECT * FROM symbols WHERE id=@p0 AND repo_root=@p1",
                                    GraphStore.ReadSymbol, match.SymbolId, GraphStore.RootKey(files.Source(repository))).SingleOrDefault();
                                if (symbol is not null) candidates.Add(new(repository, symbol, SemanticRank: index + 1, SemanticSimilarity: match.Score));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            Interlocked.Increment(ref failed);
                            warnings.TryAdd("A semantic index could not be read; lexical evidence is retained.", 0);
                            logger?.LogWarning("Semantic code search failed for {RepositoryId} ({FailureType}).", repository.Id, exception.GetType().Name);
                        }
                        return ValueTask.CompletedTask;
                    });
                }
                else warnings.TryAdd("Semantic retrieval unavailable; lexical and graph evidence is retained.", 0);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            timedOut = true;
            warnings.TryAdd("Code deadline reached. Accumulated evidence is retained; coverage is incomplete.", 0);
        }
        finally
        {
            if (semanticQuery.IsValueCreated)
            {
                try { await semanticQuery.Value; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
        }
        token.ThrowIfCancellationRequested();
        // Disconnect/configuration changes during the search cannot release cached private evidence.
        Dictionary<string, CodeRepository> current;
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(token);
        verification.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (await access.GetIdentityAsync(verification.Token) != connection) return new("not-connected", []);
            current = (await store.RepositoriesAsync(verification.Token)).ToDictionary(repository => repository.Id);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new("authorization-unavailable", [])
            {
                Coverage = new() { AuthorizationComplete = false, Warnings = ["Final access/configuration verification did not finish. Evidence was withheld."] }
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning("Final code access verification unavailable ({FailureType}); evidence withheld.", exception.GetType().Name);
            return new("authorization-unavailable", [])
            {
                Coverage = new() { AuthorizationComplete = false, Warnings = ["Final access/configuration verification failed. Evidence was withheld."] }
            };
        }
        bool StillScoped(CodeRepository repository) => current.TryGetValue(repository.Id, out var live)
            && live.Enabled && live.ProjectId == repository.ProjectId && live.GitHubId == repository.GitHubId
            && live.Branch == repository.Branch && live.FullName == repository.FullName;
        var valid = candidates.Where(candidate => StillScoped(candidate.Repository)
            && current[candidate.Repository.Id].Generation == candidate.Repository.Generation).ToArray();
        if (valid.Length < candidates.Count) warnings.TryAdd("Repository snapshots changed during search; obsolete candidates were excluded.", 0);
        var documents = CodeCandidateSelection.Select(query, valid, settings.MaxEvidenceSpans, settings.ContextCharacters);
        var available = authorized.Values.Where(StillScoped).ToArray();
        var incomplete = timedOut || failed > 0 || notReady > 0 || available.Any(repository => !searched.ContainsKey(repository.Id))
            || valid.Length < candidates.Count;
        if (available.Any(repository => !searched.ContainsKey(repository.Id)) && settings.RepositoryRoutingEnabled)
            warnings.TryAdd("Only shortlisted authorized repositories were searched; broader repositories remain eligible.", 0);
        var status = incomplete ? documents.Count > 0 ? "partial" : timedOut ? "timed-out" : notReady > 0 ? "not-ready" : "failed"
            : documents.Count > 0 ? stale > 0 ? "stale" : "ready"
            : available.Length == 0 ? "no-accessible-repositories" : "no-accessible-matches";
        if (semantic?.Enabled == true && semanticStates.Keys.Any(state => state is not ("disabled" or "ready" or "reused" or "not-used")))
            warnings.TryAdd("Semantic index coverage is limited; lexical and graph evidence remains available.", 0);
        if (candidates.Limited) warnings.TryAdd("The global candidate budget was reached; top-ranked evidence is shown, not every matching source span.", 0);
        token.ThrowIfCancellationRequested();
        var vectorCoverage = available.Where(repository => searched.ContainsKey(repository.Id) && semanticCoverage.ContainsKey(repository.Id))
            .Select(repository => semanticCoverage[repository.Id]).ToArray();
        return new(status, documents)
        {
            Coverage = new()
            {
                AuthorizedRepositories = available.Length,
                SearchedRepositories = available.Count(repository => searched.ContainsKey(repository.Id)),
                FailedRepositories = failed, AuthorizationComplete = authorizationComplete,
                DeadlineExceeded = timedOut, ExpandedFallback = expanded,
                SemanticStatus = semantic?.Enabled == true ? string.Join(", ", semanticStates.Keys.Order()) is { Length: > 0 } states ? states : "not-used" : "disabled",
                SemanticIndexedChunks = vectorCoverage.Sum(coverage => coverage.IndexedChunks),
                SemanticEligibleChunks = vectorCoverage.Sum(coverage => coverage.EligibleChunks),
                SemanticTargetChunks = vectorCoverage.Sum(coverage => coverage.TargetChunks),
                SemanticTargetReached = vectorCoverage.Length > 0 &&
                    vectorCoverage.Length == available.Count(repository => searched.ContainsKey(repository.Id)) && vectorCoverage.All(coverage => coverage.TargetReached),
                KnowledgeStatus = knowledgeStates.Count == 0 ? "not-available" : string.Join(", ", knowledgeStates.Keys.Order()),
                OldestIndexedAt = available.Where(repository => repository.LastIndexedAt is not null).Select(repository => repository.LastIndexedAt).Min(),
                Warnings = warnings.Keys.Order().ToArray()
            }
        };
    }

    private static string QueryCacheScope(ClaimsPrincipal? principal, string user) =>
        string.Join('\0', user,
            principal?.FindFirst("tid")?.Value
                ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value ?? "",
            principal?.FindFirst("iss")?.Value ?? "");
}
