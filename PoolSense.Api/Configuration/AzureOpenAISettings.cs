namespace PoolSense.Api.Configuration;

public class AiSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string ImageApiVersion { get; set; } = string.Empty;
    public AiHttpSettings Http { get; set; } = new();
    public AiModelSettings Models { get; set; } = new();
}

public class AiHttpSettings
{
    public bool AllowCertificateRevocationUnknown { get; set; }
}

public class AiModelSettings
{
    public string Chat { get; set; } = string.Empty;
    public string Embeddings { get; set; } = string.Empty;
}

public class NyraSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string GatewayEndpoint { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string EmbeddingEndpoint { get; set; } = string.Empty;
    public string EmbeddingGenerateUrl { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string EmbeddingSubscriptionType { get; set; } = string.Empty;
    public string EmbeddingApiVersion { get; set; } = string.Empty;
}