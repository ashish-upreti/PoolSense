using Microsoft.SemanticKernel;

namespace PoolSense.Api.Agents;

public interface IFailurePatternAgent
{
    Task<string> ExtractFailurePatternAsync(string problem, string rootCause, string resolution);
}

public class FailurePatternAgent : IFailurePatternAgent
{
    private readonly Kernel _kernel;

    public FailurePatternAgent(Kernel kernel)
    {
        _kernel = kernel;
    }

    public Task<string> ExtractFailurePatternAsync(string problem, string rootCause, string resolution)
    {
        const string prompt = @"
You are an AI system that extracts failure patterns from engineering support tickets.

Inputs:
- Problem
- Root cause
- Resolution

Task:
Analyze the inputs and identify the system, component, failure type, and resolution category.

Problem:
{{$problem}}

Root Cause:
{{$rootCause}}

Resolution:
{{$resolution}}

Return only valid JSON with this exact structure:
{
  ""system"": ""system name"",
  ""component"": ""component name"",
  ""failureType"": ""failure type"",
  ""resolutionCategory"": ""resolution category""
}

Rules:
- Keep values concise and normalized.
- Infer the most likely system and component from the provided inputs.
- Use engineering-oriented terminology.
- Do not include markdown, comments, code fences, or extra fields.
";

        var arguments = new KernelArguments
        {
            { "problem", problem },
            { "rootCause", rootCause },
            { "resolution", resolution }
        };

        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(_kernel, prompt, arguments);
    }
}
