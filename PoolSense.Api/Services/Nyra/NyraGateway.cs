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
    private const string NoProxyPattern = @".*\.mfgint\.intel\.com";

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
        var token = await FetchTokenAsync(tokenUrl, issuer, clientId, clientSecret, audience, scope, cancellationToken).ConfigureAwait(false);
        var options = new AzureOpenAIClientOptions();

        options.AddPolicy(new NyraAuthPolicy(token, apiKey), PipelinePosition.PerCall);

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
        using var httpClient = CreateTokenHttpClient();
        var resolvedTokenUrl = !string.IsNullOrWhiteSpace(tokenUrl)
            ? tokenUrl
            : await ResolveTokenUrlAsync(httpClient, issuer, cancellationToken).ConfigureAwait(false);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = GetRequiredOption(clientId, "Nyra:ClientId"),
            ["client_secret"] = GetRequiredOption(clientSecret, "Nyra:ClientSecret"),
            ["scope"] = FirstNonEmpty(scope, "openid"),
            ["audience"] = GetRequiredOption(audience, "Nyra:Audience"),
        };

        using var content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .PostAsync(resolvedTokenUrl, content, cancellationToken)
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

    private static async Task<string> ResolveTokenUrlAsync(
        HttpClient httpClient,
        string? issuer,
        CancellationToken cancellationToken)
    {
        var resolvedIssuer = GetRequiredOption(issuer, "Nyra:Issuer or Nyra:TokenUrl").TrimEnd('/');
        var openIdConfigurationUrl = resolvedIssuer + "/.well-known/openid-configuration";

        using var response = await httpClient.GetAsync(openIdConfigurationUrl, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OIDC configuration endpoint returned {(int)response.StatusCode}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("token_endpoint", out var tokenEndpointProperty)
            || tokenEndpointProperty.GetString() is not { Length: > 0 } tokenEndpoint)
        {
            throw new InvalidOperationException("OIDC configuration endpoint returned 200 but no token_endpoint in response.");
        }

        return tokenEndpoint;
    }

    private static string GetRequiredOption(string? value, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{configurationKey} configuration is required.");
        }

        return value;
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