using System.IO.Compression;
using System.Security.Claims;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PoolSense.Api.Services.CodeRAG;
using PoolSense.Application.Models;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class CodeRAGServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "poolsense-coderag", Guid.NewGuid().ToString("N"));
    private readonly MemoryStore store = new();
    private readonly FakeGitHub github = new();
    private readonly FakeManagedGitHubAccess access = new();
    private readonly IOptions<CodeRAGOptions> options;
    private readonly CodeIndexFiles files;
    private readonly IHttpContextAccessor context = new HttpContextAccessor();

    public CodeRAGServiceTests()
    {
        options = Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions { Enabled = true, StoragePath = directory });
        files = new CodeIndexFiles(new TestEnvironment(directory), options);
        context.HttpContext = new DefaultHttpContext { User = Principal("alice") };
        store.Repository = new() { ProjectId = "project", FullName = "owner/repo", GitHubId = 123, Branch = "main" };
    }

    [Fact]
    public async Task Refresh_UnchangedCommitSkipsBuilder_ChangedCommitReplacesEvidence()
    {
        var builder = new TestBuilder(files);
        var coordinator = Coordinator(builder);
        await coordinator.RefreshAsync(store.Repository!, default);
        var firstGeneration = store.Repository!.Generation;
        var indexedAt = store.Repository.LastIndexedAt;
        await coordinator.RefreshAsync(store.Repository, default);
        Assert.Equal(1, builder.Builds);
        Assert.Equal(firstGeneration, store.Repository.Generation);
        Assert.Equal(indexedAt, store.Repository.LastIndexedAt);
        Assert.Equal("ready", store.Repository.Status);
        Assert.NotNull(store.Repository.LastCheckedAt);

        github.Commit = new string('b', 40);
        builder.Source = "class UpdatedPumpController { void OperateValve() {} }";
        await coordinator.RefreshAsync(store.Repository, default);
        Assert.Equal(2, builder.Builds);
        Assert.NotEqual(firstGeneration, store.Repository.Generation);
        Assert.Equal(github.Commit, store.Repository.IndexedCommit);
        var knowledge = Knowledge();
        Assert.Empty((await knowledge.RetrieveAsync("OriginalPumpController", ["project"], default)).Documents);
        var result = await knowledge.RetrieveAsync("OperateValve", ["project"], default);
        Assert.NotEmpty(result.Documents);
        Assert.All(result.Documents, document => Assert.Contains($"/blob/{github.Commit}/", document.SourceUrl));
    }

    [Fact]
    public async Task PublishedSnapshotEnqueuesOneCoalescedWarmupRequest()
    {
        var builder = new TestBuilder(files);
        var queue = new CodeSnapshotWarmupQueue(Microsoft.Extensions.Options.Options.Create(new RetrievalCacheWarmupOptions()));
        var coordinator = new CodeIndexCoordinator(store, github, access, builder, files, options, TimeProvider.System,
            warmupQueue: queue);

        await coordinator.RefreshAsync(store.Repository!, default);
        var published = store.Repository!;
        var request = await queue.ReadAsync(CancellationToken.None);
        Assert.Equal(published.Id, request.RepositoryId);
        Assert.Equal(published.Generation, request.Generation);

        await coordinator.RefreshAsync(published, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task BackgroundPollingUsesManagedAccessWithoutHttpUser_WhileInteractiveRetrievalStillRequiresSignIn()
    {
        await Coordinator(new TestBuilder(files)).RefreshAsync(store.Repository!, default);
        context.HttpContext!.User = new ClaimsPrincipal();
        var service = Knowledge();
        var polling = await service.ForBackgroundPollingAsync("OriginalPumpController",
            new TicketRequest { ApplicationId = "project" }, default);
        Assert.NotEmpty(polling.Documents);
        Assert.All(polling.Documents, document => Assert.Equal("project", document.ProjectId));
        Assert.Equal("not-authenticated", (await service.ForRequestAsync("OriginalPumpController",
            new TicketRequest { ApplicationId = "project" }, default)).Status);
        Assert.Empty((await service.ForBackgroundPollingAsync("OriginalPumpController",
            new TicketRequest { ApplicationId = "other-project" }, default)).Documents);
        access.IsConfiguredValue = false;
        Assert.Equal("not-configured", (await service.ForBackgroundPollingAsync("OriginalPumpController",
            new TicketRequest { ApplicationId = "project" }, default)).Status);
    }

    [Fact]
    public async Task RevokedManagedAccessCannotUseCachedCodeEvidence()
    {
        await Coordinator(new TestBuilder(files)).RefreshAsync(store.Repository!, default);
        access.ThrowAccessException = true;
        context.HttpContext!.User = Principal("alice");
        var result = await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default);
        Assert.Empty(result.Documents);
        Assert.Equal("not-connected", result.Status);
    }

    [Fact]
    public async Task ManagedAccessUsesOnlyConfiguredPat_AndRefreshesIdentityWhenRotated()
    {
        var noPatOptions = new MutableOptionsMonitor(new CodeRAGOptions());
        var requests = 0;
        var protection = new EphemeralDataProtectionProvider();
        var factory = new TestHttpClientFactory(new TestHttpHandler(request =>
        {
            requests++;
            var token = request.Headers.Authorization?.Parameter;
            var id = token == "managed-pat-b" ? 2 : 1;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":{{id}},"login":"managed"}""")
            });
        }));
        var missing = new SharedGitHubConnection(factory, protection, noPatOptions);
        Assert.False(missing.IsConfigured);
        var missingError = await Assert.ThrowsAsync<CodeAccessException>(() => missing.GetIdentityAsync(default));
        Assert.Contains("not configured", missingError.Message);
        Assert.Equal(0, requests);

        noPatOptions.CurrentValue.GitHubToken = "managed-pat-a";
        Assert.True(missing.IsConfigured);
        var first = await missing.GetIdentityAsync(default);
        Assert.Equal("managed", first.Login);
        Assert.Equal("managed-pat-a", protection.CreateProtector(GitHubCodeClient.TokenPurpose).Unprotect(first.ProtectedToken));
        Assert.Equal(first, await missing.GetIdentityAsync(default));
        Assert.Equal(1, requests);

        noPatOptions.CurrentValue.GitHubToken = "managed-pat-b";
        var rotated = await missing.GetIdentityAsync(default);
        Assert.Equal(2, rotated.GitHubId);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ManagedAccessStatusIsReadOnlyAndNeverReturnsThePat()
    {
        access.IsConfiguredValue = false;
        var controller = new PoolSense.Api.Controllers.GitHubAccessController(access);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Status(default));
        var configured = result.Value!.GetType().GetProperty("configured")!.GetValue(result.Value);
        var available = result.Value.GetType().GetProperty("available")!.GetValue(result.Value);
        var message = result.Value.GetType().GetProperty("message")!.GetValue(result.Value)?.ToString();
        Assert.Equal(false, configured);
        Assert.Equal(false, available);
        Assert.Contains("administrator", message);
        Assert.DoesNotContain("opaque-managed-token", message);
    }

    [Fact]
    public async Task RefreshFailure_RetainsPreviousCommitAndSnapshot()
    {
        var builder = new TestBuilder(files);
        await Coordinator(builder).RefreshAsync(store.Repository!, default);
        var generation = store.Repository!.Generation;
        var commit = store.Repository.IndexedCommit;
        github.Commit = new string('c', 40);
        builder.Fail = true;
        await Coordinator(builder).RefreshAsync(store.Repository, default);
        Assert.Equal("error", store.Repository.Status);
        Assert.Equal(generation, store.Repository.Generation);
        Assert.Equal(commit, store.Repository.IndexedCommit);
        Assert.True(files.Ready(store.Repository));
    }

    [Fact]
    public async Task Retrieval_AfterProgressResponseStarts_DoesNotModifyHeaders()
    {
        await Coordinator(new TestBuilder(files)).RefreshAsync(store.Repository!, default);
        context.HttpContext!.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var result = await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default);
        Assert.NotEmpty(result.Documents);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task RepositoryChangedDuringBuild_CannotPublishObsoleteSnapshot()
    {
        var builder = new TestBuilder(files) { OnBuild = () => store.Repository = store.Repository! with { Revision = 99, Enabled = false, Status = "disabled" } };
        await Coordinator(builder).RefreshAsync(store.Repository!, default);
        Assert.Equal(99, store.Repository!.Revision);
        Assert.Equal("", store.Repository.Generation);
        Assert.False(store.Repository.Enabled);
    }

    [Fact]
    public void FeedbackPermit_BindsAnswerAndUser_RejectsMissingOrTamperedPermit()
    {
        var policy = new CodeFeedbackPolicy(new EphemeralDataProtectionProvider(), context, options);
        var permit = policy.Permit("Wiki answer");
        Assert.True(policy.Allows("Wiki answer", permit));
        Assert.False(policy.Allows("Private code answer", permit));
        Assert.False(policy.Allows("Private code answer", ""));
        context.HttpContext!.User = Principal("bob");
        Assert.False(policy.Allows("Wiki answer", permit));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com.evil.test/owner/repo")]
    [InlineData("https://token@github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("file:///tmp/repo")]
    public void GitHubUrl_RejectsUntrustedInputs(string url) => Assert.Throws<ArgumentException>(() => GitHubCodeClient.ParseUrl(url));

    [Theory]
    [InlineData(401, "", "server-managed PAT")]
    [InlineData(403, "", "server-managed PAT's repository permissions")]
    [InlineData(403, "sso", "server-managed PAT")]
    [InlineData(403, "rate-limit", "rate limit")]
    [InlineData(404, "", "could not find or access this repository")]
    [InlineData(404, "branch", "selected branch")]
    [InlineData(429, "", "rate limit")]
    [InlineData(500, "", "Try again later")]
    public async Task GitHubAccessFailure_ReportsActionableGuidanceThroughApi(int status, string scenario, string expected)
    {
        var protection = new EphemeralDataProtectionProvider();
        var clientAccess = new FakeManagedGitHubAccess
        {
            Identity = new(1, "managed",
                protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect("test-access-token"))
        };
        using var http = new HttpClient(new TestHttpHandler(request =>
        {
            if (scenario == "branch" && request.RequestUri!.AbsolutePath == "/repos/owner/repo")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":123,"full_name":"owner/repo","default_branch":"main"}""")
                });
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent("upstream-sensitive-body")
            };
            if (scenario == "sso") response.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/orgs/example/sso?secret=value");
            if (scenario == "rate-limit") response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        }));
        var client = new GitHubCodeClient(http, clientAccess, protection, options);

        var exception = await Assert.ThrowsAsync<CodeAccessException>(async () =>
        {
            if (scenario == "branch") await client.CommitAsync(store.Repository!, default);
            else await client.RepositoryAsync("owner/repo", default);
        });

        Assert.Contains(expected, exception.Message);
        Assert.Equal(scenario == "rate-limit", exception.RetryAt is not null);
        context.HttpContext!.Request.Method = "POST";
        context.HttpContext.Request.Headers["X-PoolSense-CSRF"] = "1";
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(context.HttpContext!, new(), new());
        var filters = new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>();
        var executing = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(action, filters, new Dictionary<string, object?>(), new object());
        var executed = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(action, filters, new object()) { Exception = exception };
        await new PoolSense.Api.Controllers.CodeApiFilter(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<PoolSense.Api.Controllers.CodeApiFilter>.Instance).OnActionExecutionAsync(executing, () => Task.FromResult(executed));
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(executed.Result);
        var problem = Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(result.Value);
        Assert.True(executed.ExceptionHandled);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(exception.Message, problem.Title);
        Assert.DoesNotContain("test-access-token", problem.Title);
        Assert.DoesNotContain("upstream-sensitive-body", problem.Title);
        Assert.DoesNotContain("secret=value", problem.Title);
    }

    [Fact]
    public async Task Archive_RejectsTraversal()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "archive.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("repo/../../outside.cs").Open()))
            writer.Write("class Outside {}");
        await Assert.ThrowsAsync<IOException>(() => GitHubCodeClient.ExtractAsync(path, Path.Combine(directory, "source"), 10000, default));
    }

    private CodeIndexCoordinator Coordinator(TestBuilder builder) => new(store, github, access, builder, files, options, TimeProvider.System);
    private CodeKnowledgeService Knowledge() => new(store, null!, github, access, files, context, options);
    private static ClaimsPrincipal Principal(string user) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class TestBuilder(CodeIndexFiles files) : ICodeSnapshotBuilder
    {
        public int Builds { get; private set; }
        public bool Fail { get; set; }
        public Action? OnBuild { get; set; }
        public string Source { get; set; } = "class OriginalPumpController { void StartPump() {} }";
        public async Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token, string? previousSnapshotFolder = null)
        {
            Builds++;
            if (Fail) throw new IOException("Test failure");
            Directory.CreateDirectory(files.Source(repository));
            await File.WriteAllTextAsync(Path.Combine(files.Source(repository), "Pump.cs"), Source, token);
            var result = await new RepositoryIndexer().IndexAsync(files.Source(repository), files.Database(repository), true, cancellationToken: token);
            OnBuild?.Invoke();
            return result.Summary;
        }
    }

    private sealed class FakeGitHub : IGitHubCodeClient
    {
        public string Commit { get; set; } = new('a', 40);
        public bool AllowAccess { get; set; } = true;
        public Task<string> CommitAsync(CodeRepository repository, CancellationToken token)
        {
            return AllowAccess ? Task.FromResult(Commit) : Task.FromException<string>(new CodeAccessException());
        }
        public Task<GitHubRepository> RepositoryAsync(string fullName, CancellationToken token)
        {
            return AllowAccess
                ? Task.FromResult(new GitHubRepository(123, fullName, "main"))
                : Task.FromException<GitHubRepository>(new CodeAccessException());
        }
        public Task<GitHubBranches> BranchesAsync(string fullName, CancellationToken token) => Task.FromResult(new GitHubBranches(fullName, "main", ["main"], false));
        public Task DownloadAsync(CodeRepository repository, string commit, string destination, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class MemoryStore : ICodeRepositoryStore
    {
        public CodeRepository? Repository { get; set; }
        public Task<IReadOnlyList<CodeRepository>> RepositoriesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CodeRepository>>(Repository is null ? [] : [Repository]);
        public Task AddAsync(CodeRepository repository, CancellationToken token) { Repository = repository; return Task.CompletedTask; }
        public Task<bool> UpdateAsync(CodeRepository repository, int expectedRevision, CancellationToken token)
        {
            if (Repository?.Revision != expectedRevision) return Task.FromResult(false);
            Repository = repository;
            return Task.FromResult(true);
        }
        public Task<bool> DeleteAsync(string id, int revision, CancellationToken token) { Repository = null; return Task.FromResult(true); }
    }

    private sealed class FakeManagedGitHubAccess : IManagedGitHubAccess
    {
        public bool IsConfiguredValue { get; set; } = true;
        public bool ThrowAccessException { get; set; }
        public ManagedGitHubIdentity Identity { get; set; } = new(1, "managed", "opaque-managed-token");
        public bool IsConfigured => IsConfiguredValue;
        public Task<ManagedGitHubIdentity> GetIdentityAsync(CancellationToken token) =>
            !IsConfiguredValue || ThrowAccessException
                ? Task.FromException<ManagedGitHubIdentity>(new CodeAccessException("Managed GitHub access is unavailable."))
                : Task.FromResult(Identity);
    }

    private sealed class TestHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MutableOptionsMonitor(CodeRAGOptions value) : IOptionsMonitor<CodeRAGOptions>
    {
        public CodeRAGOptions CurrentValue { get; set; } = value;
        public CodeRAGOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CodeRAGOptions, string?> listener) => null;
    }

    private sealed class TestEnvironment(string directory) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = directory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}