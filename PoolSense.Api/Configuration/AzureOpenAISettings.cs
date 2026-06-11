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