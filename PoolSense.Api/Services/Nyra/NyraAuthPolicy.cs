using System.ClientModel.Primitives;

namespace PoolSense.Api.Services.Nyra;

internal sealed class NyraAuthPolicy : PipelinePolicy
{
    private readonly string _bearerToken;
    private readonly string? _apiKey;

    internal NyraAuthPolicy(string bearerToken, string? apiKey)
    {
        _bearerToken = bearerToken;
        _apiKey = apiKey;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("Authorization", $"Bearer {_bearerToken}");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            message.Request.Headers.Set("NYRA-API-KEY", _apiKey);
        }

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("Authorization", $"Bearer {_bearerToken}");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            message.Request.Headers.Set("NYRA-API-KEY", _apiKey);
        }

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}