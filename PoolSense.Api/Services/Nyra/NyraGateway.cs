using System.ClientModel.Primitives;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;

namespace PoolSense.Api.Services.Nyra;

public static class NyraGateway
{
    private const string NoProxyPattern = @".*\.mfgint\.intel\.com";
    private static readonly Lazy<NyraTokenProvider> SharedProvider = new(
        () => new NyraTokenProvider(CreateTokenHttpClient()), LazyThreadSafetyMode.ExecutionAndPublication);
    internal static NyraTokenProvider TokenProvider => SharedProvider.Value;

    public static AzureOpenAIClient Create(
        string? apiKey,
        Uri endpoint,
        string? tokenUrl = null)
    {
        return CreateAsync(apiKey, endpoint, tokenUrl).GetAwaiter().GetResult();
    }

    public static AzureOpenAIClient Create(
        string? apiKey,
        Uri endpoint,
        string? tokenUrl,
        string? clientId,
        string? clientSecret,
        string? audience,
        string? issuer = null,
        string? scope = null)
    {
        return CreateAsync(apiKey, endpoint, tokenUrl, clientId, clientSecret, audience, issuer, scope).GetAwaiter().GetResult();
    }

    public static async Task<AzureOpenAIClient> CreateAsync(
        string? apiKey,
        Uri endpoint,
        string? tokenUrl = null,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        string? issuer = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        await FetchTokenAsync(tokenUrl, issuer, clientId, clientSecret, audience, scope, cancellationToken).ConfigureAwait(false);
        var options = new AzureOpenAIClientOptions();

        options.AddPolicy(new NyraAuthPolicy(
            ct => FetchTokenAsync(tokenUrl, issuer, clientId, clientSecret, audience, scope, ct),
            () => InvalidateToken(tokenUrl, issuer, clientId, clientSecret, audience, scope),
            apiKey), PipelinePosition.PerCall);

        return new AzureOpenAIClient(endpoint, new AzureKeyCredential("ignored_by_gateway"), options);
    }

    internal static async Task<string> FetchTokenAsync(
        string? tokenUrl = null,
        string? issuer = null,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        return await SharedProvider.Value.GetTokenAsync(tokenUrl, issuer, clientId, clientSecret, audience, scope, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static void InvalidateToken(string? tokenUrl, string? issuer, string? clientId, string? clientSecret,
        string? audience, string? scope) =>
        SharedProvider.Value.InvalidateToken(tokenUrl, issuer, clientId, clientSecret, audience, scope);

    private static HttpClient CreateTokenHttpClient()
    {
        var bypassList = new List<string> { NoProxyPattern };

        var envNoProxy = Environment.GetEnvironmentVariable("NO_PROXY")
            ?? Environment.GetEnvironmentVariable("no_proxy");
        if (!string.IsNullOrEmpty(envNoProxy))
        {
            bypassList.AddRange(
                envNoProxy.Split(',')
                    .Select(host => Regex.Escape(host.Trim()))
                    .Where(host => host.Length > 0));
        }

        var proxyUrl = Environment.GetEnvironmentVariable("HTTPS_PROXY")
            ?? Environment.GetEnvironmentVariable("HTTP_PROXY");

        IWebProxy proxy = proxyUrl is { Length: > 0 }
            ? new WebProxy(proxyUrl, true, bypassList.ToArray())
            : new WebProxy { BypassList = bypassList.ToArray(), BypassProxyOnLocal = true };

        var handler = new HttpClientHandler { UseProxy = true, Proxy = proxy };

        var caBundle = Environment.GetEnvironmentVariable("NYRA_CA_BUNDLE");
        if (!string.IsNullOrEmpty(caBundle) && File.Exists(caBundle))
        {
            var caCert = X509CertificateLoader.LoadCertificateFromFile(caBundle);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
            {
                if (chain is null || cert is null)
                {
                    return false;
                }

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCert);
                return chain.Build(cert);
            };
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}