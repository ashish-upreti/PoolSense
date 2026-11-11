using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolSense.Api.Services;

/// <summary>
/// Generates vector embeddings for text used by similarity search.
/// </summary>
public interface IEmbeddingService
{
    string Profile => string.Empty;
    string QueryCacheProfile => Profile;
    string PreviousProfile => string.Empty;

    Task<float[]> GenerateEmbedding(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GenerateEmbedding(text).WaitAsync(cancellationToken);
    }

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
    public string Profile
    {
        get
        {
            var settings = _nyraSettings.CurrentValue;
            var identity = JsonSerializer.Serialize(new
            {
                settings.EmbeddingModel,
                Dimensions = 1536,
                settings.GatewayEndpoint,
                settings.EmbeddingEndpoint,
                settings.EmbeddingGenerateUrl,
                settings.EmbeddingApiVersion,
                settings.EmbeddingSubscriptionType,
                settings.ActiveProfile
            });
            return "nyra-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }

    public string QueryCacheProfile
    {
        get
        {
            var credentials = ResolveSettings(_nyraSettings.CurrentValue);
            var credentialIdentity = JsonSerializer.Serialize(new
            {
                credentials.AuthMode,
                credentials.ClientId,
                credentials.ClientSecret,
                credentials.Issuer,
                credentials.TokenUrl,
                credentials.Audience,
                credentials.Scope,
                ApiKey = NyraSettingsResolver.GetApiKeyHeaderValue(credentials)
            });
            var identity = JsonSerializer.Serialize(new
            {
                Profile,
                Credentials = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialIdentity)))
            });
            return "nyra-query-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }

    // Reads vectors written by the brief credential-bound persisted-profile format without changing their identity.
    public string PreviousProfile
    {
        get
        {
            var settings = ResolveSettings(_nyraSettings.CurrentValue);
            var credentialIdentity = JsonSerializer.Serialize(new
            {
                settings.AuthMode,
                settings.ClientId,
                settings.ClientSecret,
                settings.Issuer,
                settings.TokenUrl,
                settings.Audience,
                settings.Scope,
                ApiKey = NyraSettingsResolver.GetApiKeyHeaderValue(settings)
            });
            var identity = JsonSerializer.Serialize(new
            {
                settings.EmbeddingModel,
                Dimensions = 1536,
                settings.GatewayEndpoint,
                settings.EmbeddingEndpoint,
                settings.EmbeddingGenerateUrl,
                settings.EmbeddingApiVersion,
                settings.EmbeddingSubscriptionType,
                settings.ActiveProfile,
                Credentials = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialIdentity)))
            });
            return "nyra-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }

    private static NyraSettings ResolveSettings(NyraSettings settings)
    {
        var copy = new NyraSettings
        {
            ActiveProfile = settings.ActiveProfile,
            AuthMode = settings.AuthMode,
            ApiKey = settings.ApiKey,
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Scope = settings.Scope,
            GatewayEndpoint = settings.GatewayEndpoint,
            TokenUrl = settings.TokenUrl,
            ResponsesUrl = settings.ResponsesUrl,
            Model = settings.Model,
            CategorizationModel = settings.CategorizationModel,
            ResolutionModel = settings.ResolutionModel,
            EmbeddingEndpoint = settings.EmbeddingEndpoint,
            EmbeddingGenerateUrl = settings.EmbeddingGenerateUrl,
            EmbeddingModel = settings.EmbeddingModel,
            EmbeddingSubscriptionType = settings.EmbeddingSubscriptionType,
            EmbeddingApiVersion = settings.EmbeddingApiVersion,
            Profiles = settings.Profiles
        };
        NyraSettingsResolver.ApplyActiveProfile(copy);
        return copy;
    }

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
    public Task<float[]> GenerateEmbedding(string text) => GenerateEmbedding(text, CancellationToken.None);

    public async Task<float[]> GenerateEmbedding(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Generating embedding for text length {TextLength}.", text.Length);
        var stopwatch = Stopwatch.StartNew();
        var options = new EmbeddingGenerationOptions { Dimensions = 1536 };
        try
        {
            var embedding = await SemanticKernelRetryHelper.ExecuteWithDeploymentRetryAsync(
                () => _embeddingGenerator.GenerateAsync(text, options, cancellationToken), cancellationToken);
            var vector = embedding.Vector.ToArray();
            await TryLogUsageAsync(text, embedding, vector.Length, stopwatch, success: true, errorMessage: string.Empty);
            _logger.LogInformation("Embedding generated with vector size {VectorSize}.", vector.Length);
            return vector;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
