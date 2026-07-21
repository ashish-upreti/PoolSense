using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using PoolSense.Api.Configuration;
using PoolSense.Api.Services.Nyra;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class NyraConfigurationTests
{
    [Fact]
    public void NyraSettings_BindsGatewayClientAndModelConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nyra:ApiKey"] = "test-key",
                ["Nyra:GatewayEndpoint"] = "https://nyra.test/api/nyra-gateway/llm-service/",
                ["Nyra:TokenUrl"] = "https://sso.test/as/token.oauth2",
                ["Nyra:Model"] = "gpt-5.4",
                ["Nyra:EmbeddingGenerateUrl"] = "https://nyra.test/api/nyra-gateway/embedding-service/v1/embeddings/generate",
                ["Nyra:EmbeddingModel"] = "text-embedding-3-small",
                ["Nyra:EmbeddingSubscriptionType"] = "azure",
                ["Nyra:EmbeddingApiVersion"] = "2025-01-01-preview"
            })
            .Build();

        var settings = configuration.GetSection("Nyra").Get<NyraSettings>();

        Assert.NotNull(settings);
        Assert.Equal("test-key", settings.ApiKey);
        Assert.Equal("https://nyra.test/api/nyra-gateway/llm-service/", settings.GatewayEndpoint);
        Assert.Equal("https://sso.test/as/token.oauth2", settings.TokenUrl);
        Assert.Equal("gpt-5.4", settings.Model);
        Assert.Equal("https://nyra.test/api/nyra-gateway/embedding-service/v1/embeddings/generate", settings.EmbeddingGenerateUrl);
        Assert.Equal("text-embedding-3-small", settings.EmbeddingModel);
        Assert.Equal("azure", settings.EmbeddingSubscriptionType);
        Assert.Equal("2025-01-01-preview", settings.EmbeddingApiVersion);
    }

    [Fact]
    public void NyraGatewayReference_IsAvailableToPoolSenseApi()
    {
        Assert.Equal("PoolSense.Api.Services.Nyra", typeof(NyraGateway).Namespace);
    }

    [Fact]
    public async Task NyraConnection_ShouldAnswerGenericLlmQuery()
    {
        var settings = CreateConfiguredNyraSettings();
        var kernel = await CreateKernelAsync(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var response = await kernel.InvokePromptAsync(
            "Answer with only the number: what is 2 + 2?",
            cancellationToken: timeout.Token);
        var content = response.ToString();

        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("4", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Error", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NyraEmbeddingService_ShouldGenerateEmbeddingsForGenericTexts()
    {
        var settings = CreateConfiguredNyraSettings();
        var embeddingGenerator = CreateEmbeddingGenerator(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var embeddings = await embeddingGenerator.GenerateAsync(
            ["first text", "second text"],
            new EmbeddingGenerationOptions { Dimensions = 1536 },
            cancellationToken: timeout.Token);

        Assert.Equal(2, embeddings.Count);
        Assert.All(embeddings, embedding =>
        {
            var vector = embedding.Vector.ToArray();
            Assert.NotEmpty(vector);
            Assert.Contains(vector, value => value != 0);
        });
    }

    private static async Task<Kernel> CreateKernelAsync(NyraSettings settings)
    {
        var endpoint = new Uri(GetRequiredOption(settings.GatewayEndpoint, "Nyra:GatewayEndpoint"));
        var nyraClient = string.IsNullOrWhiteSpace(settings.TokenUrl)
            ? await NyraGateway.CreateAsync(settings.ApiKey, endpoint)
            : await NyraGateway.CreateAsync(settings.ApiKey, endpoint, settings.TokenUrl);

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddAzureOpenAIChatCompletion(
            deploymentName: GetRequiredOption(settings.Model, "Nyra:Model"),
            azureOpenAIClient: nyraClient);

#pragma warning disable SKEXP0010
        kernelBuilder.AddAzureOpenAIEmbeddingGenerator(
            deploymentName: GetRequiredOption(settings.EmbeddingModel, "Nyra:EmbeddingModel"),
            azureOpenAIClient: nyraClient);
#pragma warning restore SKEXP0010

        return kernelBuilder.Build();
    }

    private static NyraEmbeddingGenerator CreateEmbeddingGenerator(NyraSettings settings)
    {
        return new NyraEmbeddingGenerator(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, new StaticOptionsMonitor<NyraSettings>(settings));
    }

    private static NyraSettings CreateConfiguredNyraSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var settings = configuration.GetSection("Nyra").Get<NyraSettings>()
            ?? throw new InvalidOperationException("Nyra configuration section is required.");

        settings.ApiKey = GetRequiredOption(settings.ApiKey, "Nyra:ApiKey");
        settings.GatewayEndpoint = GetRequiredOption(settings.GatewayEndpoint, "Nyra:GatewayEndpoint");
        settings.Model = GetRequiredOption(settings.Model, "Nyra:Model");
        settings.EmbeddingModel = GetRequiredOption(settings.EmbeddingModel, "Nyra:EmbeddingModel");
        settings.EmbeddingApiVersion = GetRequiredOption(settings.EmbeddingApiVersion, "Nyra:EmbeddingApiVersion");

        return settings;
    }

    private static string GetRequiredOption(string? value, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{configurationKey} configuration is required.");
        }

        return value;
    }

    private sealed class StaticOptionsMonitor<TOptions> : Microsoft.Extensions.Options.IOptionsMonitor<TOptions>
    {
        public StaticOptionsMonitor(TOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}