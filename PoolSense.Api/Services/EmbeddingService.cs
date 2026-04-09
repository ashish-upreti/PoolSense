using Microsoft.Extensions.AI;
using PoolSense.Api.Agents;

namespace PoolSense.Api.Services;

/// <summary>
/// Generates vector embeddings for text used by similarity search.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for the provided text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <returns>The embedding vector.</returns>
    Task<float[]> GenerateEmbedding(string text);
}

/// <summary>
/// Uses the configured embedding generator to create vector representations of text.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingService"/> class.
    /// </summary>
    /// <param name="embeddingGenerator">The embedding generator used to create vectors.</param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Generates an embedding vector for the provided text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <returns>The embedding vector.</returns>
    public async Task<float[]> GenerateEmbedding(string text)
    {
        _logger.LogInformation("Generating embedding for text length {TextLength}.", text?.Length ?? 0);
        var options = new EmbeddingGenerationOptions { Dimensions = 1536 };
        var embedding = await SemanticKernelRetryHelper.ExecuteWithDeploymentRetryAsync(() => _embeddingGenerator.GenerateAsync(text, options));
        var vector = embedding.Vector.ToArray();
        _logger.LogInformation("Embedding generated with vector size {VectorSize}.", vector.Length);
        return vector;
    }
}
