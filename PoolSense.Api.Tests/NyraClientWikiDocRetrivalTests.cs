using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PoolSense.Api.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class NyraClientWikiDocRetrivalTests
{
	private const string DefaultIssuer = "https://sso.cdcprod.mfg.intel.com";
	private const string DefaultAudience = "nyra-web-core-api-cdcprod";
	private const string DefaultClientId = "poolsense-nyra-app-cdcprod";
	private const string DefaultClientSecret = "";
	private const string DefaultScope = "openid";
	private const string DefaultGatewayEndpoint = "https://imi.cdcprod.mfg.intel.com/api/nyra-gateway/llm-service/";
	private const string DefaultRetrievalEmbeddingsName = NyraRetrievalSettings.SupportedEmbeddingsName;
	private const string DefaultRetrievalBaseUrl = "https://imi.cdcprod.mfg.intel.com/api/nyra-gateway/retrieval-service/v1";
	private const string DefaultVectorDbName = "NYRA_CORE_DB";
	private const string DefaultVectorDbType = "PG_VECTOR";
	private const string DefaultVectorDbCollection = "core_db";
	private const string DefaultKbName = "FSCOUserGuide,FSCO_Wiki";
	private const string DefaultQuery = "What is Drumbeat Solver?";

	private readonly ITestOutputHelper _output;

	public NyraClientWikiDocRetrivalTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void NyraRetrievalSettings_UseKbEmbeddingModelWhenNyraEmbeddingModelChanges()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NYRA_EMBEDDING_MODEL"] = "text-embedding-3-large",
				["Nyra:EmbeddingModel"] = "text-embedding-3-large",
				["Nyra:GatewayEndpoint"] = DefaultGatewayEndpoint
			})
			.Build();

		var settings = CreateSettings(requireSecret: false, configuration);

		Assert.NotNull(settings);
		Assert.Equal(DefaultRetrievalEmbeddingsName, settings.EmbeddingModel);
	}

	[Fact]
	public void NyraRetrievalSettings_RejectUnsupportedKbEmbeddingModel()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NYRA_RETRIEVAL_EMBEDDINGS_NAME"] = "text-embedding-3-large"
			})
			.Build();

		var ex = Assert.Throws<InvalidOperationException>(() => CreateSettings(requireSecret: false, configuration));

		Assert.Contains(DefaultRetrievalEmbeddingsName, ex.Message, StringComparison.Ordinal);
	}

    /// <summary>
    /// /retrieve-docs
    #region retrieve-docs algo start
    /// Retrieve pipeline visualization
    ///Sends a POST to /retrieval-service/v1/retrieve-docs.Adjust k, rerank_k, hybrid, rerank, tags, KB, and metadata filter to see which stage of the pipeline(ES BM25, PG vector, RRF fusion, reranker) 
    ///picks each doc — and how the final rank shifts.Uses whichever auth source is selected on the Models tab, so you can also verify client-specific access here.
    ///
    ///Column guide: pg# = doc's rank in the PG vector list, es# = doc's rank in the ES BM25 list. RRF fuses these two ranks (formula: Σ 1/(k+rank_source)), so a doc that's pg#1 AND es#1 lands at the top; 
    ///a doc in only one source ranks lower. ///Hover any rank badge for the underlying raw score.rerank = Jina cross-encoder relevance (higher = better; negatives OK).
    ///
    /// Output
    /// {
    //"status": "success",
    //"docs": [
    //  {
    //    "id": null,
    //    "metadata": {
    //      "text_id": "ab7d29c6-c6fe-42a2-8cb5-51f73e7ef72d",
    //      "images": [],
    //      "tables": [],
    //      "table_ids": [],
    //      "load_date": "2026-05-01 00:00:00",
    //      "source": "https://wiki.ith.intel.com/spaces/FSCOUserGuide/pages/2561845263/Drumbeat+Solver",
    //      "classification": "Intel Confidential",
    //      "tags": [
    //        "FSCOUSERGUIDE",
    //        "FSCO",
    //        "ENTERPRISE_WIKI"
    //      ],
    //      "token_count": 348,
    //      "meta": {
    //        "type": "page",
    //        "title": "Drumbeat Solver",
    //        "status": "current",
    //        "page_id": "2561845263",
    //        "published": "2022-08-24",
    //        "space_key": "FSCOUserGuide",
    //        "parent_link": "https://wiki.ith.intel.com/display/FSCOUserGuide",
    //        "pipeline_names": [
    //          "fscowikiuser",
    //          "fscouserguide"
    //        ],
    //        "last_updated_by": "asp1",
    //        "ownership_group": "Sp, Anjana Devi",
    //        "last_updated_date": "2023-02-28"
    //      },
    //      "rerank_score": 0.5807628631591797,
    //      "rrf_score": null,
    //      "pg_similarity_score": 0.6573830165104231,
    //      "es_score": null
    //    },
    //    "page_content": "**Drumbeat/PACE** **Solver** takes a snapshot of WIP, tool inventory and run rate, and projects WIP/Wafer Start movements through their operation flows to best meet wafer out demands..."
    //    "type": "Document"
    //  },		   
    #endregion output logic end
    /// </summary> 
    [Fact]
	public async Task NyraRetrieveDocs_ShouldAcceptAdvancedMetadataFilterRequest()
	{
		var settings = CreateSettings(requireSecret: true);
		if (settings is null)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var httpClient = CreateHttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, GetRetrieveDocsUrl(settings));
		await ApplyNyraAuthHeadersAsync(request, settings, httpClient, timeout.Token);
		request.Content = JsonContent.Create(new
		{
			input = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY")
				?? DefaultQuery,
			embeddings_name = settings.EmbeddingModel,
			vector_db_config = new
			{
				vector_db_name = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_VECTOR_DB_NAME")
					?? DefaultVectorDbName,
				vector_db_type = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_VECTOR_DB_TYPE")
					?? DefaultVectorDbType,
				vector_db_collection = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_COLLECTION")
					?? DefaultVectorDbCollection
			},
			search_config = new
			{
				k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 10),
				search_type = "similarity",
				filter = CreateKbFilter()
			},
			rerank = true,
			rerank_config = new
			{
				k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_RERANK_K", 5),
				score_threshold = 0
			},
			hybrid_search = true
		});

		using var response = await httpClient.SendAsync(request, timeout.Token);
		var body = await response.Content.ReadAsStringAsync(timeout.Token);

		Assert.True(response.IsSuccessStatusCode, $"Retrieve docs returned {(int)response.StatusCode}: {body}");
		AssertRetrievedDocs(body, "retrieve-docs");
	}

    /// <summary>
    /// /browse-docs
    #region browse-docs algo start 
    /// Browse — multi-mode retrieval + modality bias
    /// Sends a POST to /retrieval-service/v1/browse-docs.Same doc shape as /retrieve-docs, but with four modes: metadata_only(filter-only, no query), keyword(ES BM25), semantic(PG vector), 
    /// hybrid(PG+ES+RRF). Combine with prefer_modality to bias toward image / table - rich chunks and rerank for Jina cross-encoder on the hybrid fused pool.
    /// 
    ///Output:
    ///{
    //"status": "success",
    //"docs": [
    //  {
    //    "id": null,
    //    "metadata": {
    //      "text_id": "f7a75a4c-4b72-43f4-b422-dadf3be7fce5",
    //      "images": [],
    //      "tables": [],
    //      "load_date": "2025-03-08 00:00:00",
    //      "source": "https://wiki.ith.intel.com/display/FSMIDMWIKI/FSCO Goaling Business Process",
    //      "classification": "Intel Confidential",
    //      "tags": [
    //        "ENTERPRISE_WIKI",
    //        "FSMIDMWIKI"
    //      ],
    //      "token_count": 2593,
    //      "meta": {
    //        "message": "scheduled data insertion 2025-03-08 03:02:50, sibling_document: https://wiki.ith.intel.com/display/FSMIDMWIKI/DPML+-+Days+Per+Mask+Layer found - 2025-03-08 03:04:12",
    //        "page_id": "3583479339",
    //        "space_key": "FSMIDMWIKI",
    //        "parent_link": "https://wiki.ith.intel.com/display/FSMIDMWIKI",
    //        "total_chunks": 1,
    //        "published_date": "2024-05-29",
    //        "last_updated_date": "2025-03-07",
    //        "enterprise_wiki_url": "https://wiki.ith.intel.com/display/FSMIDMWIKI/FSCO Goaling Business Process",
    //        "enterprise_wiki_pull_type": "scheduled"
    //      },
    //      "pg_similarity_score": 0.4573577830705454,
    //      "es_score": null,
    //      "rerank_score": null,
    //      "rrf_score": null
    //    },
    //    "page_content": "FSCO Goaling Business Process Owner1 Brendan Murray Owner2 Tzahi Vilenski Approver Orit Assaf Initial Issue Date May 29, 2024 Current Revision# 6 Latest Revision ..."
    //    "type": "Document"
    //  },
    #endregion browse-docs algo end
    /// </summary>   
    [Fact]
	public async Task NyraBrowseDocs_ShouldRetrieveData()
	{
		var settings = CreateSettings(requireSecret: true);
		if (settings is null)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var httpClient = CreateHttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, GetBrowseDocsUrl(settings));
		await ApplyNyraAuthHeadersAsync(request, settings, httpClient, timeout.Token);
		request.Content = JsonContent.Create(new
		{
			search_mode = "hybrid",
			k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 10),
			filter = CreateKbFilter(),
			query = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY")
				?? DefaultQuery,
			rerank = true
		});

		using var response = await httpClient.SendAsync(request, timeout.Token);
		var body = await response.Content.ReadAsStringAsync(timeout.Token);

		Assert.True(response.IsSuccessStatusCode, $"Browse docs returned {(int)response.StatusCode}: {body}");
		AssertRetrievedDocs(body, "browse-docs");
	}

	[Theory]
	[InlineData("metadata_only", "")]
	[InlineData("keyword", DefaultQuery)]
	[InlineData("semantic", DefaultQuery)]
	[InlineData("hybrid", DefaultQuery)]
	public async Task BrowseDocs_AllModes_ReturnValidResponse(string searchMode, string query)
	{
		var settings = CreateSettings(requireSecret: true);
		if (settings is null)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var httpClient = CreateHttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, GetBrowseDocsUrl(settings));
		await ApplyNyraAuthHeadersAsync(request, settings, httpClient, timeout.Token);
		request.Content = JsonContent.Create(new
		{
			search_mode = searchMode,
			k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 10),
			filter = CreateKbFilter(),
			query = searchMode.Equals("metadata_only", StringComparison.OrdinalIgnoreCase)
				? query
				: Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY") ?? query,
			rerank = searchMode.Equals("hybrid", StringComparison.OrdinalIgnoreCase)
		});

		using var response = await httpClient.SendAsync(request, timeout.Token);
		var body = await response.Content.ReadAsStringAsync(timeout.Token);

		Assert.True(response.IsSuccessStatusCode, $"Browse docs {searchMode} mode returned {(int)response.StatusCode}: {body}");
		AssertRetrievedDocs(body, $"browse-docs {searchMode}");
	}

	[Fact]
	public async Task BrowseDocs_MetadataMode_ReturnsFilteredResults()
	{
		await AssertBrowseDocsModeReturnsDocsAsync("metadata_only", string.Empty);
	}

	[Fact]
	public async Task BrowseDocs_KeywordMode_ReturnsBM25Results()
	{
		await AssertBrowseDocsModeReturnsDocsAsync(
			"keyword",
			Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY") ?? DefaultQuery);
	}

	[Fact]
	public async Task BrowseDocs_SemanticMode_ReturnsVectorResults()
	{
		await AssertBrowseDocsModeReturnsDocsAsync(
			"semantic",
			Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY") ?? DefaultQuery);
	}

	[Fact]
	public async Task BrowseDocs_HybridMode_WithModalityPreference_ReturnsRankedResults()
	{
		var settings = CreateSettings(requireSecret: true);
		if (settings is null)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var httpClient = CreateHttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, GetBrowseDocsUrl(settings));
		await ApplyNyraAuthHeadersAsync(request, settings, httpClient, timeout.Token);
		request.Content = JsonContent.Create(new
		{
			search_mode = "hybrid",
			k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 20),
			filter = CreateKbFilter(),
			query = Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_QUERY")
				?? DefaultQuery,
			prefer_modality = new[] { "image", "table" },
			rerank = true
		});

		using var response = await httpClient.SendAsync(request, timeout.Token);
		var body = await response.Content.ReadAsStringAsync(timeout.Token);

		Assert.True(response.IsSuccessStatusCode, $"Browse docs hybrid modality mode returned {(int)response.StatusCode}: {body}");
		AssertRetrievedDocs(body, "browse-docs hybrid modality", maxDocs: GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 20));
	}

	private async Task AssertBrowseDocsModeReturnsDocsAsync(string searchMode, string query)
	{
		var settings = CreateSettings(requireSecret: true);
		if (settings is null)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var httpClient = CreateHttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, GetBrowseDocsUrl(settings));
		await ApplyNyraAuthHeadersAsync(request, settings, httpClient, timeout.Token);
		request.Content = JsonContent.Create(new
		{
			search_mode = searchMode,
			k = GetIntEnvironmentVariable("NYRA_RETRIEVAL_K", 10),
			filter = CreateKbFilter(),
			query,
			rerank = searchMode.Equals("hybrid", StringComparison.OrdinalIgnoreCase)
		});

		using var response = await httpClient.SendAsync(request, timeout.Token);
		var body = await response.Content.ReadAsStringAsync(timeout.Token);

		Assert.True(response.IsSuccessStatusCode, $"Browse docs {searchMode} mode returned {(int)response.StatusCode}: {body}");
		AssertRetrievedDocs(body, $"browse-docs {searchMode}");
	}

	private static Uri GetRetrieveDocsUrl(NyraClientTestSettings settings)
	{
		return new Uri($"{settings.RetrievalBaseUrl.TrimEnd('/')}/retrieve-docs");
	}

	private static Uri GetBrowseDocsUrl(NyraClientTestSettings settings)
	{
		return new Uri($"{settings.RetrievalBaseUrl.TrimEnd('/')}/browse-docs");
	}

	private static int GetIntEnvironmentVariable(string name, int defaultValue)
	{
		return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
			? value
			: defaultValue;
	}

	private static object CreateKbFilter()
	{
		return new
		{
			kb_names = new[]
			{
				Environment.GetEnvironmentVariable("NYRA_RETRIEVAL_KB_NAME")
					?? DefaultKbName
			}
		};
	}

	private void AssertRetrievedDocs(string body, string operation, int? maxDocs = null)
	{
		Assert.False(string.IsNullOrWhiteSpace(body));
		_output.WriteLine($"NYRA {operation} raw response: {body}");

		using var json = JsonDocument.Parse(body);
		Assert.NotEqual(JsonValueKind.Undefined, json.RootElement.ValueKind);

		var root = json.RootElement;
		Assert.True(root.TryGetProperty("docs", out var docsElement), "Response does not include a docs property.");
		Assert.Equal(JsonValueKind.Array, docsElement.ValueKind);
		Assert.True(docsElement.GetArrayLength() > 0, "Expected at least one retrieved doc, but docs array is empty.");

		if (maxDocs.HasValue)
		{
			Assert.True(docsElement.GetArrayLength() <= maxDocs.Value, $"Expected no more than {maxDocs.Value} docs, but received {docsElement.GetArrayLength()}.");
		}

		_output.WriteLine($"NYRA {operation} docs count: {docsElement.GetArrayLength()}");
	}

	private static NyraClientTestSettings? CreateSettings(bool requireSecret)
	{
		var configuration = new ConfigurationBuilder()
			.AddJsonFile("appsettings.json", optional: true)
			.AddEnvironmentVariables()
			.Build();

		return CreateSettings(requireSecret, configuration);
	}

	private static NyraClientTestSettings? CreateSettings(bool requireSecret, IConfiguration configuration)
	{
		var retrievalSettings = configuration.GetSection("NyraRetrieval").Get<NyraRetrievalSettings>() ?? new NyraRetrievalSettings();

		var nyraSettings = configuration.GetSection("Nyra").Get<NyraSettings>() ?? new NyraSettings();
		nyraSettings.ActiveProfile = FirstNonEmpty(
			configuration["NYRA_RETRIEVAL_PROFILE"],
			retrievalSettings.ActiveProfile,
			nyraSettings.ActiveProfile);
		NyraSettingsResolver.ApplyActiveProfile(nyraSettings);

		var settings = new NyraClientTestSettings(
			Issuer: FirstNonEmpty(configuration["NYRA_RETRIEVAL_ISSUER"], configuration["NYRA_ISSUER"], nyraSettings.Issuer, DefaultIssuer),
			TokenUrl: FirstNonEmpty(configuration["NYRA_RETRIEVAL_TOKEN_URL"], configuration["NYRA_TOKEN_URL"], nyraSettings.TokenUrl),
			Audience: FirstNonEmpty(configuration["NYRA_RETRIEVAL_AUDIENCE"], configuration["NYRA_AUDIENCE"], nyraSettings.Audience, DefaultAudience),
			ClientId: FirstNonEmpty(configuration["NYRA_RETRIEVAL_CLIENT_ID"], configuration["NYRA_CLIENT_ID"], nyraSettings.ClientId, DefaultClientId),
			ClientSecret: FirstNonEmpty(configuration["NYRA_RETRIEVAL_CLIENT_SECRET"], configuration["NYRA_CLIENT_SECRET"], nyraSettings.ClientSecret, DefaultClientSecret),
			Scope: FirstNonEmpty(configuration["NYRA_RETRIEVAL_SCOPE"], configuration["NYRA_SCOPE"], nyraSettings.Scope, DefaultScope),
			ApiKey: FirstNonEmpty(configuration["NYRA_RETRIEVAL_API_KEY"], configuration["NYRA_API_KEY"], NyraSettingsResolver.GetApiKeyHeaderValue(nyraSettings)),
			EmbeddingModel: GetRetrievalEmbeddingsName(configuration),
			RetrievalBaseUrl: FirstNonEmpty(
				configuration["NYRA_RETRIEVAL_BASE_URL"],
				retrievalSettings.BaseUrl,
				BuildRetrievalBaseUrl(nyraSettings.GatewayEndpoint),
				DefaultRetrievalBaseUrl));

		if (requireSecret && string.IsNullOrWhiteSpace(settings.ClientSecret))
		{
			return null;
		}

		return settings;
	}

	private static string GetRetrievalEmbeddingsName(IConfiguration configuration)
	{
		var retrievalSettings = configuration.GetSection("NyraRetrieval").Get<NyraRetrievalSettings>() ?? new NyraRetrievalSettings();
		retrievalSettings.EmbeddingsName = FirstNonEmpty(
			configuration["NYRA_RETRIEVAL_EMBEDDINGS_NAME"],
			retrievalSettings.EmbeddingsName,
			DefaultRetrievalEmbeddingsName);

		return NyraRetrievalSettingsResolver.GetEmbeddingsName(retrievalSettings);
	}

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

	private static async Task ApplyNyraAuthHeadersAsync(
		HttpRequestMessage request,
		NyraClientTestSettings settings,
		HttpClient httpClient,
		CancellationToken cancellationToken)
	{
		var token = await RequestAccessTokenAsync(httpClient, settings, cancellationToken);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		if (!string.IsNullOrWhiteSpace(settings.ApiKey))
		{
			request.Headers.Add("NYRA-API-KEY", settings.ApiKey);
		}
	}

	private static async Task<string> RequestAccessTokenAsync(
		HttpClient httpClient,
		NyraClientTestSettings settings,
		CancellationToken cancellationToken)
	{
		var tokenUrl = string.IsNullOrWhiteSpace(settings.TokenUrl)
			? await ResolveTokenUrlAsync(httpClient, settings.Issuer, cancellationToken)
			: settings.TokenUrl;

		using var content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = settings.ClientId,
			["client_secret"] = settings.ClientSecret,
			["scope"] = settings.Scope,
			["audience"] = settings.Audience,
		});

		using var response = await httpClient.PostAsync(tokenUrl, content, cancellationToken);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);

		Assert.True(response.IsSuccessStatusCode, $"Token endpoint returned {(int)response.StatusCode}: {body}");

		using var json = JsonDocument.Parse(body);
		Assert.True(json.RootElement.TryGetProperty("access_token", out var tokenProperty), body);

		var accessToken = tokenProperty.GetString();
		Assert.False(string.IsNullOrWhiteSpace(accessToken), body);
		return accessToken;
	}

	private static async Task<string> ResolveTokenUrlAsync(
		HttpClient httpClient,
		string issuer,
		CancellationToken cancellationToken)
	{
		Assert.False(string.IsNullOrWhiteSpace(issuer), "NYRA issuer is required when token URL is not configured.");

		var openIdConfigurationUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
		using var response = await httpClient.GetAsync(openIdConfigurationUrl, cancellationToken);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);

		Assert.True(response.IsSuccessStatusCode, $"OIDC configuration endpoint returned {(int)response.StatusCode}: {body}");

		using var json = JsonDocument.Parse(body);
		Assert.True(json.RootElement.TryGetProperty("token_endpoint", out var tokenEndpointProperty), body);

		var tokenEndpoint = tokenEndpointProperty.GetString();
		Assert.False(string.IsNullOrWhiteSpace(tokenEndpoint), body);
		return tokenEndpoint;
	}

	private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(90) };

	private static string FirstNonEmpty(params string?[] values) =>
		values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

	private sealed record NyraClientTestSettings(
		string Issuer,
		string TokenUrl,
		string Audience,
		string ClientId,
		string ClientSecret,
		string Scope,
		string ApiKey,
		string EmbeddingModel,
		string RetrievalBaseUrl);
}
