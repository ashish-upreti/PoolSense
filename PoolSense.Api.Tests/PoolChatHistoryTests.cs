using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PoolSense.Api.Controllers;
using PoolSense.Api.Models;
using PoolSense.Api.Orchestration;
using PoolSense.Api.Services;
using PoolSense.Application.Models;
using System.Security.Claims;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class PoolChatHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Process_OwnedRestoredSession_PropagatesContext_AndStoresOriginalQuestion(bool streaming)
    {
        var store = new StubPoolChatHistoryStore();
        var sessionId = await store.AppendExchangeAsync("alice", null, "Pump stops", ["g1"],
            new TicketWorkflowResult { SuggestedRootCause = "Possible mapping", SuggestedResolution = "1. Inspect.\n2. Compare.\n3. Validate VG-123." }, CancellationToken.None);
        var orchestrator = new StubOrchestrator();
        var controller = CreateResolutionController(store, "alice", orchestrator);
        const string question = "  Explain step 3, please  ";
        var request = new TicketRequest { Title = question, Description = question, ChatSessionId = sessionId, SelectedGroupIds = ["g1"] };
        if (streaming)
        {
            Assert.IsType<EmptyResult>(await controller.PostWithProgress(request, CancellationToken.None));
            controller.Response.Body.Position = 0;
            var stream = await new StreamReader(controller.Response.Body).ReadToEndAsync();
            Assert.Contains("\"type\":\"result\"", stream);
            Assert.Contains(sessionId, stream);
        }
        else
        {
            Assert.IsType<OkObjectResult>((await controller.Post(request, CancellationToken.None)).Result);
        }
        Assert.Equal(question, orchestrator.LastQuestion);
        Assert.Equal(2, orchestrator.LastContext!.Turns.Count);
        Assert.Equal(6, store.LatestReadLimit);
        Assert.Contains("VG-123", orchestrator.LastContext.Turns[1].Content);
        var messages = Assert.Single(store.Users["alice"]).Messages;
        Assert.Equal(4, messages.Count);
        Assert.Equal(question, messages[2].Content);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("foreign", false)]
    [InlineData("scope-change", false)]
    [InlineData("foreign", true)]
    [InlineData("scope-change", true)]
    public async Task Process_UnownedOrDifferentScopeSession_StartsIsolatedChat(string scenario, bool streaming)
    {
        var store = new StubPoolChatHistoryStore();
        var oldId = await store.AppendExchangeAsync(scenario == "foreign" ? "bob" : "alice", null, "SECRET_OTHER_CONTEXT", ["old"],
            new TicketWorkflowResult { SuggestedResolution = "SECRET_ANSWER" }, CancellationToken.None);
        var orchestrator = new StubOrchestrator();
        var controller = CreateResolutionController(store, "alice", orchestrator);
        var request = new TicketRequest
        {
            Title = "Current question", Description = "Current details",
            ChatSessionId = scenario == "unknown" ? "missing" : oldId,
            SelectedGroupIds = scenario == "scope-change" ? ["new"] : ["old"]
        };
        if (streaming) await controller.PostWithProgress(request, CancellationToken.None);
        else await controller.Post(request, CancellationToken.None);
        Assert.False(orchestrator.LastContext!.HasTurns);
        Assert.Null(request.ChatSessionId);
        Assert.Contains(store.Users["alice"], session => session.Id != oldId && session.Title == "Current question");
        Assert.Equal(2, store.Users[scenario == "foreign" ? "bob" : "alice"].Single(session => session.Id == oldId).Messages.Count);
    }

    [Fact]
    public async Task Process_InitialRequest_DoesNotLoadAnyContext()
    {
        var orchestrator = new StubOrchestrator();
        await CreateResolutionController(new StubPoolChatHistoryStore { ThrowOnRead = new InvalidOperationException("Should not read history") }, "alice", orchestrator).Post(
            new TicketRequest { Title = "First", Description = "New issue" }, CancellationToken.None);
        Assert.False(orchestrator.LastContext!.HasTurns);
        Assert.Equal(1, orchestrator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Process_ContinuationReadFailure_FailsExplicitlyWithoutRunningUncontextualizedAnswer(bool streaming)
    {
        var store = new StubPoolChatHistoryStore { ThrowOnRead = new InvalidOperationException("History unavailable") };
        var orchestrator = new StubOrchestrator();
        var controller = CreateResolutionController(store, "alice", orchestrator);
        var request = new TicketRequest { Title = "Explain step 3", Description = "Explain step 3", ChatSessionId = "owned" };
        var response = streaming
            ? await controller.PostWithProgress(request, CancellationToken.None)
            : (await controller.Post(request, CancellationToken.None)).Result;
        Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Null(controller.Response.ContentType);
        Assert.Equal(0, orchestrator.Calls);
        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task Process_StreamingSaveFailure_RemainsNonfatalAndVisible()
    {
        var store = new StubPoolChatHistoryStore { ThrowOnAppend = new InvalidOperationException("History down") };
        var controller = CreateResolutionController(store, "alice");
        await controller.PostWithProgress(new TicketRequest { Title = "Current question", Description = "Current details" }, CancellationToken.None);
        controller.Response.Body.Position = 0;
        var stream = await new StreamReader(controller.Response.Body).ReadToEndAsync();
        Assert.Contains("\"type\":\"result\"", stream);
        Assert.Contains("historyPersistenceError", stream);
        Assert.Contains("could not be saved", stream);
        Assert.Contains("Stub resolution", stream);
    }

    [Fact]
    public async Task Process_Post_PersistsHistoryAndReturnsChatSessionId()
    {
        var store = new StubPoolChatHistoryStore();
        var controller = CreateResolutionController(store, "alice");

        var request = new TicketRequest { Title = "Pump fault", Description = "Pump stops", SelectedGroupIds = ["g1"] };
        var response = await controller.Post(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<TicketWorkflowResult>(ok.Value);
        var session = Assert.Single(store.Users["alice"]);
        Assert.Equal(session.Id, result.ChatSessionId);
        Assert.Equal(string.Empty, result.HistoryPersistenceError);
        Assert.Equal("Pump fault", session.Title);
        Assert.Equal(["g1"], session.GroupIds);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("user", session.Messages[0].Role);
        Assert.Equal("Pump fault", session.Messages[0].Content);
        Assert.Equal("assistant", session.Messages[1].Role);
        Assert.NotNull(session.Messages[1].Result);
        Assert.Equal(StubOrchestrator.DetailedMarkdown, session.Messages[1].Result!.DetailedAnalysisMarkdown);
    }

    [Fact]
    public async Task Process_Post_ContinuesOwnedSessionAndAppends()
    {
        var store = new StubPoolChatHistoryStore();
        var controller = CreateResolutionController(store, "alice");
        var sessionId = await store.AppendExchangeAsync("alice", null, "First question", null, new TicketWorkflowResult(), CancellationToken.None);

        var request = new TicketRequest { Title = "Follow up", Description = "Follow up detail", ChatSessionId = sessionId };
        var response = await controller.Post(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<TicketWorkflowResult>(ok.Value);
        Assert.Equal(sessionId, result.ChatSessionId);
        var session = Assert.Single(store.Users["alice"]);
        Assert.Equal(sessionId, session.Id);
        Assert.Equal(4, session.Messages.Count);
    }

    [Fact]
    public async Task Process_Post_ForeignChatSessionIdIsIgnoredAndNewSessionCreated()
    {
        var store = new StubPoolChatHistoryStore();
        var controller = CreateResolutionController(store, "alice");
        var bobSessionId = await store.AppendExchangeAsync("bob", null, "Bob question", null, new TicketWorkflowResult(), CancellationToken.None);

        var request = new TicketRequest { Title = "Alice question", Description = "Alice detail", ChatSessionId = bobSessionId };
        var response = await controller.Post(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<TicketWorkflowResult>(ok.Value);
        Assert.NotEqual(bobSessionId, result.ChatSessionId);
        Assert.Equal("Alice question", Assert.Single(store.Users["alice"]).Title);
        Assert.Single(store.Users["bob"]);
    }

    [Fact]
    public async Task Process_Post_PersistenceFailureIsNonFatal()
    {
        var store = new StubPoolChatHistoryStore { ThrowOnAppend = new InvalidOperationException("db down") };
        var controller = CreateResolutionController(store, "alice");

        var response = await controller.Post(new TicketRequest { Title = "Pump fault", Description = "Pump detail" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<TicketWorkflowResult>(ok.Value);
        Assert.Equal("Stub resolution", result.SuggestedResolution);
        Assert.NotEmpty(result.HistoryPersistenceError);
        Assert.Equal(string.Empty, result.ChatSessionId);
        Assert.False(store.Users.ContainsKey("alice"));
    }

    [Fact]
    public async Task Process_Post_UnauthenticatedRequestSkipsHistory()
    {
        var store = new StubPoolChatHistoryStore();
        var controller = CreateResolutionController(store, null);

        await controller.Post(new TicketRequest { Title = "Anonymous", Description = "Anonymous detail" }, CancellationToken.None);

        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task History_Get_ReturnsOnlyOwnedSession()
    {
        var store = new StubPoolChatHistoryStore();
        var sessionId = await store.AppendExchangeAsync("alice", null, "Alice question", ["g1"], new TicketWorkflowResult { SuggestedResolution = "Alice answer" }, CancellationToken.None);

        var aliceController = CreateHistoryController(store, "alice");
        var aliceResponse = await aliceController.Get(sessionId, CancellationToken.None);
        var aliceSession = Assert.IsType<PoolChatSession>(Assert.IsType<OkObjectResult>(aliceResponse).Value);
        Assert.Equal("Alice question", aliceSession.Session.Title);
        Assert.Equal(["g1"], aliceSession.Session.SelectedGroupIds);
        Assert.Equal("Alice answer", aliceSession.Messages[1].Content);

        var bobController = CreateHistoryController(store, "bob");
        Assert.IsType<NotFoundResult>(await bobController.Get(sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task History_List_ScopesByUserAndSortsPinnedFirst()
    {
        var store = new StubPoolChatHistoryStore();
        var first = await store.AppendExchangeAsync("alice", null, "Question one", null, new TicketWorkflowResult(), CancellationToken.None);
        await store.AppendExchangeAsync("alice", null, "Question two", null, new TicketWorkflowResult(), CancellationToken.None);
        await store.AppendExchangeAsync("bob", null, "Bob question", null, new TicketWorkflowResult(), CancellationToken.None);
        await store.UpdateSessionAsync("alice", first, null, true, CancellationToken.None);

        var response = await CreateHistoryController(store, "alice").List(CancellationToken.None);

        var sessions = Assert.IsType<List<PoolChatSessionSummary>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(first, sessions[0].Id);
        Assert.True(sessions[0].IsPinned);
    }

    [Fact]
    public async Task History_Patch_ValidatesTitleAndOwnership()
    {
        var store = new StubPoolChatHistoryStore();
        var sessionId = await store.AppendExchangeAsync("alice", null, "Alice question", null, new TicketWorkflowResult(), CancellationToken.None);
        var controller = CreateHistoryController(store, "alice");

        Assert.IsType<BadRequestObjectResult>(await controller.Patch(sessionId, new PoolChatSessionPatchRequest { Title = "   " }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Patch(sessionId, new PoolChatSessionPatchRequest { Title = new string('x', 101) }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Patch(sessionId, new PoolChatSessionPatchRequest(), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.Patch(sessionId, new PoolChatSessionPatchRequest { Title = "Renamed", IsPinned = true }, CancellationToken.None));
        Assert.True((await store.SessionsAsync("alice", CancellationToken.None)).Single() is { IsPinned: true } summary && summary.Title == "Renamed");

        var bobController = CreateHistoryController(store, "bob");
        Assert.IsType<NotFoundResult>(await bobController.Patch(sessionId, new PoolChatSessionPatchRequest { Title = "Hijack" }, CancellationToken.None));
    }

    [Fact]
    public async Task History_Delete_RequiresOwnership()
    {
        var store = new StubPoolChatHistoryStore();
        var sessionId = await store.AppendExchangeAsync("alice", null, "Alice question", null, new TicketWorkflowResult(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(await CreateHistoryController(store, "bob").Delete(sessionId, CancellationToken.None));
        Assert.Single(await store.SessionsAsync("alice", CancellationToken.None));

        Assert.IsType<NoContentResult>(await CreateHistoryController(store, "alice").Delete(sessionId, CancellationToken.None));
        Assert.Empty(await store.SessionsAsync("alice", CancellationToken.None));
    }

    private static ResolutionController CreateResolutionController(IPoolChatHistoryStore store, string? username, StubOrchestrator? orchestrator = null) =>
        new(orchestrator ?? new StubOrchestrator(), store, NullLogger<ResolutionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(username) },
        };

    private static PoolChatHistoryController CreateHistoryController(IPoolChatHistoryStore store, string? username) =>
        new(store)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(username) },
        };

    private static DefaultHttpContext CreateHttpContext(string? username)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (username is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, username)], "TestAuth"));
        }

        return context;
    }

    private sealed class StubOrchestrator : ITicketWorkflowOrchestrator
    {
        public TicketConversationContext? LastContext { get; private set; }
        public string? LastQuestion { get; private set; }
        public int Calls { get; private set; }
        public const string DetailedMarkdown = "## Assessment\nStub analysis";

        public Task<TicketWorkflowResult> ProcessAsync(string title, string description, string? ticketId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TicketWorkflowResult> ProcessAsync(TicketRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult());

        public Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult());

        public async Task<TicketWorkflowResult> RecommendAsync(TicketRequest request, TicketConversationContext context, Func<TicketWorkflowProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            LastQuestion = request.GetWorkflowTitle();
            if (progressCallback is not null) await progressCallback(new TicketWorkflowProgress { Stage = "test", State = "completed" }, cancellationToken);
            return CreateResult();
        }

        private static TicketWorkflowResult CreateResult() => new()
        {
            SuggestedRootCause = "Stub root cause",
            SuggestedResolution = "Stub resolution",
            DetailedAnalysisMarkdown = DetailedMarkdown,
            Confidence = 0.75,
        };
    }

    /// <summary>In-memory per-user stand-in for SQL ownership predicates.</summary>
    internal sealed class StubPoolChatHistoryStore : IPoolChatHistoryStore
    {
        public Dictionary<string, List<StoredSession>> Users { get; } = new(StringComparer.Ordinal);
        public Exception? ThrowOnAppend { get; init; }
        public Exception? ThrowOnRead { get; init; }
        public int? LatestReadLimit { get; private set; }
        private static int _nextId;

        public sealed class StoredSession
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public List<string> GroupIds { get; set; } = [];
            public bool IsPinned { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public List<PoolChatStoredMessage> Messages { get; set; } = [];
        }

        public Task EnsureSchemaAsync(CancellationToken token) => Task.CompletedTask;

        public Task<IReadOnlyList<PoolChatSessionSummary>> SessionsAsync(string user, CancellationToken token)
        {
            IReadOnlyList<PoolChatSessionSummary> sessions = Users.TryGetValue(user, out var list)
                ? list.OrderByDescending(session => session.IsPinned).ThenByDescending(session => session.UpdatedAt)
                    .Select(ToSummary).ToList()
                : [];
            return Task.FromResult(sessions);
        }

        public Task<PoolChatSession?> SessionAsync(string user, string sessionId, CancellationToken token, int? latestMessageLimit = null)
        {
            LatestReadLimit = latestMessageLimit;
            if (ThrowOnRead is not null) throw ThrowOnRead;
            StoredSession? session = null;
            if (Users.TryGetValue(user, out var list))
            {
                session = list.FirstOrDefault(candidate => candidate.Id == sessionId);
            }

            if (session is null)
            {
                return Task.FromResult<PoolChatSession?>(null);
            }

            IReadOnlyList<PoolChatStoredMessage> messages = latestMessageLimit is { } limit ? session.Messages.TakeLast(limit).ToArray() : session.Messages;
            return Task.FromResult<PoolChatSession?>(new(ToSummary(session), messages));
        }

        public Task<string> AppendExchangeAsync(string user, string? sessionId, string question, IReadOnlyList<string>? selectedGroupIds,
            TicketWorkflowResult result, CancellationToken token)
        {
            if (ThrowOnAppend is not null)
            {
                throw ThrowOnAppend;
            }

            if (!Users.TryGetValue(user, out var list))
            {
                list = [];
                Users[user] = list;
            }

            var now = DateTimeOffset.UtcNow;
            var owned = sessionId;
            if (owned is not null)
            {
                owned = list.FirstOrDefault(candidate => candidate.Id == owned)?.Id;
            }

            if (owned is null)
            {
                owned = "s" + Interlocked.Increment(ref _nextId);
                list.Add(new StoredSession
                {
                    Id = owned,
                    Title = question.Length <= 80 ? question : question[..77] + "...",
                    GroupIds = selectedGroupIds?.ToList() ?? [],
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                list.First(candidate => candidate.Id == owned).UpdatedAt = now;
            }

            var session = list.First(candidate => candidate.Id == owned);
            session.Messages.Add(new("user", question, null, now));
            session.Messages.Add(new("assistant", result.SuggestedResolution, result, now));
            return Task.FromResult(owned);
        }

        public Task<bool> UpdateSessionAsync(string user, string sessionId, string? title, bool? isPinned, CancellationToken token)
        {
            var session = Users.TryGetValue(user, out var list) ? list.FirstOrDefault(candidate => candidate.Id == sessionId) : null;
            if (session is null)
            {
                return Task.FromResult(false);
            }

            if (title is not null)
            {
                session.Title = title.Length > 100 ? title[..100] : title;
            }

            if (isPinned is not null)
            {
                session.IsPinned = isPinned.Value;
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token)
        {
            var removed = false;
            if (Users.TryGetValue(user, out var list))
            {
                var session = list.FirstOrDefault(candidate => candidate.Id == sessionId);
                if (session is not null)
                {
                    list.Remove(session);
                    removed = true;
                }
            }

            return Task.FromResult(removed);
        }

        private static PoolChatSessionSummary ToSummary(StoredSession session) =>
            new(session.Id, session.Title, session.GroupIds, session.IsPinned, session.CreatedAt, session.UpdatedAt, session.Messages.Count);
    }
}
