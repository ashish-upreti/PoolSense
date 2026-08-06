using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PoolSense.Api.Configuration;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class NyraProdClientConfigurationTests
{
    private const string DefaultIssuer = "https://sso.rf3prod.mfgint.intel.com";
    private const string DefaultAudience = "nyra-web-core-api-rf3prod";
    private const string DefaultClientId = "poolsense-nyra-app-rf3prod";
    private const string DefaultClientSecret = "";
    private const string DefaultScope = "openid";
    private const string DefaultResponsesUrl = "https://imi.rf3prod.mfgint.intel.com/api/nyra-gateway/llm-service/openai/responses?api-version=2025-03-01-preview";
    private const string DefaultLlmModel = "gpt-5.4";
    private const string DefaultEmbeddingGenerateUrl = "https://imi.rf3prod.mfgint.intel.com/api/nyra-gateway/embedding-service/v1/embeddings/generate";
    private const string DefaultEmbeddingModel = "text-embedding-3-large";
    private const string DefaultEmbeddingSubscriptionType = "azure";
    private const string DefaultEmbeddingApiVersion = "2025-01-01-preview";

    [Fact]
    public void NyraProdClientSecretSettings_BindExpectedDefaults()
    {
        var settings = CreateSettings(requireSecret: false);

        Assert.NotNull(settings);
        Assert.Equal(DefaultIssuer, settings.Issuer);
        Assert.Equal(DefaultAudience, settings.Audience);
        Assert.Equal(DefaultClientId, settings.ClientId);
        Assert.Equal(DefaultResponsesUrl, settings.ResponsesUrl);
        Assert.Equal(DefaultLlmModel, settings.LlmModel);
        Assert.Equal(DefaultEmbeddingGenerateUrl, settings.EmbeddingGenerateUrl);
        Assert.Equal(DefaultEmbeddingModel, settings.EmbeddingModel);
        Assert.Equal(DefaultEmbeddingSubscriptionType, settings.EmbeddingSubscriptionType);
        Assert.Equal(DefaultEmbeddingApiVersion, settings.EmbeddingApiVersion);
    }

    [Fact]
    public async Task NyraProdClientCredentials_ShouldCallResponsesEndpoint()
    {
        var settings = CreateSettings(requireSecret: true);
        if (settings is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var httpClient = CreateHttpClient();
        var accessToken = await RequestAccessTokenAsync(httpClient, settings, timeout.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ResponsesUrl);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var requestBody = JsonSerializer.Serialize(new
        {
            model = settings.LlmModel,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "Answer with only the number: what is 2 + 2?"
                        }
                    }
                }
            },
            instructions = "You are a helpful assistant.",
            stream = true
        });

        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        Assert.True(response.IsSuccessStatusCode, $"Responses endpoint returned {(int)response.StatusCode}: {body}");

        var answer = ExtractStreamingResponseText(body);
        Assert.False(string.IsNullOrWhiteSpace(answer), body);
        Assert.Contains("4", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NyraProdClientCredentials_ShouldGenerateEmbedding()
    {
        var settings = CreateSettings(requireSecret: true);
        if (settings is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var httpClient = CreateHttpClient();
        var accessToken = await RequestAccessTokenAsync(httpClient, settings, timeout.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.EmbeddingGenerateUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            model = settings.EmbeddingModel,
            texts = new[] { "PoolSense NYRA prod client credential embedding smoke test." },
            subscription_type = settings.EmbeddingSubscriptionType,
            api_version = settings.EmbeddingApiVersion
        });

        using var response = await httpClient.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        Assert.True(response.IsSuccessStatusCode, $"Embedding endpoint returned {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        var vectors = new List<float[]>();
        CollectEmbeddingVectors(json.RootElement, vectors);

        Assert.NotEmpty(vectors);
        Assert.NotEmpty(vectors[0]);
        Assert.Contains(vectors[0], value => value != 0);
    }

    private static NyraClientTestSettings? CreateSettings(bool requireSecret)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var nyraSettings = configuration.GetSection("Nyra").Get<NyraSettings>() ?? new NyraSettings();
        nyraSettings.ActiveProfile = FirstNonEmpty(
            configuration["NYRA_PROD_PROFILE"],
            configuration["NyraProd:ActiveProfile"],
            "production");
        NyraSettingsResolver.ApplyActiveProfile(nyraSettings);

        var settings = new NyraClientTestSettings(
            Issuer: FirstNonEmpty(configuration["NYRA_PROD_ISSUER"], nyraSettings.Issuer, DefaultIssuer),
            TokenUrl: FirstNonEmpty(configuration["NYRA_PROD_TOKEN_URL"], nyraSettings.TokenUrl),
            Audience: FirstNonEmpty(configuration["NYRA_PROD_AUDIENCE"], nyraSettings.Audience, DefaultAudience),
            ClientId: FirstNonEmpty(configuration["NYRA_PROD_CLIENT_ID"], nyraSettings.ClientId, DefaultClientId),
            ClientSecret: FirstNonEmpty(configuration["NYRA_PROD_CLIENT_SECRET"], nyraSettings.ClientSecret, DefaultClientSecret),
            Scope: FirstNonEmpty(configuration["NYRA_PROD_SCOPE"], nyraSettings.Scope, DefaultScope),
            ResponsesUrl: FirstNonEmpty(configuration["NYRA_PROD_RESPONSES_URL"], nyraSettings.ResponsesUrl, DefaultResponsesUrl),
            LlmModel: FirstNonEmpty(configuration["NYRA_PROD_LLM_MODEL"], nyraSettings.Model, DefaultLlmModel),
            EmbeddingGenerateUrl: FirstNonEmpty(configuration["NYRA_PROD_EMBEDDING_GENERATE_URL"], nyraSettings.EmbeddingGenerateUrl, DefaultEmbeddingGenerateUrl),
            EmbeddingModel: FirstNonEmpty(configuration["NYRA_PROD_EMBEDDING_MODEL"], nyraSettings.EmbeddingModel, DefaultEmbeddingModel),
            EmbeddingSubscriptionType: FirstNonEmpty(configuration["NYRA_PROD_EMBEDDING_SUBSCRIPTION_TYPE"], nyraSettings.EmbeddingSubscriptionType, DefaultEmbeddingSubscriptionType),
            EmbeddingApiVersion: FirstNonEmpty(configuration["NYRA_PROD_EMBEDDING_API_VERSION"], nyraSettings.EmbeddingApiVersion, DefaultEmbeddingApiVersion));

        if (requireSecret && string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return null;
        }

        return settings;
    }

    private static async Task<string> RequestAccessTokenAsync(
        HttpClient httpClient,
        NyraClientTestSettings settings,
        CancellationToken cancellationToken)
    {
        var tokenUrl = string.IsNullOrWhiteSpace(settings.TokenUrl)
            ? await ResolveTokenUrlAsync(httpClient, settings.Issuer, cancellationToken)
            : settings.TokenUrl;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["scope"] = settings.Scope,
            ["audience"] = settings.Audience,
        });

        using var response = await httpClient.PostAsync(tokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"Token endpoint returned {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("access_token", out var tokenProperty), body);

        var accessToken = tokenProperty.GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken), body);
        return accessToken;
    }

    private static async Task<string> ResolveTokenUrlAsync(
        HttpClient httpClient,
        string issuer,
        CancellationToken cancellationToken)
    {
        var openIdConfigurationUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        using var response = await httpClient.GetAsync(openIdConfigurationUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"OIDC configuration endpoint returned {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("token_endpoint", out var tokenEndpointProperty), body);

        var tokenEndpoint = tokenEndpointProperty.GetString();
        Assert.False(string.IsNullOrWhiteSpace(tokenEndpoint), body);
        return tokenEndpoint;
    }

    private static string ExtractStreamingResponseText(string body)
    {
        var text = new StringBuilder();

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data: ".Length..];
            if (payload == "[DONE]")
            {
                break;
            }

            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("type", out var type)
                && type.GetString() is "response.output_text.delta" or "response.output_text.done"
                && json.RootElement.TryGetProperty(type.GetString() == "response.output_text.delta" ? "delta" : "text", out var value))
            {
                text.Append(value.GetString());
            }
        }

        return text.ToString();
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

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(90) };

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed record NyraClientTestSettings(
        string Issuer,
        string TokenUrl,
        string Audience,
        string ClientId,
        string ClientSecret,
        string Scope,
        string ResponsesUrl,
        string LlmModel,
        string EmbeddingGenerateUrl,
        string EmbeddingModel,
        string EmbeddingSubscriptionType,
        string EmbeddingApiVersion);
}
