using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;
using System.Diagnostics;

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
    private readonly ILlmTokenUsageRepository _tokenUsageRepository;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingService"/> class.
    /// </summary>
    /// <param name="embeddingGenerator">The embedding generator used to create vectors.</param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILlmTokenUsageRepository tokenUsageRepository,
        IOptionsMonitor<NyraSettings> nyraSettings,
        ILogger<EmbeddingService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _tokenUsageRepository = tokenUsageRepository;
        _nyraSettings = nyraSettings;
        _logger = logger;
    }

    /// <summary>
    /// Generates an embedding vector for the provided text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <returns>The embedding vector.</returns>
    public async Task<float[]> GenerateEmbedding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _logger.LogInformation("Generating embedding for text length {TextLength}.", text.Length);
        var stopwatch = Stopwatch.StartNew();
        var options = new EmbeddingGenerationOptions { Dimensions = 1536 };
        try
        {
            var embedding = await SemanticKernelRetryHelper.ExecuteWithDeploymentRetryAsync(() => _embeddingGenerator.GenerateAsync(text, options));
            var vector = embedding.Vector.ToArray();
            await TryLogUsageAsync(text, embedding, vector.Length, stopwatch, success: true, errorMessage: string.Empty);
            _logger.LogInformation("Embedding generated with vector size {VectorSize}.", vector.Length);
            return vector;
        }
        catch (Exception ex)
        {
            await TryLogUsageAsync(
                text,
                metadata: null,
                vectorDimensions: null,
                stopwatch,
                success: false,
                errorMessage: ex.Message);
            throw;
        }
    }

    private async Task TryLogUsageAsync(
        string inputText,
        object? metadata,
        int? vectorDimensions,
        Stopwatch stopwatch,
        bool success,
        string errorMessage)
    {
        try
        {
            var usage = TokenUsageMetadataExtractor.FromMetadata(metadata, inputText);
            var model = _nyraSettings.CurrentValue.EmbeddingModel;
            await _tokenUsageRepository.LogAsync(new LlmTokenUsageRecord
            {
                ServiceType = "embedding",
                OperationName = "EmbeddingGeneration",
                Model = model,
                DeploymentName = model,
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                IsEstimated = usage.IsEstimated,
                InputCharacters = inputText.Length,
                VectorDimensions = vectorDimensions,
                LatencyMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                Success = success,
                ErrorMessage = errorMessage,
                CorrelationId = Activity.Current?.Id ?? string.Empty
            });
        }
        catch
        {
        }
    }
}
