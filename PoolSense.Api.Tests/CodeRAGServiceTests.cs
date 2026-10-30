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
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class CodeRAGServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "poolsense-coderag", Guid.NewGuid().ToString("N"));
    private readonly MemoryStore store = new();
    private readonly FakeGitHub github = new();
    private readonly IOptions<CodeRAGOptions> options;
    private readonly CodeIndexFiles files;
    private readonly IHttpContextAccessor context = new HttpContextAccessor();

    public CodeRAGServiceTests()
    {
        options = Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions { Enabled = true, StoragePath = directory });
        files = new CodeIndexFiles(new TestEnvironment(directory), options);
        store.Connections["alice"] = new("alice", 1, "alice-gh", "protected-test-value");
        context.HttpContext = new DefaultHttpContext { User = Principal("alice") };
        store.Repository = new() { ProjectId = "project", FullName = "owner/repo", GitHubId = 123, Branch = "main", IndexingUser = "alice" };
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
    public async Task Retrieval_ChecksQueryingUserAndProject_EvenWhenIndexIsCached()
    {
        await Coordinator(new TestBuilder(files)).RefreshAsync(store.Repository!, default);
        Assert.NotEmpty((await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default)).Documents);
        Assert.Empty((await Knowledge().RetrieveAsync("OriginalPumpController", ["other-project"], default)).Documents);
        store.Connections["bob"] = new("bob", 2, "bob-gh", "protected-other-value");
        context.HttpContext!.User = Principal("bob");
        Assert.Empty((await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default)).Documents);
        Assert.Contains("bob", github.CheckedUsers);
        context.HttpContext.User = Principal("alice");
        github.AllowAlice = false;
        Assert.Empty((await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default)).Documents);
        context.HttpContext.User = new ClaimsPrincipal();
        Assert.Empty((await Knowledge().RetrieveAsync("OriginalPumpController", ["project"], default)).Documents);
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

    [Fact]
    public void OAuthBegin_RequiresAccountSelectionAndRepositoryConsent()
    {
        options.Value.GitHubClientId = "test-client";
        options.Value.GitHubClientSecret = "test-secret";
        options.Value.CallbackUrl = "http://localhost:4200/api/github/callback";
        using var http = new HttpClient();
        var states = new GitHubOAuthStates();
        var oauth = new GitHubOAuthService(http, store, states, new EphemeralDataProtectionProvider(), options);

        var authorization = oauth.Begin("alice", "session");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(authorization.Url).Query);

        Assert.Equal("select_account", query["prompt"].ToString());
        Assert.Equal("repo", query["scope"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal(authorization.State, query["state"].ToString());
        Assert.False(query.ContainsKey("login"));
        Assert.DoesNotContain("test-secret", authorization.Url);
        Assert.Equal("alice", states.Consume(authorization.State)?.User);
    }

    [Fact]
    public void OAuthStates_AreSingleUseAndExpire()
    {
        var states = new GitHubOAuthStates();
        states.Add("state", new("alice", "session", "verifier", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.NotNull(states.Consume("state"));
        Assert.Null(states.Consume("state"));
        states.Add("expired", new("alice", "session", "verifier", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Null(states.Consume("expired"));
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    public async Task OAuthDisconnect_RevokesGrant_ClearsLocalConnectionAndPendingCallbacks(int status)
    {
        options.Value.GitHubClientId = "test-client";
        options.Value.GitHubClientSecret = "test-secret";
        var protection = new EphemeralDataProtectionProvider();
        store.Connections["alice"] = new("alice", 1, "alice-gh", protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect("test-access-token"));
        store.Connections["bob"] = new("bob", 2, "bob-gh", "other-protected-token");
        var states = new GitHubOAuthStates();
        states.Add("old-state", new("alice", "session", "verifier", DateTimeOffset.UtcNow.AddMinutes(1)));
        states.Add("other-state", new("bob", "other-session", "other-verifier", DateTimeOffset.UtcNow.AddMinutes(1)));
        var requests = 0;
        using var http = new HttpClient(new OAuthHandler(async request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("https://api.github.com/applications/test-client/grant", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test-client:test-secret")), request.Headers.Authorization?.Parameter);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("test-access-token", body.RootElement.GetProperty("access_token").GetString());
            Assert.True(store.Connections.ContainsKey("alice"));
            Assert.Null(states.Consume("old-state"));
            return new HttpResponseMessage((System.Net.HttpStatusCode)status);
        }));
        var oauth = new GitHubOAuthService(http, store, states, protection, options);

        await oauth.DisconnectAsync("alice", default);

        Assert.False(store.Connections.ContainsKey("alice"));
        Assert.True(store.Connections.ContainsKey("bob"));
        Assert.NotNull(states.Consume("other-state"));
        await Assert.ThrowsAsync<CodeAccessException>(() => oauth.CompleteAsync("alice", "session", "old-state", "code", default));
        await oauth.DisconnectAsync("alice", default);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task OAuthDisconnect_FailedRevocationDoesNotReportLocalSuccess(int status)
    {
        options.Value.GitHubClientId = "test-client";
        options.Value.GitHubClientSecret = "test-secret";
        var protection = new EphemeralDataProtectionProvider();
        store.Connections["alice"] = new("alice", 1, "alice-gh", protection.CreateProtector(GitHubCodeClient.TokenPurpose).Protect("test-access-token"));
        var states = new GitHubOAuthStates();
        states.Add("old-state", new("alice", "session", "verifier", DateTimeOffset.UtcNow.AddMinutes(1)));
        using var http = new HttpClient(new OAuthHandler(_ => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status))));
        var oauth = new GitHubOAuthService(http, store, states, protection, options);

        var error = await Assert.ThrowsAsync<GitHubDisconnectException>(() => oauth.DisconnectAsync("alice", default));

        Assert.True(store.Connections.ContainsKey("alice"));
        Assert.Null(states.Consume("old-state"));
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.DoesNotContain("test-access-token", error.Message);
    }

    private sealed class OAuthHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
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

    private CodeIndexCoordinator Coordinator(TestBuilder builder) => new(store, github, builder, files, options, TimeProvider.System);
    private CodeKnowledgeService Knowledge() => new(store, null!, github, files, context, options);
    private static ClaimsPrincipal Principal(string user) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class TestBuilder(CodeIndexFiles files) : ICodeSnapshotBuilder
    {
        public int Builds { get; private set; }
        public bool Fail { get; set; }
        public Action? OnBuild { get; set; }
        public string Source { get; set; } = "class OriginalPumpController { void StartPump() {} }";
        public async Task<IndexSummary> BuildAsync(CodeRepository repository, string commit, CancellationToken token)
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
        public bool AllowAlice { get; set; } = true;
        public List<string> CheckedUsers { get; } = [];
        public Task<string> CommitAsync(string user, CodeRepository repository, CancellationToken token)
        {
            CheckedUsers.Add(user);
            return user == "alice" && AllowAlice ? Task.FromResult(Commit) : Task.FromException<string>(new CodeAccessException());
        }
        public Task<GitHubRepository> RepositoryAsync(string user, string fullName, CancellationToken token) => Task.FromResult(new GitHubRepository(123, fullName, "main"));
        public Task DownloadAsync(string user, CodeRepository repository, string commit, string destination, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class MemoryStore : ICodeRepositoryStore
    {
        public CodeRepository? Repository { get; set; }
        public Dictionary<string, GitHubConnection> Connections { get; } = [];
        public Task<GitHubConnection?> ConnectionAsync(string user, CancellationToken token) => Task.FromResult(Connections.GetValueOrDefault(user));
        public Task SaveConnectionAsync(GitHubConnection connection, CancellationToken token) { Connections[connection.UserName] = connection; return Task.CompletedTask; }
        public Task DisconnectAsync(string user, CancellationToken token) { Connections.Remove(user); return Task.CompletedTask; }
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