using System.ClientModel.Primitives;

namespace PoolSense.Api.Services.Nyra;

internal sealed class NyraAuthPolicy : PipelinePolicy
{
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly Action _invalidateToken;
    private readonly string? _apiKey;

    internal NyraAuthPolicy(
        Func<CancellationToken, Task<string>> tokenProvider,
        Action invalidateToken,
        string? apiKey)
    {
        _tokenProvider = tokenProvider;
        _invalidateToken = invalidateToken;
        _apiKey = apiKey;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = _tokenProvider(message.CancellationToken).GetAwaiter().GetResult();
        SetHeaders(message, token);
        ProcessNext(message, pipeline, currentIndex);
        InvalidateOnUnauthorized(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = await _tokenProvider(message.CancellationToken).ConfigureAwait(false);
        SetHeaders(message, token);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        InvalidateOnUnauthorized(message);
    }

    private void SetHeaders(PipelineMessage message, string token)
    {
        message.Request.Headers.Set("Authorization", $"Bearer {token}");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            message.Request.Headers.Set("NYRA-API-KEY", _apiKey);
    }

    private void InvalidateOnUnauthorized(PipelineMessage message)
    {
        if (message.Response?.Status == 401)
            _invalidateToken();
    }
}
