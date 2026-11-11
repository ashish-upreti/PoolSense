using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;
using PoolSense.Api.Models;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class QueryCategorizationPolicyTests
{
    [Fact]
    public async Task ConfiguredClassifier_IsInvokedWithBoundedContextAndConfiguredModelService()
    {
        var chat = new ClassifierChat();
        var builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedSingleton<IChatCompletionService>(QueryCategorizationAgent.CategorizationServiceId, chat);
        var usage = new Usage();
        var settings = new Settings(new NyraSettings { CategorizationModel = "configured-category-model" });
        var agent = new QueryCategorizationAgent(builder.Build(), usage, settings);
        var context = new TicketConversationContext([new("user", "Publication process"), new("assistant", "Prior hypothesis, not current evidence")]);
        var response = await agent.CategorizeQueryAsync("show that as a flow", "show that as a flow", context, default);
        Assert.Contains("Info", response);
        Assert.Equal(1, chat.Calls);
        Assert.Contains("Publication process", chat.Prompt);
        Assert.Contains("untrusted context", chat.Prompt);
        Assert.Contains("authorized code", chat.Prompt);
        Assert.Contains("visualizationPlan", chat.Prompt);
        Assert.Contains("Strict supported kinds: none, relationship, process, table, bar", chat.Prompt);
        Assert.Contains("Can you create a image to explain how the multiple project applications are related", chat.Prompt);
        Assert.Contains("How apps talk", chat.Prompt);
        Assert.Contains("'Draw that'", chat.Prompt);
        Assert.Contains("Do not change source routing", chat.Prompt);
        Assert.Equal("configured-category-model", Assert.Single(usage.Records).Model);
        Assert.Equal("QueryCategorization", usage.Records[0].OperationName);
    }

    [Fact]
    public async Task FinalResolutionAndSingleRepair_ReceiveActualTypedPlanAndConfiguredModel()
    {
        var chat = new ClassifierChat();
        var builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedSingleton<IChatCompletionService>(ResolutionAgent.ResolutionServiceId, chat);
        var usage = new Usage();
        var agent = new ResolutionAgent(builder.Build(), usage, new Settings(new NyraSettings { ResolutionModel = "configured-resolution-model" }));
        var plan = new VisualizationPlan(true, "relationship", true, "Explain only established application links.");
        await agent.GenerateResolutionAsync("Draw that", "Related apps", [], [], [], TicketConversationContext.Empty, "",
            ResolutionAnswerIntent.Explanation, plan);
        Assert.Contains("\"kind\":\"relationship\"", System.Net.WebUtility.HtmlDecode(chat.Prompt));
        Assert.Contains(plan.Purpose, chat.Prompt);
        Assert.Contains("do NOT join unrelated applications", chat.Prompt);
        Assert.Contains("visualizationTable", chat.Prompt);
        Assert.Contains("visualizationChart", chat.Prompt);
        Assert.Contains("Never claim an accompanying/below diagram", chat.Prompt);
        Assert.Contains("Explanation", chat.Prompt);
        await agent.RepairVisualizationAsync("Draw related apps", plan, [new("C1", "App links", "A calls B", "")], default);
        Assert.Equal(2, chat.Calls);
        Assert.Contains("\"kind\":\"relationship\"", System.Net.WebUtility.HtmlDecode(chat.Prompt));
        Assert.Contains("A calls B", chat.Prompt);
        var execution = Assert.IsType<Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings>(chat.Settings);
        Assert.Equal(1500, execution.MaxTokens);
        Assert.All(usage.Records, record => Assert.Equal("configured-resolution-model", record.Model));
    }

    [Fact]
    public async Task ClassificationAndRepair_DoNotRepeatProviderCallsOnDeploymentFailure()
    {
        var chat = new ClassifierChat { Failure = new InvalidOperationException("DeploymentNotFound") };
        var builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedSingleton<IChatCompletionService>(QueryCategorizationAgent.CategorizationServiceId, chat);
        builder.Services.AddKeyedSingleton<IChatCompletionService>(ResolutionAgent.ResolutionServiceId, chat);
        var settings = new Settings(new NyraSettings { CategorizationModel = "category", ResolutionModel = "resolution" });
        var usage = new Usage();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new QueryCategorizationAgent(builder.Build(), usage, settings).CategorizeQueryAsync("draw related apps", ""));
        Assert.Equal(1, chat.Calls);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ResolutionAgent(builder.Build(), usage, settings).RepairVisualizationAsync("draw related apps",
                new(true, "relationship", true, "Source-backed links."), [], default));
        Assert.Equal(2, chat.Calls);
        Assert.All(usage.Records, record => Assert.False(record.Success));
    }

    private sealed class ClassifierChat : IChatCompletionService
    {
        public int Calls;
        public string Prompt = "";
        public PromptExecutionSettings? Settings;
        public Exception? Failure;
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();
        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Settings = executionSettings;
            if (Failure is not null) throw Failure;
            Prompt = string.Join("\n", chatHistory.Select(message => message.Content));
            IReadOnlyList<ChatMessageContent> result = [new(AuthorRole.Assistant, """{"category":"Info","reasoning":"Normal process explanation"}""")];
            return Task.FromResult(result);
        }
        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
    private sealed class Usage : ILlmTokenUsageRepository
    {
        public List<LlmTokenUsageRecord> Records { get; } = [];
        public Task LogAsync(LlmTokenUsageRecord record, CancellationToken cancellationToken = default) { Records.Add(record); return Task.CompletedTask; }
    }
    private sealed class Settings(NyraSettings value) : IOptionsMonitor<NyraSettings>
    {
        public NyraSettings CurrentValue => value;
        public NyraSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<NyraSettings, string?> listener) => null;
    }
}
