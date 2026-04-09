namespace PoolSense.Api.Configuration;

public class AiSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public AiModelSettings Models { get; set; } = new();
}

public class AiModelSettings
{
    public string Chat { get; set; } = string.Empty;
    public string Embeddings { get; set; } = string.Empty;
}