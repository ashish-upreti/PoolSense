using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;

namespace PoolSense.Api.Agents;

public interface IQueryCategorizationAgent
{
    Task<string> CategorizeQueryAsync(string title, string description);
}

/// <summary>
/// Triages an incoming query as an "Issue" (needs incident retrieval) or "Info" (documentation question only)
/// using a smaller, faster NYRA model ahead of the full resolution pipeline.
/// </summary>
public class QueryCategorizationAgent : IQueryCategorizationAgent
{
    public const string CategorizationServiceId = "categorization";

    private readonly Kernel _kernel;
    private readonly ILlmTokenUsageRepository _tokenUsageRepository;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;

    public QueryCategorizationAgent(
        Kernel kernel,
        ILlmTokenUsageRepository tokenUsageRepository,
        IOptionsMonitor<NyraSettings> nyraSettings)
    {
        _kernel = kernel;
        _tokenUsageRepository = tokenUsageRepository;
        _nyraSettings = nyraSettings;
    }

    public Task<string> CategorizeQueryAsync(string title, string description)
    {
        const string prompt = @"
You are a fast triage classifier for an engineering support assistant.

Classify the query into exactly one category:
- ""Issue"": the user is reporting a problem, error, failure, or something not working that needs matching against historical incidents to find a resolution.
- ""Info"": the user is asking a general question, asking how something works, or asking for a documentation/process explanation, with no reported failure that needs incident history.

Query Title:
{{$title}}

Query Description:
{{$description}}

Return only valid JSON with this exact structure:
{
  ""category"": ""Issue"" or ""Info"",
  ""reasoning"": ""One short sentence explaining the classification""
}

Do not include markdown, comments, code fences, or extra fields.
";

        var executionSettings = new PromptExecutionSettings { ServiceId = CategorizationServiceId };
        var arguments = new KernelArguments(executionSettings)
        {
            { "title", title },
            { "description", description }
        };

        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(
            _kernel,
            prompt,
            arguments,
            _tokenUsageRepository,
            "QueryCategorization",
            _nyraSettings.CurrentValue.CategorizationModel);
    }
}
