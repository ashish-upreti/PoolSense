using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace PoolSense.Api.Services;

/// <summary>
/// Provides a simple abstraction for prompt-based large language model interactions.
/// </summary>
public interface ILLMService
{
    /// <summary>
    /// Sends a prompt to the configured language model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt text to submit.</param>
    /// <returns>The model response.</returns>
    Task<string> GetResponseAsync(string prompt);
    Task<string> GetBoundedResponseAsync(string prompt, int maxOutputTokens, CancellationToken token) =>
        throw new NotSupportedException("This provider does not support bounded generation.");
}

/// <summary>
/// Uses Semantic Kernel to execute prompt-based requests against the configured model.
/// </summary>
public class LLMService : ILLMService
{
    private readonly Kernel _kernel;
    private readonly ILlmTokenUsageRepository _tokenUsageRepository;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;

    /// <summary>
    /// Initializes a new instance of the <see cref="LLMService"/> class.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance used to invoke prompts.</param>
    public LLMService(
        Kernel kernel,
        ILlmTokenUsageRepository tokenUsageRepository,
        IOptionsMonitor<NyraSettings> nyraSettings)
    {
        _kernel = kernel;
        _tokenUsageRepository = tokenUsageRepository;
        _nyraSettings = nyraSettings;
    }

    /// <summary>
    /// Sends a prompt to the configured language model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt text to submit.</param>
    /// <returns>The model response.</returns>
    public async Task<string> GetResponseAsync(string prompt)
    {
        return await SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(
            _kernel,
            prompt,
            new KernelArguments(),
            _tokenUsageRepository,
            "GenericPrompt",
            _nyraSettings.CurrentValue.Model);
    }

    public Task<string> GetBoundedResponseAsync(string prompt, int maxOutputTokens, CancellationToken token)
    {
        if (maxOutputTokens is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(_kernel, prompt,
            new KernelArguments(new OpenAIPromptExecutionSettings { MaxTokens = maxOutputTokens, Temperature = 0 }),
            _tokenUsageRepository, "CodeKnowledgePreparation", _nyraSettings.CurrentValue.Model, token);
    }
}