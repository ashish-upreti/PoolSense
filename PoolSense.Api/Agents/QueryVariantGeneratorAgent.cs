using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;

namespace PoolSense.Api.Agents;

public interface IQueryVariantGeneratorAgent
{
    Task<IReadOnlyList<string>> GenerateQueryVariantsAsync(string problemDescription, string rootCause, string resolution);
}

public class QueryVariantGeneratorAgent : IQueryVariantGeneratorAgent
{
    private readonly Kernel _kernel;
    private readonly ILlmTokenUsageRepository _tokenUsageRepository;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;

    public QueryVariantGeneratorAgent(
        Kernel kernel,
        ILlmTokenUsageRepository tokenUsageRepository,
        IOptionsMonitor<NyraSettings> nyraSettings)
    {
        _kernel = kernel;
        _tokenUsageRepository = tokenUsageRepository;
        _nyraSettings = nyraSettings;
    }

    public async Task<IReadOnlyList<string>> GenerateQueryVariantsAsync(string problemDescription, string rootCause, string resolution)
    {
        const string prompt = @"
You are an AI assistant that generates alternative search queries for engineering support tickets.

Inputs:
- Problem description
- Root cause
- Resolution

Task:
Generate 5 alternative search queries that engineers might use to describe or search for the same issue.

Problem Description:
{{$problemDescription}}

Root Cause:
{{$rootCause}}

Resolution:
{{$resolution}}

Return only valid JSON as an array of 5 strings.
Example:
[
  ""sql connection pool exhausted"",
  ""database timeout api"",
  ""api failing due to db connections"",
  ""sql timeout during api request"",
  ""connection pool limit reached""
]

Rules:
- Keep each query short and natural.
- Use different wording across the 5 queries.
- Focus on how engineers would actually search for the issue.
- Do not include markdown, comments, numbering, or extra text.
";

        var arguments = new KernelArguments
        {
            { "problemDescription", problemDescription },
            { "rootCause", rootCause },
            { "resolution", resolution }
        };

        var content = await SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(
            _kernel,
            prompt,
            arguments,
            _tokenUsageRepository,
            "QueryVariantGeneration",
            _nyraSettings.CurrentValue.Model);
        var queries = JsonSerializer.Deserialize<List<string>>(AiJsonResponseSanitizer.Normalize(content));
        return queries is { Count: > 0 } ? queries : [];
    }
}
