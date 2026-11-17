using System.Text.Json;
using PoolSense.Api.Data;
using PoolSense.Api.Logging;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using PoolSense.Application.Models;
using Xunit;

namespace PoolSense.Api.Tests;

public class CodeChatScopeTests
{
    [Fact]
    public void Scope_NormalizesIds_AndMapsSingleProjectOntoLegacyShape()
    {
        var single = CodeChatScope.From([" app-a "]);
        Assert.Equal("app-a", single.ProjectId);
        Assert.Null(single.ScopeJson);
        Assert.False(single.IsAll);

        var multi = CodeChatScope.From(["Zeta", "alpha", "ALPHA", ""]);
        Assert.Null(multi.ProjectId);
        Assert.Equal(["alpha", "Zeta"], multi.ProjectIds);
        Assert.Equal("[\"alpha\",\"Zeta\"]", multi.ScopeJson);

        var all = CodeChatScope.From(null);
        Assert.True(all.IsAll);
        Assert.Equal("[]", all.ScopeJson);
        Assert.True(CodeChatScope.FromStored(null, "[]").IsAll);
        Assert.Equal("app-a", CodeChatScope.FromStored("app-a", null).ProjectId);
        Assert.Equal(["alpha", "Zeta"], CodeChatScope.FromStored(null, "[\"Zeta\",\"alpha\"]").ProjectIds);
    }

    [Fact]
    public async Task Ask_AllScope_RetrievesAcrossEveryApplication_AndNamesThemInPrompt()
    {
        var knowledge = new RecordingKnowledge();
        var history = new RecordingHistory();
        var llm = new RecordingLlm();
        var service = new CodeChatService(knowledge, history, new Projects("app-a", "app-b"), llm, new NoAudit());

        var response = await service.AskAsync("user", CodeChatScope.All, new CodeAskRequest { Question = "Where is the pool scheduler?" }, CancellationToken.None);

        Assert.Equal(["app-a", "app-b"], knowledge.LastProjectIds);
        Assert.Contains("All applications (App app-a, App app-b)", llm.LastPrompt);
        Assert.True(history.LastScope!.IsAll);
        Assert.Equal("", response.ProjectId);
        Assert.Empty(response.ProjectIds);
    }

    [Fact]
    public async Task Ask_ExplicitScope_RequiresEveryApplicationToExist()
    {
        var service = new CodeChatService(new RecordingKnowledge(), new RecordingHistory(), new Projects("app-a"), new RecordingLlm(), new NoAudit());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.AskAsync("user", CodeChatScope.From(["app-a", "missing"]), new CodeAskRequest { Question = "q" }, CancellationToken.None));
    }

    [Fact]
    public async Task Ask_SingleProject_UsesLegacyProjectShape()
    {
        var knowledge = new RecordingKnowledge();
        var history = new RecordingHistory();
        var service = new CodeChatService(knowledge, history, new Projects("app-a", "app-b"), new RecordingLlm(), new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = "q" }, CancellationToken.None);

        Assert.Equal(["app-a"], knowledge.LastProjectIds);
        Assert.Equal("app-a", history.LastScope!.ProjectId);
        Assert.Equal("app-a", response.ProjectId);
        Assert.Equal(["app-a"], response.ProjectIds);
    }

    [Theory]
    [InlineData("Show the scheduler workflow", "## Process flow")]
    [InlineData("Draw scheduler dependencies", "## Application relationships")]
    public async Task Ask_GraphRequest_RendersReferencedEdges_AndPersistsSafeMarkdown(string question, string heading)
    {
        var history = new RecordingHistory();
        var graph = new ProcessDiagram("Scheduler", "Run saves work.",
            [new("run", "Run scheduler", ["C1"]), new("save", "Save work", ["C1"])],
            [new("run", "save", ["C1"])], []);
        var llm = new RecordingLlm
        {
            Response = JsonSerializer.Serialize(new
            {
                answer = """
                    The scheduler saves work.
                    ```mermaid
                    %%{init: {"securityLevel":"loose"}}%%
                    flowchart LR
                    A["<b>Unsafe</b>"] --> B
                    click A "https://example.test"
                    style A fill:red
                    ```
                    """,
                processGraph = graph
            })
        };
        var service = new CodeChatService(new RecordingKnowledge(), history, new Projects("app-a"), llm, new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = question }, CancellationToken.None);

        Assert.Contains(heading, response.Answer);
        Assert.Contains("flowchart TD\n  p_run[\"Run scheduler\"]\n  p_save[\"Save work\"]\n  p_run --> p_save\n", response.Answer);
        Assert.Contains("The scheduler saves work.", response.Answer);
        Assert.DoesNotContain("%%", response.Answer);
        Assert.DoesNotContain("click A", response.Answer);
        Assert.DoesNotContain("style A", response.Answer);
        Assert.DoesNotContain("<b>", response.Answer);
        Assert.Contains(response.Citations.Single().SourceUrl, response.Answer);
        Assert.Contains("\"id\":\"C1\"", llm.LastPrompt);
        Assert.Contains("Every node and edge needs actual current source IDs", llm.LastPrompt);
        Assert.Contains("Do not include Mermaid/chart fences", llm.LastPrompt);
        Assert.Equal(response.Answer, history.LastAnswer);
        Assert.Equal(1, llm.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not a graph\"")]
    [InlineData("""{"nodes":[{"id":"a","label":"Unknown","sourceIds":["C99"]}],"edges":[],"gaps":[]}""")]
    [InlineData("""{"nodes":[{"id":"a","label":"Known","sourceIds":["C1"]}],"edges":[{"from":"a","to":"missing","sourceIds":["C1"]}],"gaps":[]}""")]
    public async Task Ask_InvalidOrUnsupportedGraph_ReportsUnavailableWithoutModelMermaid(string graphJson)
    {
        var llm = new RecordingLlm
        {
            Response = JsonSerializer.Serialize(new
            {
                answer = "Known source only.\n```mermaid\nflowchart LR\nA-->B\n```",
                processGraph = JsonSerializer.Deserialize<JsonElement>(graphJson)
            })
        };
        var service = new CodeChatService(new RecordingKnowledge(), new RecordingHistory(), new Projects("app-a"), llm, new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = "Draw the workflow" }, CancellationToken.None);

        Assert.DoesNotContain("```mermaid", response.Answer);
        Assert.Contains("validated source-backed process diagram was not returned", response.Answer);
        Assert.Contains("Known source only.", response.Answer);
    }

    [Fact]
    public async Task Ask_PartialGraph_KeepsDisconnectedGroupsAndExplainsGap()
    {
        var llm = new RecordingLlm
        {
            Response = JsonSerializer.Serialize(new
            {
                answer = "Only separate operations are established.",
                processGraph = new ProcessDiagram("Partial", "",
                    [new("run", "Run scheduler", ["C1"]), new("save", "Save work", ["C1"])],
                    [], ["The connection is not established."])
            })
        };
        var service = new CodeChatService(new RecordingKnowledge(), new RecordingHistory(), new Projects("app-a"), llm, new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = "Show dependencies" }, CancellationToken.None);

        Assert.Contains("```mermaid", response.Answer);
        Assert.DoesNotContain("-->", response.Answer);
        Assert.Contains("Supported partial flow", response.Answer);
        Assert.Contains("The connection is not established.", response.Answer);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"answer":42}""")]
    public async Task Ask_MalformedGraphResponse_SurfacesErrorAndDoesNotSave(string output)
    {
        var history = new RecordingHistory();
        var service = new CodeChatService(new RecordingKnowledge(), history, new Projects("app-a"),
            new RecordingLlm { Response = output }, new NoAudit());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AskAsync("user", "app-a", new CodeAskRequest { Question = "Draw the workflow" }, CancellationToken.None));

        Assert.Contains("Repository graph generation", error.Message);
        Assert.Null(history.LastAnswer);
    }

    [Fact]
    public async Task Ask_OrdinaryQuestion_PreservesMarkdownWithoutStructuredGeneration()
    {
        const string answer = "## Scheduler\n\n```csharp\nRun();\n```";
        var llm = new RecordingLlm { Response = answer };
        var service = new CodeChatService(new RecordingKnowledge(), new RecordingHistory(), new Projects("app-a"), llm, new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = "Where is Scheduler defined?" }, CancellationToken.None);

        Assert.Equal(answer, response.Answer);
        Assert.DoesNotContain("Current source ID registry", llm.LastPrompt);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Ask_GraphWithoutEvidence_DoesNotGenerateUnsupportedAnswer()
    {
        var llm = new RecordingLlm();
        var service = new CodeChatService(new RecordingKnowledge { HasEvidence = false }, new RecordingHistory(),
            new Projects("app-a"), llm, new NoAudit());

        var response = await service.AskAsync("user", "app-a", new CodeAskRequest { Question = "Draw the workflow" }, CancellationToken.None);

        Assert.Empty(response.Citations);
        Assert.DoesNotContain("```mermaid", response.Answer);
        Assert.Equal(0, llm.Calls);
    }

    private sealed class Projects(params string[] ids) : IProjectRepository
    {
        private readonly List<ProjectConfig> projects = ids.Select(id => new ProjectConfig { ProjectId = id, ProjectName = "App " + id }).ToList();
        public Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default) => Task.FromResult(projectConfig);
        public Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectConfig>>(projects);
        public Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects.FirstOrDefault(project => project.ProjectId == projectId));
        public Task<ProjectConfig?> GetProjectByApplicationFilterAsync(string applicationFilter, CancellationToken cancellationToken = default) => Task.FromResult<ProjectConfig?>(null);
        public Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default) => Task.FromResult<ProjectConfig?>(projectConfig);
    }

    private sealed class RecordingKnowledge : ICodeKnowledgeService
    {
        public bool HasEvidence { get; init; } = true;
        public string? LastQuery { get; private set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastProjectIds { get; private set; }
        public Task<CodeRetrievalResult> ForRequestAsync(string query, TicketRequest request, CancellationToken token, CodeQueryEmbedding? reusable = null) => throw new NotSupportedException();
        public Task<CodeRetrievalResult> ForBackgroundPollingAsync(string query, TicketRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<CodeRetrievalResult> RetrieveAsync(string query, IReadOnlyList<string> projectIds, CancellationToken token)
        {
            LastQuery = query;
            Calls++;
            LastProjectIds = projectIds;
            var evidence = new CodeEvidence("repo", projectIds[0], "org/repo", "main", "abc123", "src/Scheduler.cs", "Scheduler", 1, 10, "class Scheduler { void Run() { Save(); } void Save() {} }", "https://github.com/org/repo/blob/abc123/src/Scheduler.cs#L1-L10");
            return Task.FromResult(HasEvidence ? new CodeRetrievalResult("ready", [evidence]) : new CodeRetrievalResult("no-accessible-matches", []));
        }
    }

    private sealed class RecordingHistory : ICodeChatHistoryStore
    {
        public CodeChatScope? LastScope { get; private set; }
        public string? LastAnswer { get; private set; }
        public Task<IReadOnlyList<CodeChatSessionSummary>> SessionsAsync(string user, CodeChatScope scope, CancellationToken token) => Task.FromResult<IReadOnlyList<CodeChatSessionSummary>>([]);
        public Task<CodeChatSession?> SessionAsync(string user, string sessionId, CancellationToken token) => Task.FromResult<CodeChatSession?>(null);
        public Task<string> AppendExchangeAsync(string user, string? sessionId, CodeChatScope scope, string question, string answer, IReadOnlyList<CodeEvidence> citations, CancellationToken token)
        {
            LastScope = scope;
            LastAnswer = answer;
            return Task.FromResult("session-1");
        }
        public Task<bool> DeleteSessionAsync(string user, string sessionId, CancellationToken token) => Task.FromResult(true);
    }

    private sealed class RecordingLlm : ILLMService
    {
        public string Response { get; init; } = "answer";
        public int Calls { get; private set; }
        public string LastPrompt { get; private set; } = "";
        public Task<string> GetResponseAsync(string prompt) { Calls++; LastPrompt = prompt; return Task.FromResult(Response); }
    }

    private sealed class NoAudit : IUserActivityAuditLogger
    {
        public Task LogAsync(string action, string entityType, string entityId, string details, bool success = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("Explain more")]
    [InlineData("Where is it called?")]
    [InlineData("Why?")]
    [InlineData("Explain this")]
    [InlineData("Explain the second option")]
    public async Task Followup_ContextualizesBeforeSingleRetrievalEvenWhenEvidenceExists(string question)
    {
        var knowledge = new RecordingKnowledge();
        var service = new CodeChatService(knowledge, new RecordingHistory(), new Projects("app-a"), new RecordingLlm(), new NoAudit());
        var response = await service.AskAsync("user", "app-a", new CodeAskRequest
        {
            Question = question,
            History = [new() { Role = "user", Content = "Where is the payment scheduler defined?" },
                new() { Role = "assistant", Content = "An unverified earlier hypothesis." }]
        }, default);
        Assert.Contains("payment scheduler", knowledge.LastQuery);
        Assert.Contains(question, knowledge.LastQuery);
        Assert.DoesNotContain("unverified", knowledge.LastQuery);
        Assert.Equal(1, knowledge.Calls);
        Assert.Contains("retrieval", response.TimingsMs.Keys);
        Assert.Contains("generation", response.TimingsMs.Keys);
    }
}
