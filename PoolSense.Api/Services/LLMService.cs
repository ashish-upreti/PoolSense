using Microsoft.SemanticKernel;

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
}

/// <summary>
/// Uses Semantic Kernel to execute prompt-based requests against the configured model.
/// </summary>
public class LLMService : ILLMService
{
    private readonly Kernel _kernel;

    /// <summary>
    /// Initializes a new instance of the <see cref="LLMService"/> class.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance used to invoke prompts.</param>
    public LLMService(Kernel kernel)
    {
        _kernel = kernel;
    }

    /// <summary>
    /// Sends a prompt to the configured language model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt text to submit.</param>
    /// <returns>The model response.</returns>
    public async Task<string> GetResponseAsync(string prompt)
    {
        var result = await _kernel.InvokePromptAsync(prompt);
        return result.ToString();
    }
}