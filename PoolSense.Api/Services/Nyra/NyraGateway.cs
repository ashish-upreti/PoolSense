using System.ClientModel.Primitives;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;

namespace PoolSense.Api.Services.Nyra;

public static class NyraGateway
{
    internal const string DefaultTokenUrl = "https://sso.rf3stg.mfgint.intel.com/as/token.oauth2";
    private const string DefaultClientId = "nyra-test-client-rf3stg";
    private const string DefaultClientSecret = "rMMB74dvVUTfc1gAETHRLVgktjRbfyUDkTMUiToCFVXBCX01S3escWU2rAqfhyr0";
    private const string DefaultAudience = "nyra-web-core-api-rf3stg";
    private const string NoProxyPattern = @".*\.mfgint\.intel\.com";

    public static AzureOpenAIClient Create(
        string? apiKey,
        Uri endpoint,
        string tokenUrl = DefaultTokenUrl)
    {
        return CreateAsync(apiKey, endpoint, tokenUrl).GetAwaiter().GetResult();
    }

    public static AzureOpenAIClient Create(
        string? apiKey,
        Uri endpoint,
        string? tokenUrl,
        string? clientId,
        string? clientSecret,
        string? audience)
    {
        return CreateAsync(apiKey, endpoint, tokenUrl, clientId, clientSecret, audience).GetAwaiter().GetResult();
    }

    public static async Task<AzureOpenAIClient> CreateAsync(
        string? apiKey,
        Uri endpoint,
        string? tokenUrl = DefaultTokenUrl,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        var token = await FetchTokenAsync(tokenUrl, clientId, clientSecret, audience, cancellationToken).ConfigureAwait(false);
        var options = new AzureOpenAIClientOptions();

        options.AddPolicy(new NyraAuthPolicy(token, apiKey), PipelinePosition.PerCall);

        return new AzureOpenAIClient(endpoint, new AzureKeyCredential("ignored_by_gateway"), options);
    }

    internal static async Task<string> FetchTokenAsync(
        string? tokenUrl = DefaultTokenUrl,
        string? clientId = null,
        string? clientSecret = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateTokenHttpClient();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = FirstNonEmpty(clientId, DefaultClientId),
            ["client_secret"] = FirstNonEmpty(clientSecret, DefaultClientSecret),
            ["scope"] = "openid",
            ["audience"] = FirstNonEmpty(audience, DefaultAudience),
        };

        using var content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .PostAsync(FirstNonEmpty(tokenUrl, DefaultTokenUrl), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Token request failed: {ex.Message}", ex);
        }

        var body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("access_token", out var tokenProp)
            || tokenProp.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException("Token endpoint returned 200 but no access_token in response.");
        }

        return accessToken;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

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