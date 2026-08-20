using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services.Nyra;

public interface INyraDocumentRetrievalService
{
    Task<IReadOnlyList<NyraDocumentResult>> RetrieveHybridDocumentsAsync(
        string query,
        IReadOnlyList<string> kbNames,
        int limit = 5,
        CancellationToken cancellationToken = default);
}

public sealed class NyraDocumentRetrievalService : INyraDocumentRetrievalService
{
    private const int DefaultTimeoutSeconds = 90;
    private const int MaxContentLength = 1600;

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;
    private readonly IOptionsMonitor<NyraRetrievalSettings> _retrievalSettings;
    private readonly ILogger<NyraDocumentRetrievalService> _logger;

    public NyraDocumentRetrievalService(
        HttpClient httpClient,
        IOptionsMonitor<NyraSettings> nyraSettings,
        IOptionsMonitor<NyraRetrievalSettings> retrievalSettings,
        ILogger<NyraDocumentRetrievalService> logger)
    {
        _httpClient = httpClient;
        _nyraSettings = nyraSettings;
        _retrievalSettings = retrievalSettings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NyraDocumentResult>> RetrieveHybridDocumentsAsync(
        string query,
        IReadOnlyList<string> kbNames,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || kbNames.Count == 0 || limit <= 0)
        {
            return [];
        }

        var settings = _nyraSettings.CurrentValue;
        NyraSettingsResolver.ApplyActiveProfile(settings);
        var retrievalSettings = _retrievalSettings.CurrentValue;
        NyraRetrievalSettingsResolver.ApplyDefaults(retrievalSettings);

        var retrieveDocsUrl = GetRetrieveDocsUrl(settings, retrievalSettings);
        var token = await NyraGateway.FetchTokenAsync(
            tokenUrl: string.IsNullOrWhiteSpace(settings.TokenUrl) ? null : settings.TokenUrl,
            issuer: settings.Issuer,
            clientId: settings.ClientId,
            clientSecret: settings.ClientSecret,
            audience: settings.Audience,
            scope: settings.Scope,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, retrieveDocsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var apiKey = NyraSettingsResolver.GetApiKeyHeaderValue(settings);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("NYRA-API-KEY", apiKey);
        }

        request.Content = JsonContent.Create(new
        {
            input = query,
            embeddings_name = NyraRetrievalSettingsResolver.GetEmbeddingsName(retrievalSettings),
            vector_db_config = new
            {
                vector_db_name = retrievalSettings.VectorDbName,
                vector_db_type = retrievalSettings.VectorDbType,
                vector_db_collection = retrievalSettings.VectorDbCollection
            },
            search_config = new
            {
                // Retrieve a wider candidate pool than rerank_config.k; equal values can break server-side reranker slicing.
                k = Math.Max(limit * 2, 10),
                search_type = "similarity",
                filter = new
                {
                    kb_names = NormalizeKbNames(kbNames).ToArray()
                },
                include_images = true,
                include_tables = true
            },
            rerank = true,
            rerank_config = new
            {
                k = limit,
                score_threshold = 0
            },
            hybrid_search = true
        });


        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));

        using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"NYRA document retrieval returned {(int)response.StatusCode} from {retrieveDocsUrl}: {Truncate(body, 800)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("docs", out var docsElement) || docsElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("NYRA document retrieval response did not include a docs array.");
            return [];
        }

        return docsElement
            .EnumerateArray()
            .Select(MapDocument)
            .Where(document => !string.IsNullOrWhiteSpace(document.Content) || !string.IsNullOrWhiteSpace(document.SourceUrl))
            .Take(limit)
            .ToList();
    }

    private static Uri GetRetrieveDocsUrl(NyraSettings nyraSettings, NyraRetrievalSettings retrievalSettings)
    {
        var baseUrl = FirstNonEmpty(retrievalSettings.BaseUrl, BuildRetrievalBaseUrl(nyraSettings.GatewayEndpoint));
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("NyraRetrieval:BaseUrl or Nyra:GatewayEndpoint configuration is required for document retrieval.");
        }

        return new Uri($"{baseUrl.TrimEnd('/')}/retrieve-docs");
    }

    private static NyraDocumentResult MapDocument(JsonElement document)
    {
        var metadata = document.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : default;

        var title = FirstNonEmpty(
            GetString(document, "title", "document_title", "name"),
            GetString(metadata, "title", "document_title", "name", "source"));
        var sourceUrl = FirstNonEmpty(
            GetString(document, "source_url", "url", "web_url", "wiki_url", "link", "doc_url", "page_url", "href", "file_url", "source"),
            GetString(metadata, "source_url", "url", "web_url", "wiki_url", "link", "doc_url", "page_url", "href", "file_url", "source", "path"),
            FindFirstHttpUrl(document),
            FindFirstHttpUrl(metadata));
        var content = FirstNonEmpty(
            GetString(document, "page_content", "content", "text", "document"),
            GetString(metadata, "page_content", "content", "text", "chunk"));
        var kbName = FirstNonEmpty(
            GetString(document, "kb_name", "knowledge_base"),
            GetString(metadata, "kb_name", "kb_names", "knowledge_base"));
        var documentId = FirstNonEmpty(
            GetString(document, "doc_id", "document_id", "id"),
            GetString(metadata, "doc_id", "document_id", "id"));
        var score = GetDouble(document, "score", "rerank_score", "similarity")
            ?? GetDouble(metadata, "score", "rerank_score", "similarity")
            ?? 0;

        return new NyraDocumentResult
        {
            DocumentId = documentId,
            KbName = kbName,
            Title = title,
            Content = Truncate(content, MaxContentLength),
            SourceUrl = sourceUrl,
            Citation = FirstNonEmpty(title, sourceUrl, documentId, kbName, "NYRA document"),
            Score = score
        };
    }

    private static IReadOnlyList<string> NormalizeKbNames(IEnumerable<string> kbNames) =>
        kbNames
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildRetrievalBaseUrl(string gatewayEndpoint)
    {
        if (string.IsNullOrWhiteSpace(gatewayEndpoint))
        {
            return string.Empty;
        }

        return gatewayEndpoint
            .TrimEnd('/')
            .Replace("/llm-service", "/retrieval-service/v1", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var propertyName in propertyNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? string.Empty;
                }

                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    var values = property.Value
                        .EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString())
                        .Where(value => !string.IsNullOrWhiteSpace(value));

                    return string.Join(", ", values);
                }
            }
        }

        return string.Empty;
    }

    private static string FindFirstHttpUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString() ?? string.Empty;
                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static double? GetDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetDouble(out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}