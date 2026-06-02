namespace PoolSense.Api.Logging;

public sealed class LlmTokenUsageRecord
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string ServiceType { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string Provider { get; set; } = "AzureOpenAI";
    public string Model { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public bool IsEstimated { get; set; }
    public int InputCharacters { get; set; }
    public int OutputCharacters { get; set; }
    public int? VectorDimensions { get; set; }
    public int LatencyMs { get; set; }
    public bool Success { get; set; } = true;
    public string ErrorMessage { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
}