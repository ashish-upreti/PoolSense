using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Data;
using PoolSense.Api.Feedback;
using PoolSense.Api.Logging;
using PoolSense.Api.Models;
using PoolSense.Api.Orchestration;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using PoolSense.Api.Services.Nyra;
using PoolSense.Application.Models;
using Xunit;

namespace PoolSense.Api.Tests;

/// <summary>
/// Verifies that the orchestrator's three evidence sources (Pool DB, NYRA Wiki, indexed code) are
/// started together and fanned in with Task.WhenAll, that progress events stay truthful, and that
/// per-source error degradation keeps working.
/// </summary>
public sealed class TicketWorkflowOrchestratorTests
{
    private const int RetrievalWaitSeconds = 10;

    private static string PlannedClassification(string category, string kind, bool explicitRequest = true) =>
        JsonSerializer.Serialize(new { category, reasoning = "Classified and planned in one call.",
            visualizationPlan = new { needed = kind != "none", kind, explicitRequest, purpose = "Explain supplied evidence." } });

    [Theory]
    [InlineData("search wiki for related apps", "Issue", false, true, false)]
    [InlineData("search repo for related apps", "Issue", false, false, true)]
    [InlineData("search pool for related apps", "Info", true, true, true)]
    public async Task ExplicitRouting_IsNeverChangedByClassifierCategoryOrPlan(string question, string classifiedCategory, bool pool, bool wiki, bool code)
    {
        var classifier = new StubQueryCategorizationAgent { ResponseJson = PlannedClassification(classifiedCategory, "relationship") };
        var request = NewRequest();
        request.Title = request.Description = question;
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(),
            categorizer: classifier, forbidIncidentWork: !pool).RecommendAsync(request);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal("explicit", result.SourcePlan!.Origin);
        Assert.Equal(pool, result.SourcePlan.Pool);
        Assert.Equal(wiki, result.SourcePlan.Wiki);
        Assert.Equal(code, result.SourcePlan.Code);
        Assert.Equal("Info", result.QueryCategory);
        Assert.Equal("relationship", result.VisualizationPlan!.Kind);
    }

    [Fact]
    public async Task UnknownCategory_RetainsValidPlanAndRespectsSourceExclusions()
    {
        var classifier = new StubQueryCategorizationAgent { ResponseJson = PlannedClassification("Unknown", "table") };
        var code = FreshCode();
        var request = NewRequest();
        request.Title = request.Description = "do not search wiki; compare app roles in a table";
        var wiki = new StubNyraRetrieval();
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), wiki, code,
            categorizer: classifier, projectRepository: new StubProjectRepository { Forbid = true }).RecommendAsync(request);
        Assert.Equal("Issue", result.QueryCategory);
        Assert.Equal("classification-fallback", result.SourcePlan!.Origin);
        Assert.Equal("table", result.VisualizationPlan!.Kind);
        Assert.Equal(0, wiki.Calls);
        Assert.Equal(1, code.Calls);
        Assert.True(result.SourcePlan.Pool && result.SourcePlan.Code);
    }

    [Theory]
    [InlineData("Can you create a image to explain how the multiple project applications are related to each other if at all", "Info", true)]
    [InlineData("How apps talk", "Info", false)]
    [InlineData("draw that", "Info", true)]
    [InlineData("App A failed; draw a decision diagram", "Issue", true)]
    public async Task ClassifierPlan_ProducesSeparateConnectedGroupsWithoutInventingCrossEdges(string question, string category, bool explicitRequest)
    {
        var graph = new ProcessDiagram("Application relationships", "Two source-supported groups; other connections are unconfirmed.",
            [new("a", "App A", ["C1"]), new("b", "App B", ["C1"]), new("c", "App C", ["C1"]), new("d", "App D", ["C1"])],
            [new("a", "b", ["C1"]), new("c", "d", ["C1"])], ["No cross-group relationship is established."]);
        var resolution = new StubResolutionAgent { ResponseJson = JsonSerializer.Serialize(new { processGraph = graph, detailedAnalysisMarkdown = "## Assessment\nSupported groups." }) };
        var classifier = new StubQueryCategorizationAgent { ResponseJson = PlannedClassification(category, "relationship", explicitRequest) };
        var code = FreshCode();
        code.Result = code.Result with { Documents = [code.Result.Documents[0] with { Content = "App A calls App B. App C calls App D. No cross-group relationship is established." }] };
        var request = NewRequest();
        request.Title = request.Description = question;
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), code, resolution,
            categorizer: classifier, forbidIncidentWork: category == "Info")
            .RecommendAsync(request, new TicketConversationContext([new("user", "Explain the applications")]), null);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, resolution.Calls);
        Assert.Equal(result.VisualizationPlan, resolution.Plan);
        Assert.Equal("relationship", result.VisualizationPlan!.Kind);
        Assert.Equal(explicitRequest, result.VisualizationPlan.ExplicitRequest);
        Assert.Equal("partial", result.VisualizationStatus);
        Assert.Equal("provided", result.ProcessDiagramStatus);
        Assert.StartsWith("## Application relationships\n\n```mermaid", result.DetailedAnalysisMarkdown);
        Assert.Contains("p_a --> p_b", result.DetailedAnalysisMarkdown);
        Assert.Contains("p_c --> p_d", result.DetailedAnalysisMarkdown);
        Assert.DoesNotContain("p_b --> p_c", result.DetailedAnalysisMarkdown);
        Assert.Equal(category == "Issue", result.UsedPoolDatabase);
        Assert.Equal(0, resolution.RepairCalls);
    }

    [Theory]
    [InlineData("none", "What is App A?", "Info")]
    [InlineData("table", "Compare app responsibilities in a table", "Info")]
    [InlineData("table", "App failed; compare possible causes in a table", "Issue")]
    [InlineData("bar", "Chart observed app latency", "Info")]
    public async Task TypedPlan_DrivesValidatedTableChartOrNoVisual(string kind, string question, string category)
    {
        var resolution = new StubResolutionAgent { ResponseJson = JsonSerializer.Serialize(new
        {
            suggestedResolution = "Current source-backed facts.",
            detailedAnalysisMarkdown = "## Assessment\nCurrent facts.",
            visualizationTable = new VisualizationTable("Responsibilities", ["App", "Role"],
                [new(["App A", "Publisher"], ["C1"])]),
            visualizationChart = new VisualizationChart("Latency", "ms", [new("App A", 12, ["C1"], "App A: 12 ms")])
        }) };
        var code = FreshCode();
        code.Result = code.Result with { Documents = [code.Result.Documents[0] with { Content = "App A is the Publisher. App A: 12 ms" }] };
        var classifier = new StubQueryCategorizationAgent { ResponseJson = PlannedClassification(category, kind, kind != "none") };
        var request = NewRequest();
        request.Title = request.Description = question;
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), code, resolution,
            categorizer: classifier, forbidIncidentWork: category == "Info").RecommendAsync(request);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(category, result.QueryCategory);
        Assert.Equal(kind, resolution.Plan!.Kind);
        Assert.Equal(kind == "none" ? "not-needed" : "provided", result.VisualizationStatus);
        Assert.Equal("", result.ProcessDiagramStatus);
        Assert.Equal(0, resolution.RepairCalls);
        if (kind == "bar") { Assert.Contains("```chart", result.DetailedAnalysisMarkdown); Assert.Contains("\"data\":[12]", result.DetailedAnalysisMarkdown); }
        if (kind == "table") Assert.Contains("| App A | Publisher | C1 |", result.DetailedAnalysisMarkdown);
        if (kind == "none") Assert.Equal("## Assessment\nCurrent facts.", result.DetailedAnalysisMarkdown);
    }

    [Theory]
    [InlineData("""{"category":"Info","visualizationPlan":{"needed":"yes"}}""")]
    [InlineData("""{"category":"Info","visualizationPlan":{"needed":true,"kind":"image-generation","explicitRequest":true,"purpose":"draw"}}""")]
    [InlineData("""{"category":"Info"}""")]
    [InlineData("""{"category":"Info","visualizationPlan":{"needed":false,"kind":"none","explicitRequest":true,"purpose":""}}""")]
    public async Task BadPlan_DoesNotLoseValidCategory_AndPreservesExplicitImageIntent(string json)
    {
        var classifier = new StubQueryCategorizationAgent { ResponseJson = json };
        var resolution = new StubResolutionAgent();
        var request = NewRequest();
        request.Title = request.Description = "Create an image showing how apps are related";
        var pool = new StubVectorStore { ThrowOnSearch = new InvalidOperationException("Info must not use Pool") };
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), pool, new StubNyraRetrieval(), FreshCode(), resolution,
            categorizer: classifier, forbidIncidentWork: true).RecommendAsync(request);
        Assert.Equal("Info", result.QueryCategory);
        Assert.Equal("relationship", result.VisualizationPlan!.Kind);
        Assert.True(result.VisualizationPlan.ExplicitRequest);
        Assert.Equal("unavailable", result.VisualizationStatus);
        Assert.Equal(0, pool.Calls);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, resolution.RepairCalls);
    }

    [Fact]
    public async Task PlannerFailure_PreservesImageRequestAndAllSourceFallback()
    {
        var classifier = new StubQueryCategorizationAgent { Failure = new InvalidOperationException("Unavailable") };
        var request = NewRequest();
        request.Title = request.Description = "Can you create a image to explain how the multiple project applications are related to each other if at all";
        var pool = new StubVectorStore();
        var wiki = new StubNyraRetrieval();
        var code = FreshCode();
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), pool, wiki, code, categorizer: classifier).RecommendAsync(request);
        Assert.Equal("Issue", result.QueryCategory);
        Assert.Equal("classification-fallback", result.SourcePlan!.Origin);
        Assert.Equal("relationship", result.VisualizationPlan!.Kind);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, pool.Calls);
        Assert.Equal(1, wiki.Calls);
        Assert.Equal(1, code.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableVisual_NeverClaimsAccompanyingDiagram_AndRepairsAtMostOnce(bool hasSources)
    {
        var resolution = new StubResolutionAgent { ResponseJson = """
            {"suggestedRootCause":"The accompanying diagram shows all relationships.",
             "suggestedResolution":"See the diagram below. App A handles publication.",
             "reasoning":"An accompanying image illustrates the connections.",
             "detailedAnalysisMarkdown":"## Assessment\nThe partial relationship diagram below explains everything.\nApp A handles publication."}
            """ };
        var request = NewRequest();
        request.Title = request.Description = "Create an image of related apps";
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(),
            hasSources ? FreshCode() : new StubCodeKnowledge(), resolution,
            categorizer: new StubQueryCategorizationAgent { ResponseJson = PlannedClassification("Info", "relationship") }).RecommendAsync(request);
        Assert.Equal("unavailable", result.VisualizationStatus);
        Assert.Equal(hasSources ? 1 : 0, resolution.RepairCalls);
        Assert.Contains("Visualization unavailable", result.SuggestedResolution);
        Assert.Contains("App A handles publication", result.SuggestedResolution);
        Assert.DoesNotContain("accompanying", result.SuggestedRootCause + result.SuggestedResolution + result.Reasoning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagram below", result.DetailedAnalysisMarkdown);
        Assert.DoesNotContain("```mermaid", result.DetailedAnalysisMarkdown);
    }

    [Fact]
    public async Task BackgroundEvent_DoesNotInvokePlannerOrRequestVisual()
    {
        var request = NewRequest();
        request.SourceEventId = "event-1";
        request.Title = request.Description = "Draw a process image";
        var classifier = new StubQueryCategorizationAgent { Failure = new InvalidOperationException("No background planner") };
        var resolution = new StubResolutionAgent();
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(), resolution,
            categorizer: classifier).RecommendAsync(request);
        Assert.Equal(0, classifier.Calls);
        Assert.Equal("Issue", result.QueryCategory);
        Assert.Equal("not-needed", result.VisualizationStatus);
        Assert.False(resolution.Plan!.Needed);
    }

    [Theory]
    [InlineData("search wiki for publication flow", 0, 1, 0)]
    [InlineData("look in the repo for publication flow", 0, 0, 1)]
    [InlineData("search code and wiki for publication flow", 0, 1, 1)]
    [InlineData("search pool for publication", 1, 1, 1)]
    public async Task ExplicitPlan_PlansVisualOnceAndSkipsEveryExcludedProvider(string question, int poolCalls, int wikiCalls, int codeCalls)
    {
        var classifier = new StubQueryCategorizationAgent { Failure = new InvalidOperationException("Planner failure must not change explicit routing") };
        var pool = new StubVectorStore();
        var wiki = new StubNyraRetrieval();
        var code = FreshCode();
        var embedding = new StubEmbeddingService { Forbid = poolCalls == 0 };
        var request = NewRequest();
        request.Title = request.Description = question;
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), pool, wiki, code, new StubResolutionAgent(), embedding, classifier,
            forbidIncidentWork: poolCalls == 0, projectRepository: new StubProjectRepository { Forbid = wikiCalls == 0 }).RecommendAsync(request);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(poolCalls, pool.Calls);
        Assert.Equal(wikiCalls, wiki.Calls);
        Assert.Equal(codeCalls, code.Calls);
        Assert.Equal(poolCalls, embedding.Calls);
        Assert.Equal(poolCalls == 1, result.UsedPoolDatabase);
        Assert.Equal("explicit", result.SourcePlan!.Origin);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"category\":\"Other\"}")]
    public async Task InvalidClassification_FallsBackExplicitlyToAllThree(string response)
    {
        var classifier = new StubQueryCategorizationAgent { ResponseJson = response };
        var pool = new StubVectorStore();
        var wiki = new StubNyraRetrieval();
        var code = FreshCode();
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), pool, wiki, code, categorizer: classifier).RecommendAsync(NewRequest());
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, pool.Calls);
        Assert.Equal(1, wiki.Calls);
        Assert.Equal(1, code.Calls);
        Assert.Equal("classification-fallback", result.SourcePlan!.Origin);
        Assert.Contains("falling back", result.QueryCategorizationReasoning);
    }

    [Fact]
    public async Task ClassificationFailureAndCancellation_AreNotSilentDefaults()
    {
        var classifier = new StubQueryCategorizationAgent { Failure = new InvalidOperationException("Provider unavailable") };
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(),
            categorizer: classifier).RecommendAsync(NewRequest());
        Assert.Equal("Issue", result.QueryCategory);
        Assert.Equal("classification-fallback", result.SourcePlan!.Origin);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(), categorizer: classifier)
                .RecommendAsync(NewRequest(), cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringClassification_PropagatesWithoutStartingAnyProvider()
    {
        using var cancellation = new CancellationTokenSource();
        var classifier = new StubQueryCategorizationAgent { BeforeResponse = cancellation.Cancel };
        var analyzer = new StubTicketAnalyzer();
        var pool = new StubVectorStore();
        var wiki = new StubNyraRetrieval();
        var code = FreshCode();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateOrchestrator(analyzer, pool, wiki, code, categorizer: classifier).RecommendAsync(NewRequest(), cancellation.Token));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(0, analyzer.Calls + pool.Calls + wiki.Calls + code.Calls);
    }

    [Fact]
    public async Task CancellationDuringRepair_PropagatesRatherThanBecomingUnavailable()
    {
        using var cancellation = new CancellationTokenSource();
        var resolution = new StubResolutionAgent { BeforeRepair = cancellation.Cancel };
        var request = NewRequest();
        request.Title = request.Description = "Draw related applications";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(), resolution,
                categorizer: new StubQueryCategorizationAgent { ResponseJson = PlannedClassification("Info", "relationship") })
                .RecommendAsync(request, cancellation.Token));
        Assert.Equal(1, resolution.RepairCalls);
    }

    [Fact]
    public async Task ConflictingPoolDirective_ClarifiesBeforeAnalyzerOrProviders()
    {
        var classifier = new StubQueryCategorizationAgent();
        var analyzer = new StubTicketAnalyzer();
        var pool = new StubVectorStore();
        var wiki = new StubNyraRetrieval();
        var code = FreshCode();
        var request = NewRequest();
        request.Title = request.Description = "search pool, not code";
        var result = await CreateOrchestrator(analyzer, pool, wiki, code, categorizer: classifier).RecommendAsync(request);
        Assert.Contains("conflicts", result.SuggestedResolution);
        Assert.Equal(0, classifier.Calls + analyzer.Calls + pool.Calls + wiki.Calls + code.Calls);
    }

    [Fact]
    public async Task Results_ReportServerProcessingDuration()
    {
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode()).RecommendAsync(NewRequest());
        Assert.NotNull(result.ProcessingDurationMs);
        Assert.True(result.ProcessingDurationMs >= 0);
    }

    [Fact]
    public async Task ClarificationResults_ReportServerProcessingDuration()
    {
        var request = NewRequest();
        request.Title = request.Description = "search pool, not code";
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(),
            categorizer: new StubQueryCategorizationAgent()).RecommendAsync(request);
        Assert.Contains("conflicts", result.SuggestedResolution);
        Assert.NotNull(result.ProcessingDurationMs);
        Assert.True(result.ProcessingDurationMs >= 0);
    }

    private static ProcessDiagram PublicationGraph() => new(
        "Publication flow",
        "Requests are validated and prepared before publication is persisted and downstream consumers are notified.",
        [new("validate", "Validate request", ["C1"]), new("prepare", "Prepare publication", ["C1"]),
         new("persist", "Persist publication", ["C1"]), new("notify", "Notify downstream", ["C1"])],
        [new("validate", "prepare", ["C1"]), new("prepare", "persist", ["C1"]), new("persist", "notify", ["C1"])], []);

    [Theory]
    [InlineData("onemps publication flow")]
    [InlineData("walk me through publication end to end")]
    [InlineData("show that as a flow")]
    public async Task ProcessRequest_GeneratesGroundedFlow_WithoutInventingAnIncidentOrSkippingSources(string question)
    {
        var gate = new RetrievalGate("nyra", "code");
        var analyzer = new StubTicketAnalyzer { ResponseJson = """{"problem":"Publication process workflow"}""" };
        var code = new StubCodeKnowledge(gate)
        {
            Result = new("ready", [new("r", "P1", "org/repo", "main", new string('a', 40), "Publish.cs", "Publish", 1, 10,
                "void Publish() { Validate(); Prepare(); Persist(); Notify(); }", $"https://github.com/org/repo/blob/{new string('a', 40)}/Publish.cs#L1-L10")])
        };
        var resolution = new StubResolutionAgent { ResponseJson = JsonSerializer.Serialize(new
        {
            suggestedRootCause = "No technical root cause is established because no failure was reported.",
            suggestedResolution = "Clarify whether a failure or process explanation is required.",
            detailedAnalysisMarkdown = "## Assessment\nEvidence-backed publication explanation.",
            confidence = .85,
            processGraph = PublicationGraph()
        }) };
        var nyra = new StubNyraRetrieval(gate) { Documents = { new() { Title = "Publication", Content = "Validate, prepare, persist, then notify." } } };
        var request = NewRequest();
        request.Title = question;
        request.Description = question;
        var pool = new StubVectorStore { ThrowOnSearch = new InvalidOperationException("Info must not search Pool") };
        var embedding = new StubEmbeddingService { Forbid = true };
        var classifier = new StubQueryCategorizationAgent { ResponseJson = """{"category":"Info","reasoning":"Normal process explanation"}""" };
        var result = await CreateOrchestrator(analyzer, pool, nyra, code, resolution, embedding, classifier, forbidIncidentWork: true)
            .RecommendAsync(request, new TicketConversationContext([new("user", "Explain publication")]), null);
        Assert.Equal("Info", result.QueryCategory);
        Assert.Equal(ResolutionAnswerIntent.Explanation, resolution.Intent);
        Assert.True(resolution.WantsFlow);
        Assert.Equal("provided", result.ProcessDiagramStatus);
        Assert.StartsWith("## Process flow\n\n```mermaid\nflowchart TD", result.DetailedAnalysisMarkdown);
        Assert.Contains("p_validate --> p_prepare", result.DetailedAnalysisMarkdown);
        Assert.Contains(code.Result.Documents[0].SourceUrl, result.DetailedAnalysisMarkdown);
        Assert.DoesNotContain("root cause", result.SuggestedRootCause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clarify", result.SuggestedResolution);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, resolution.Calls);
        Assert.Equal(0, resolution.RepairCalls);
        Assert.Single(result.NyraDocuments);
        Assert.Single(result.CodeDocuments);
        Assert.False(result.UsedPoolDatabase);
        Assert.Equal(0, pool.Calls);
        Assert.Equal(0, embedding.Calls);
        Assert.Equal(1, classifier.Calls);
        Assert.True(classifier.Context!.HasTurns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRequest_OmittedOrMalformedGraph_UsesOneBoundedRepairWithCurrentEvidence(bool malformed)
    {
        var resolution = new StubResolutionAgent
        {
            ResponseJson = JsonSerializer.Serialize(new
            {
                suggestedResolution = "A textual process explanation.",
                detailedAnalysisMarkdown = "```mermaid\nflowchart TD\n broken[\n```",
                processGraph = malformed ? PublicationGraph() with { Edges = [new("validate", "made_up", ["C99"])] } : null
            }),
            RepairJson = JsonSerializer.Serialize(new { processGraph = PublicationGraph() })
        };
        var request = NewRequest();
        request.Title = request.Description = "onemps publication flow";
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(), resolution,
            categorizer: new StubQueryCategorizationAgent { ResponseJson = """{"category":"Info"}""" }).RecommendAsync(request);
        Assert.Equal("provided", result.ProcessDiagramStatus);
        Assert.Equal(1, resolution.RepairCalls);
        Assert.Single(resolution.RepairEvidence!);
        Assert.Contains("fresh authorized source", resolution.RepairEvidence![0].Content);
        Assert.DoesNotContain("broken[", result.DetailedAnalysisMarkdown);
        Assert.Equal(1, result.DetailedAnalysisMarkdown.Split("```mermaid").Length - 1);
    }

    [Fact]
    public async Task ProcessRequest_InsufficientEvidence_IsExplicitRatherThanManufacturingAFlow()
    {
        var resolution = new StubResolutionAgent { ResponseJson = """{"suggestedResolution":"No process evidence is available."}""" };
        var request = NewRequest();
        request.Title = request.Description = "onemps publication flow";
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), new StubCodeKnowledge(), resolution,
            categorizer: new StubQueryCategorizationAgent { ResponseJson = """{"category":"Info"}""" })
            .RecommendAsync(request);
        Assert.Equal("Info", result.QueryCategory);
        Assert.Equal("unavailable", result.ProcessDiagramStatus);
        Assert.DoesNotContain("```mermaid", result.DetailedAnalysisMarkdown);
        Assert.Contains("No current source-backed process steps", result.DetailedAnalysisMarkdown);
        Assert.Equal(0, resolution.RepairCalls);
    }

    [Fact]
    public async Task GenuineFailure_RemainsTroubleshooting_WithoutUnrequestedRepair()
    {
        var resolution = new StubResolutionAgent();
        var request = NewRequest();
        request.Title = request.Description = "Publication failed with HTTP 500";
        var result = await CreateOrchestrator(new StubTicketAnalyzer(), new StubVectorStore(), new StubNyraRetrieval(), FreshCode(), resolution).RecommendAsync(request);
        Assert.Equal("Issue", result.QueryCategory);
        Assert.Equal(ResolutionAnswerIntent.Incident, resolution.Intent);
        Assert.False(resolution.WantsFlow);
        Assert.Equal("", result.ProcessDiagramStatus);
        Assert.Equal(0, resolution.RepairCalls);
    }

    [Fact]
    public async Task Followup_UsesAnalyzerQueryForAllThreeSources_AndPreservesCurrentQuestionForResolution()
    {
        const string query = "Validate VG-123 mapping in Mapping.cs, the third step for the pump fault";
        var analyzer = new StubTicketAnalyzer { ResponseJson = JsonSerializer.Serialize(new { problem = query }) };
        var embedding = new StubEmbeddingService();
        var nyra = new StubNyraRetrieval();
        var code = FreshCode();
        var resolution = new StubResolutionAgent();
        var context = new TicketConversationContext([new("user", "Pump fault"), new("assistant", "3. Validate VG-123 in Mapping.cs")]);
        var request = NewRequest();
        request.Title = "Explain step 3";
        request.Description = "Explain step 3";
        await CreateOrchestrator(analyzer, new StubVectorStore(), nyra, code, resolution, embedding)
            .RecommendAsync(request, context, null, CancellationToken.None);
        Assert.Equal(1, analyzer.Calls);
        Assert.Same(context, analyzer.Context);
        Assert.Equal(query, embedding.Text);
        Assert.Equal(query, nyra.Query);
        Assert.Equal(query, code.Query);
        Assert.NotNull(code.Reusable);
        Assert.Equal(query, code.Reusable.Query);
        Assert.Equal("fixture-embedding-profile", code.Reusable.Profile);
        Assert.Equal(new float[] { .25f, -.75f }, code.Reusable.Vector);
        Assert.Same(context, resolution.Context);
        Assert.Equal(query, resolution.Query);
        Assert.Equal("Explain step 3", resolution.Question);
        Assert.Equal(1, resolution.Calls);
    }

    [Fact]
    public async Task Followup_AmbiguousReference_AsksClarification_AfterFreshRetrieval()
    {
        var analyzer = new StubTicketAnalyzer { ResponseJson = """{"problem":"invented option","requiresClarification":true,"clarificationQuestion":"Which second option do you mean?"}""" };
        var embedding = new StubEmbeddingService();
        var nyra = new StubNyraRetrieval();
        var code = FreshCode();
        var resolution = new StubResolutionAgent();
        var request = NewRequest();
        request.Title = "What about the second option?";
        request.Description = request.Title;
        var result = await CreateOrchestrator(analyzer, new StubVectorStore(), nyra, code, resolution, embedding)
            .RecommendAsync(request, new TicketConversationContext([new("user", "Vague prior question")]), null, CancellationToken.None);
        Assert.Equal("Which second option do you mean?", result.SuggestedResolution);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(0, resolution.Calls);
        Assert.DoesNotContain("invented option", embedding.Text!);
        Assert.Equal(embedding.Text, nyra.Query);
        Assert.Equal(embedding.Text, code.Query);
    }

    [Fact]
    public async Task InitialRequest_UsesOriginalAnalyzerAndResolutionPaths()
    {
        var analyzer = new StubTicketAnalyzer();
        var resolution = new StubResolutionAgent();
        var code = FreshCode();
        await CreateOrchestrator(analyzer, new StubVectorStore(), new StubNyraRetrieval(), code, resolution).RecommendAsync(NewRequest());
        Assert.Null(analyzer.Context);
        Assert.Null(resolution.Context);
        Assert.Contains("Pump tripping breaker", code.Query);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, resolution.Calls);
    }

    [Fact]
    public async Task Recommend_RunsAllThreeRetrievalSourcesConcurrently_AndReportsTruthfulProgress()
    {
        var gate = new RetrievalGate("pool", "nyra", "code");
        var analyzer = new StubTicketAnalyzer();
        var vectorStore = new StubVectorStore(gate)
        {
            Tickets =
            {
                new TicketKnowledge
                {
                    TicketId = "T-100", Problem = "Pump trips breaker", RootCause = "Clogged impeller",
                    Resolution = "Clean impeller", Similarity = 0.87
                }
            }
        };
        var nyra = new StubNyraRetrieval(gate)
        {
            Documents = { new NyraDocumentResult { DocumentId = "D-1", KbName = "KB-One", Title = "Wiki page" } }
        };
        var code = new StubCodeKnowledge(gate)
        {
            Result = new CodeRetrievalResult("indexed",
            [
                new CodeEvidence("repo-1", "P1", "org/repo", "main", "abc123", "Pump.cs", "Start", 10, 20,
                    "public void Start() {}", "https://github.com/org/repo/blob/main/Pump.cs")
            ])
        };
        var recorder = new ProgressRecorder();

        var result = await CreateOrchestrator(analyzer, vectorStore, nyra, code).RecommendAsync(
            NewRequest(),
            recorder.Record,
            CancellationToken.None);

        // Typed results from all three sources survive the fan-in.
        Assert.Single(result.SimilarIncidents);
        Assert.Equal("T-100", result.SimilarIncidents[0].TicketId);
        Assert.Equal(0.87, result.SimilarIncidents[0].Similarity);
        Assert.Single(result.NyraDocuments);
        Assert.Equal("D-1", result.NyraDocuments[0].DocumentId);
        Assert.Single(result.CodeDocuments);
        Assert.Equal("indexed", result.CodeKnowledgeStatus);
        Assert.Equal(StubResolutionAgent.DefaultDetailedAnalysisMarkdown, result.DetailedAnalysisMarkdown);
        Assert.True(result.UsedPoolDatabase);
        Assert.True(result.NyraKnowledgeBaseUsed);
        Assert.Equal("Queried", result.NyraKnowledgeBaseStatus);

        // All three retrieval stages were reported active before any of them completed.
        var retrievalEvents = recorder.Events
            .Where(e => e.Stage is "pool-data" or "nyra-wiki" or "code-repositories")
            .ToList();
        var firstCompletion = retrievalEvents.FindIndex(e => e.State is "completed" or "skipped");
        Assert.True(firstCompletion >= 0, "Expected at least one retrieval completion event.");
        Assert.All(retrievalEvents.Take(firstCompletion), e => Assert.Equal("active", e.State));
        Assert.Equal(
            new HashSet<string> { "pool-data", "nyra-wiki", "code-repositories" },
            retrievalEvents.Take(firstCompletion).Select(e => e.Stage).ToHashSet());

        // Stage order stays stable for UI sorting.
        Assert.Equal(3, retrievalEvents.Single(e => e.Stage == "pool-data" && e.State == "active").Order);
        Assert.Equal(4, retrievalEvents.Single(e => e.Stage == "nyra-wiki" && e.State == "active").Order);
        Assert.Equal(5, retrievalEvents.Single(e => e.Stage == "code-repositories" && e.State == "active").Order);
    }

    [Fact]
    public async Task Recommend_NyraFailureDegradesGracefully_AndKeepsPoolAndCodeResults()
    {
        var gate = new RetrievalGate("pool", "code");
        var vectorStore = new StubVectorStore(gate)
        {
            Tickets = { new TicketKnowledge { TicketId = "T-200", Problem = "p", Similarity = 0.5 } }
        };
        var nyra = new StubNyraRetrieval(gate) { ThrowOnRetrieve = new InvalidOperationException("nyra down") };
        var code = new StubCodeKnowledge(gate)
        {
            Result = new CodeRetrievalResult("indexed",
                [new CodeEvidence("repo-2", "P1", "org/repo2", "main", "def456", "Valve.cs", "Open", 1, 2, "x", "u")])
        };
        var recorder = new ProgressRecorder();

        var result = await CreateOrchestrator(new StubTicketAnalyzer(), vectorStore, nyra, code).RecommendAsync(
            NewRequest(),
            recorder.Record,
            CancellationToken.None);

        Assert.Empty(result.NyraDocuments);
        Assert.Equal("Failed", result.NyraKnowledgeBaseStatus);
        Assert.Equal("nyra down", result.NyraKnowledgeBaseMessage);
        Assert.True(result.NyraKnowledgeBaseUsed);
        Assert.Single(result.SimilarIncidents);
        Assert.Single(result.CodeDocuments);

        // NYRA was still reported active before its task ran, and its completion event was emitted.
        var nyraEvents = recorder.Events.Where(e => e.Stage == "nyra-wiki").ToList();
        Assert.Contains(nyraEvents, e => e.State == "active");
        Assert.Contains(nyraEvents, e => e.State == "completed");
    }

    [Fact]
    public async Task Recommend_PoolSearchFailure_PropagatesOriginalException()
    {
        var vectorStore = new StubVectorStore { ThrowOnSearch = new InvalidOperationException("pool down") };
        var recorder = new ProgressRecorder();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOrchestrator(new StubTicketAnalyzer(), vectorStore, new StubNyraRetrieval(), new StubCodeKnowledge())
                .RecommendAsync(NewRequest(), recorder.Record, CancellationToken.None));

        Assert.Equal("pool down", exception.Message);
    }

    private static TicketWorkflowOrchestrator CreateOrchestrator(
        StubTicketAnalyzer analyzer,
        StubVectorStore vectorStore,
        StubNyraRetrieval nyra,
        StubCodeKnowledge code,
        StubResolutionAgent? resolutionAgent = null,
        StubEmbeddingService? embeddingService = null,
        StubQueryCategorizationAgent? categorizer = null,
        bool forbidIncidentWork = false,
        StubProjectRepository? projectRepository = null)
    {
        var projects = projectRepository ?? new StubProjectRepository();
        return new TicketWorkflowOrchestrator(
            analyzer,
            embeddingService ?? new StubEmbeddingService(),
            vectorStore,
            new IncidentContextBuilder(),
            resolutionAgent ?? new StubResolutionAgent(),
            categorizer ?? new StubQueryCategorizationAgent(),
            nyra,
            projects,
            new StubKnowledgeEnrichment { Forbid = forbidIncidentWork },
            new StubFailurePatternAgent { Forbid = forbidIncidentWork },
            new StubFailurePatternRepository(),
            new StubFeedbackRepository { Forbid = forbidIncidentWork },
            new StubValidatedResolutionRepository { Forbid = forbidIncidentWork },
            new InteractionLogger(new ThrowingSqlConnectionFactory(), NullLogger<InteractionLogger>.Instance),
            Microsoft.Extensions.Options.Options.Create(new TicketAutomationSettings()),
            NullLogger<TicketWorkflowOrchestrator>.Instance,
            code,
            new CodeFeedbackPolicy(
                new EphemeralDataProtectionProvider(),
                new HttpContextAccessor(),
                Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions())));
    }

    [Fact]
    public async Task Recommend_MissingDetailedAnalysisMarkdown_DefaultsToEmpty()
    {
        var resolutionAgent = new StubResolutionAgent
        {
            ResponseJson = JsonSerializer.Serialize(new
            {
                suggestedRootCause = "Clogged impeller",
                suggestedResolution = "Clean the impeller",
                confidence = 0.9,
                reasoning = "Matches historical incidents"
            })
        };
        var code = new StubCodeKnowledge
        {
            Result = new CodeRetrievalResult("indexed",
                [new CodeEvidence("repo-3", "P1", "org/repo3", "main", "aaa111", "F.cs", "M", 1, 2, "x", "u")])
        };

        var result = await CreateOrchestrator(
            new StubTicketAnalyzer(),
            new StubVectorStore(),
            new StubNyraRetrieval(),
            code,
            resolutionAgent).RecommendAsync(NewRequest(), null, CancellationToken.None);

        // Older agent responses without the field must keep working.
        Assert.Equal(string.Empty, result.DetailedAnalysisMarkdown);
        Assert.Equal("Clogged impeller", result.SuggestedRootCause);
    }

    private static StubCodeKnowledge FreshCode() => new()
    {
        Result = new CodeRetrievalResult("indexed",
            [new("repo", "P1", "org/repo", "main", "abc", "Mapping.cs", "Validate", 1, 2, "// fresh authorized source\nvoid Publish() { Validate(); Prepare(); Persist(); Notify(); }", "https://github.com/org/repo/blob/abc/Mapping.cs#L1-L2")])
    };

    private static TicketRequest NewRequest() => new()
    {
        TicketId = "T-100",
        Title = "Pump tripping breaker",
        Description = "Pump stops after five minutes",
        Application = "MOR/FCS/SCS"
    };

    /// <summary>
    /// Opens only once every required evidence source has actually started, so a regression to
    /// sequential awaits deadlocks the fake until WaitAsync times out instead of passing silently.
    /// </summary>
    private sealed class RetrievalGate(params string[] requiredSources)
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _started = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkStarted(string source)
        {
            lock (_lock)
            {
                _started.Add(source);
                if (requiredSources.All(_started.Contains))
                {
                    _allStarted.TrySetResult();
                }
            }
        }

        public Task WaitUntilAllStarted(TimeSpan timeout) => _allStarted.Task.WaitAsync(timeout);
    }

    private sealed class ProgressRecorder
    {
        private readonly object _lock = new();

        public List<(string Stage, string State, int Order)> Events { get; } = [];

        public Task Record(TicketWorkflowProgress progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Events.Add((progress.Stage, progress.State, progress.Order));
            }

            return Task.CompletedTask;
        }
    }

    private static Task WaitAsync(RetrievalGate? gate)
        => gate?.WaitUntilAllStarted(TimeSpan.FromSeconds(RetrievalWaitSeconds)) ?? Task.CompletedTask;

    private sealed class ThrowingSqlConnectionFactory : IPoolSenseSqlConnectionFactory
    {
        public Microsoft.Data.SqlClient.SqlConnection CreateConnection()
            => throw new InvalidOperationException("Test stub: SQL access is not expected in this test.");

        public bool HasConnectionString() => false;
    }

    private sealed class StubTicketAnalyzer : ITicketAnalyzerAgent
    {
        public TicketConversationContext? Context { get; private set; }
        public int Calls { get; private set; }
        public string? ResponseJson { get; init; }
        public Task<string> AnalyzeTicketAsync(string title, string description)
        {
            Calls++;
            return Task.FromResult(ResponseJson ?? JsonSerializer.Serialize(new
            {
                problem = "Pump trips breaker",
                rootCause = "Clogged impeller",
                resolution = "Clean the impeller",
                keywords = new[] { "pump" }
            }));
        }
        public Task<string> AnalyzeTicketAsync(string title, string description, TicketConversationContext context)
        {
            Context = context;
            return AnalyzeTicketAsync(title, description);
        }
    }

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public bool Forbid { get; init; }
        public int Calls { get; private set; }
        public string Profile => "fixture-embedding-profile";
        public string? Text { get; private set; }
        public Task<float[]> GenerateEmbedding(string text)
        {
            Calls++;
            if (Forbid) throw new InvalidOperationException("Pool embedding must not run");
            Text = text;
            return Task.FromResult(new float[] { 0.25f, -0.75f });
        }
    }

    private sealed class StubVectorStore(RetrievalGate? gate = null) : IVectorStore
    {
        public int Calls { get; private set; }
        public Exception? ThrowOnSearch { get; init; }

        public List<TicketKnowledge> Tickets { get; } = [];

        public async Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(
            float[] embedding,
            int limit = 5,
            IReadOnlyList<string>? selectedGroupIds = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            gate?.MarkStarted("pool");
            if (ThrowOnSearch is not null)
            {
                throw ThrowOnSearch;
            }

            await WaitAsync(gate);
            return Tickets;
        }

        public Task InsertTicketKnowledge(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult(0d);

        public Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(
            int monthCount = 6,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IncidentTimelinePoint>>([]);
    }

    private sealed class StubNyraRetrieval(RetrievalGate? gate = null) : INyraDocumentRetrievalService
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public Exception? ThrowOnRetrieve { get; init; }

        public List<NyraDocumentResult> Documents { get; } = [];

        public async Task<IReadOnlyList<NyraDocumentResult>> RetrieveHybridDocumentsAsync(
            string query,
            IReadOnlyList<string> kbNames,
            int limit = 5,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Query = query;
            gate?.MarkStarted("nyra");
            if (ThrowOnRetrieve is not null)
            {
                throw ThrowOnRetrieve;
            }

            await WaitAsync(gate);
            return Documents;
        }
    }

    private sealed class StubCodeKnowledge(RetrievalGate? gate = null) : ICodeKnowledgeService
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public CodeQueryEmbedding? Reusable { get; private set; }
        public CodeRetrievalResult Result { get; set; } = CodeRetrievalResult.Skipped;

        public async Task<CodeRetrievalResult> ForRequestAsync(
            string query,
            TicketRequest request,
            CancellationToken cancellationToken = default,
            CodeQueryEmbedding? reusable = null)
        {
            Calls++;
            Query = query;
            Reusable = reusable;
            gate?.MarkStarted("code");
            await WaitAsync(gate);
            return Result;
        }

        public Task<CodeRetrievalResult> RetrieveAsync(
            string query,
            IReadOnlyList<string> selectedGroupIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CodeRetrievalResult.Skipped);
    }

    private sealed class StubResolutionAgent : IResolutionAgent, IProcessDiagramRepair
    {
        public VisualizationPlan? Plan { get; private set; }
        public TicketConversationContext? Context { get; private set; }
        public string? Query { get; private set; }
        public string? Question { get; private set; }
        public int Calls { get; private set; }
        public ResolutionAnswerIntent Intent { get; private set; }
        public bool WantsFlow { get; private set; }
        public int RepairCalls { get; private set; }
        public Action? BeforeRepair { get; init; }
        public string RepairJson { get; set; } = "{}";
        public IReadOnlyList<ProcessSource>? RepairEvidence { get; private set; }
        public const string DefaultDetailedAnalysisMarkdown =
            "## Assessment\n\nPump impeller obstruction is the likely cause.\n\n## Evidence considered\n\n| Source | Identifier | Note |\n| --- | --- | --- |\n| Historical incident | T-100 | Pump trips breaker |\n\n## Validation checklist\n\n1. Inspect the impeller housing.\n2. Confirm correct rotation.";

        public string ResponseJson { get; set; } = JsonSerializer.Serialize(new
        {
            suggestedRootCause = "Clogged impeller",
            suggestedResolution = "Clean the impeller",
            confidence = 0.9,
            reasoning = "Matches historical incidents",
            detailedAnalysisMarkdown = DefaultDetailedAnalysisMarkdown
        });

        public Task<string> GenerateResolutionAsync(
            string title,
            string description,
            IReadOnlyList<ResolutionIncident> similarIncidents,
            IReadOnlyList<NyraDocumentResult> nyraDocuments,
            IReadOnlyList<CodeEvidence>? codeDocuments = null, ResolutionAnswerIntent intent = ResolutionAnswerIntent.Incident, bool requiresProcessDiagram = false)
        {
            Calls++;
            Intent = intent;
            WantsFlow = requiresProcessDiagram;
            Question = title;
            return Task.FromResult(ResponseJson);
        }

        public Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarIncidents,
            IReadOnlyList<NyraDocumentResult> nyraDocuments, IReadOnlyList<CodeEvidence>? codeDocuments, TicketConversationContext context, string retrievalQuery, ResolutionAnswerIntent intent = ResolutionAnswerIntent.Incident, bool requiresProcessDiagram = false)
        {
            Context = context;
            Query = retrievalQuery;
            return GenerateResolutionAsync(title, description, similarIncidents, nyraDocuments, codeDocuments, intent, requiresProcessDiagram);
        }

        public Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarIncidents,
            IReadOnlyList<NyraDocumentResult> nyraDocuments, IReadOnlyList<CodeEvidence>? codeDocuments, TicketConversationContext context,
            string retrievalQuery, ResolutionAnswerIntent intent, VisualizationPlan plan, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Plan = plan;
            return context.HasTurns
                ? GenerateResolutionAsync(title, description, similarIncidents, nyraDocuments, codeDocuments, context, retrievalQuery, intent, plan.IsGraph)
                : GenerateResolutionAsync(title, description, similarIncidents, nyraDocuments, codeDocuments, intent, plan.IsGraph);
        }

        public Task<string> RepairProcessDiagramAsync(string question, IReadOnlyList<ProcessSource> sources, CancellationToken token)
        {
            RepairCalls++;
            BeforeRepair?.Invoke();
            token.ThrowIfCancellationRequested();
            RepairEvidence = sources;
            return Task.FromResult(RepairJson);
        }
    }

    private sealed class StubQueryCategorizationAgent : IQueryCategorizationAgent
    {
        public string ResponseJson { get; set; } = """{"category":"Issue","reasoning":"Reported problem"}""";
        public Exception? Failure { get; init; }
        public Action? BeforeResponse { get; init; }
        public int Calls { get; private set; }
        public TicketConversationContext? Context { get; private set; }
        public Task<string> CategorizeQueryAsync(string query, string context)
            => CategorizeQueryAsync(query, context, TicketConversationContext.Empty, default);
        public Task<string> CategorizeQueryAsync(string title, string description, TicketConversationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Context = context;
            BeforeResponse?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult(ResponseJson);
        }
    }

    private sealed class StubProjectRepository : IProjectRepository
    {
        public bool Forbid { get; init; }
        public Task<ProjectConfig> CreateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
            => Task.FromResult(projectConfig);

        public Task<IReadOnlyList<ProjectConfig>> GetAllProjectsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectConfig>>([]);

        public Task<ProjectConfig?> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult<ProjectConfig?>(null);

        public Task<ProjectConfig?> GetProjectByApplicationFilterAsync(string applicationFilter, CancellationToken cancellationToken = default)
            => Forbid ? throw new InvalidOperationException("Wiki scope setup must not run") : Task.FromResult<ProjectConfig?>(new ProjectConfig
            {
                ProjectId = "P1",
                ProjectName = "Pool Ops",
                NyraKbNames = "KB-One"
            });

        public Task<ProjectConfig?> UpdateProjectAsync(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
            => Task.FromResult<ProjectConfig?>(projectConfig);
    }

    private sealed class StubKnowledgeEnrichment : IKnowledgeEnrichmentService
    {
        public bool Forbid { get; init; }
        public Task<EnrichedTicketKnowledge> EnrichAsync(TicketKnowledge ticketKnowledge)
            => Forbid ? throw new InvalidOperationException("Incident enrichment must not run") : Task.FromResult(new EnrichedTicketKnowledge
            {
                TicketKnowledge = ticketKnowledge,
                EmbeddingText = ticketKnowledge.Problem
            });
    }

    private sealed class StubFailurePatternAgent : IFailurePatternAgent
    {
        public bool Forbid { get; init; }
        public Task<string> ExtractFailurePatternAsync(string system, string problem, string rootCause)
            => Forbid ? throw new InvalidOperationException("Failure pattern generation must not run") : Task.FromResult("{}");
    }

    private sealed class StubFailurePatternRepository : IFailurePatternRepository
    {
        public Task InsertFailurePattern(FailurePattern failurePattern, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FailurePattern>> GetPatternsBySystem(string system, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FailurePattern>>([]);

        public Task<IReadOnlyList<FailureTypeFrequency>> GetMostFrequentFailureTypes(int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FailureTypeFrequency>>([]);

        public Task<IReadOnlyList<ComponentFrequency>> GetMostProblematicComponents(int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ComponentFrequency>>([]);

        public Task<IReadOnlyList<SystemIncidentFrequency>> GetSystemsWithRepeatedIncidents(int minimumIncidentCount = 2, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SystemIncidentFrequency>>([]);
    }

    private sealed class StubFeedbackRepository : IFeedbackRepository
    {
        public bool Forbid { get; init; }
        public Task<int> AddAsync(FeedbackLog feedback, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<int> AddApplicationFeedbackAsync(ApplicationFeedbackLog feedback, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<ApplicationFeedbackInsights> GetApplicationFeedbackInsightsAsync(int rangeDays = 30, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Test stub: feedback insights are not expected in this test.");

        public Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult(0d);

        public Task<IReadOnlyDictionary<string, FeedbackEvidence>> GetFeedbackEvidence(
            IReadOnlyCollection<string> ticketIds,
            CancellationToken cancellationToken = default)
            => Forbid ? throw new InvalidOperationException("Incident feedback reads must not run") : Task.FromResult<IReadOnlyDictionary<string, FeedbackEvidence>>(new Dictionary<string, FeedbackEvidence>());

        public Task<IReadOnlyDictionary<string, double>> GetFeedbackScores(
            IReadOnlyCollection<string> ticketIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    }

    private sealed class StubValidatedResolutionRepository : IValidatedResolutionRepository
    {
        public bool Forbid { get; init; }
        public Task<int> AddAsync(ValidatedResolution resolution, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>>> GetByTicketIdsAsync(
            IReadOnlyCollection<string> ticketIds,
            CancellationToken cancellationToken = default)
            => Forbid ? throw new InvalidOperationException("Validated incident reads must not run") : Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ValidatedResolution>>>(
                new Dictionary<string, IReadOnlyList<ValidatedResolution>>());
    }
}
