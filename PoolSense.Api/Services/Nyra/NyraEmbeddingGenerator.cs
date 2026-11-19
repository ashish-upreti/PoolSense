using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;

namespace PoolSense.Api.Services.Nyra;

public sealed class NyraEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int DefaultEmbeddingDimensions = 1536;
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<NyraSettings> _settings;
    private readonly NyraTokenProvider _tokens;

    public NyraEmbeddingGenerator(HttpClient httpClient, IOptionsMonitor<NyraSettings> settings, NyraTokenProvider? tokens = null)
    {
        _httpClient = httpClient;
        _settings = settings;
        _tokens = tokens ?? NyraGateway.TokenProvider;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var texts = values.ToArray();
        if (texts.Length == 0)
        {
            return [];
        }

        var settings = _settings.CurrentValue;
        NyraSettingsResolver.ApplyActiveProfile(settings);
        var tokenUrl = string.IsNullOrWhiteSpace(settings.TokenUrl)
            ? null
            : settings.TokenUrl;
        var generateUrl = GetEmbeddingGenerateUrl(settings);
        var apiKey = NyraSettingsResolver.GetApiKeyHeaderValue(settings);
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _tokens.GetTokenAsync(tokenUrl, settings.Issuer, settings.ClientId, settings.ClientSecret,
                settings.Audience, settings.Scope, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, generateUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("NYRA-API-KEY", apiKey);

            request.Content = JsonContent.Create(new
            {
                model = GetRequiredOption(settings.EmbeddingModel, "Nyra:EmbeddingModel"),
                texts,
                subscription_type = FirstNonEmpty(settings.EmbeddingSubscriptionType, "azure"),
                api_version = GetRequiredOption(settings.EmbeddingApiVersion, "Nyra:EmbeddingApiVersion")
            });

            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt != 0)
                break;

            response.Dispose();
            response = null;
            _tokens.InvalidateToken(tokenUrl, settings.Issuer, settings.ClientId, settings.ClientSecret,
                settings.Audience, settings.Scope);
        }

        using (response)
        {
            var body = await response!.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Embedding service returned {(int)response.StatusCode}: {body}");

            using var json = JsonDocument.Parse(body);
            var vectors = new List<float[]>();
            CollectEmbeddingVectors(json.RootElement, vectors);

            if (vectors.Count < texts.Length)
                throw new InvalidOperationException($"Expected at least {texts.Length} embedding vectors in response: {body}");

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
            foreach (var vector in vectors.Take(texts.Length))
            {
                embeddings.Add(new Embedding<float>(vector)
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    ModelId = settings.EmbeddingModel
                });
            }

            return embeddings;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            var settings = _settings.CurrentValue;
            NyraSettingsResolver.ApplyActiveProfile(settings);
            return new EmbeddingGeneratorMetadata(
                providerName: "nyra",
                providerUri: new Uri(GetEmbeddingGenerateUrl(settings)),
                defaultModelId: settings.EmbeddingModel,
                defaultModelDimensions: DefaultEmbeddingDimensions);
        }

        return null;
    }

    public void Dispose()
    {
    }

    private static string GetEmbeddingGenerateUrl(NyraSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.EmbeddingGenerateUrl))
        {
            return settings.EmbeddingGenerateUrl;
        }

        if (!string.IsNullOrWhiteSpace(settings.EmbeddingEndpoint))
        {
            return settings.EmbeddingEndpoint.TrimEnd('/') + "/generate";
        }

        throw new InvalidOperationException("Nyra:EmbeddingGenerateUrl or Nyra:EmbeddingEndpoint configuration is required.");
    }

    private static void CollectEmbeddingVectors(JsonElement element, List<float[]> vectors)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = element.EnumerateArray().ToArray();
            if (values.Length > 0 && values.All(value => value.ValueKind == JsonValueKind.Number))
            {
                vectors.Add(values.Select(value => value.GetSingle()).ToArray());
                return;
            }

            foreach (var value in values)
            {
                CollectEmbeddingVectors(value, vectors);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CollectEmbeddingVectors(property.Value, vectors);
            }
        }
    }

    private static string GetRequiredOption(string? value, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{configurationKey} configuration is required.");
        }

        return value;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}