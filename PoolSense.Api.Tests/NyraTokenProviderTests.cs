using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Services.Nyra;
using Xunit;
using Xunit.Abstractions;

namespace PoolSense.Api.Tests;

public sealed class NyraTokenProviderTests
{
    private readonly ITestOutputHelper output;

    public NyraTokenProviderTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task TokenAndDiscoveryAreCachedByCredentialIdentityUntilSafeExpiry()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tokens = 0;
        var discoveries = 0;
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref discoveries);
                return Json("""{"token_endpoint":"https://auth.test/token"}""");
            }
            var token = Interlocked.Increment(ref tokens);
            return Json($$"""{"access_token":"token-{{token}}","expires_in":3600}""");
        }));
        var provider = new NyraTokenProvider(client, clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetTokenAsync(
            issuer: "https://auth.test", clientId: "app", clientSecret: "secret-a", audience: "api")));
        Assert.All(results, token => Assert.Equal("token-1", token));
        Assert.Equal(1, tokens);
        Assert.Equal(1, discoveries);
        Assert.Equal(1, provider.TokenEndpointRequests);
        Assert.Equal(1, provider.DiscoveryRequests);

        var rotated = await provider.GetTokenAsync(issuer: "https://auth.test", clientId: "app",
            clientSecret: "secret-b", audience: "api");
        Assert.Equal("token-2", rotated);
        clock.Advance(TimeSpan.FromSeconds(3541));
        Assert.Equal("token-3", await provider.GetTokenAsync(issuer: "https://auth.test", clientId: "app",
            clientSecret: "secret-a", audience: "api"));
        Assert.Equal(3, tokens);
        Assert.Equal(1, discoveries);
    }

    [Fact]
    public async Task MissingExpiryIsNotCachedAndFailuresCanBeRetried()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(call == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"access_token":"opaque-token"}"""));
        }));
        var provider = new NyraTokenProvider(client);
        var arguments = new { TokenUrl = "https://auth.test/token", ClientId = "app", ClientSecret = "secret", Audience = "api" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(arguments.TokenUrl,
            clientId: arguments.ClientId, clientSecret: arguments.ClientSecret, audience: arguments.Audience));
        Assert.Equal("opaque-token", await provider.GetTokenAsync(arguments.TokenUrl,
            clientId: arguments.ClientId, clientSecret: arguments.ClientSecret, audience: arguments.Audience));
        Assert.Equal("opaque-token", await provider.GetTokenAsync(arguments.TokenUrl,
            clientId: arguments.ClientId, clientSecret: arguments.ClientSecret, audience: arguments.Audience));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelSharedRefresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return Json("""{"access_token":"shared-token","expires_in":600}""");
        }));
        var provider = new NyraTokenProvider(client);

        using var canceled = new CancellationTokenSource();
        var first = provider.GetTokenAsync("https://auth.test/token", clientId: "app", clientSecret: "secret",
            audience: "api", cancellationToken: canceled.Token);
        var second = provider.GetTokenAsync("https://auth.test/token", clientId: "app", clientSecret: "secret",
            audience: "api");
        await started.Task;
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();

        Assert.Equal("shared-token", await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExplicitUnauthorizedInvalidationForcesFreshToken()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var token = Interlocked.Increment(ref calls);
            return Task.FromResult(Json($$"""{"access_token":"token-{{token}}","expires_in":600}"""));
        }));
        var provider = new NyraTokenProvider(client);
        var args = new { TokenUrl = "https://auth.test/token", ClientId = "app", ClientSecret = "secret", Audience = "api" };
        Assert.Equal("token-1", await provider.GetTokenAsync(args.TokenUrl, clientId: args.ClientId,
            clientSecret: args.ClientSecret, audience: args.Audience));
        provider.InvalidateToken(args.TokenUrl, clientId: args.ClientId, clientSecret: args.ClientSecret, audience: args.Audience);
        Assert.Equal("token-2", await provider.GetTokenAsync(args.TokenUrl, clientId: args.ClientId,
            clientSecret: args.ClientSecret, audience: args.Audience));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EmbeddingRetriesUnauthorizedOnceWithFreshBearerToken()
    {
        var tokenCalls = 0;
        using var tokenClient = new HttpClient(new Handler((_, _) =>
        {
            var token = Interlocked.Increment(ref tokenCalls);
            return Task.FromResult(Json($$"""{"access_token":"token-{{token}}","expires_in":600}"""));
        }));
        var provider = new NyraTokenProvider(tokenClient);
        var embeddingCalls = 0;
        var bearerHeaders = new List<string?>();
        using var embeddingClient = new HttpClient(new Handler((request, _) =>
        {
            bearerHeaders.Add(request.Headers.Authorization?.Parameter);
            var call = Interlocked.Increment(ref embeddingCalls);
            return Task.FromResult(call == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""{"data":[{"embedding":[1,2]}]}"""));
        }));
        var settings = new NyraSettings
        {
            ClientId = "app",
            ClientSecret = "secret",
            Audience = "api",
            TokenUrl = "https://auth.test/token",
            EmbeddingGenerateUrl = "https://embedding.test/generate",
            EmbeddingModel = "model",
            EmbeddingApiVersion = "v1"
        };
        var generator = new NyraEmbeddingGenerator(embeddingClient, new StaticMonitor<NyraSettings>(settings), provider);

        var result = await generator.GenerateAsync(["question"]);

        Assert.Single(result);
        Assert.Equal(2, embeddingCalls);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(new[] { "token-1", "token-2" }, bearerHeaders);
    }

    [Fact]
    public async Task ReadOnlyDocumentRetrievalRetriesUnauthorizedOnceWithFreshBearerToken()
    {
        var tokenCalls = 0;
        using var tokenClient = new HttpClient(new Handler((_, _) =>
        {
            var token = Interlocked.Increment(ref tokenCalls);
            return Task.FromResult(Json($$"""{"access_token":"token-{{token}}","expires_in":600}"""));
        }));
        var provider = new NyraTokenProvider(tokenClient);
        var retrievalCalls = 0;
        var bearerHeaders = new List<string?>();
        using var retrievalClient = new HttpClient(new Handler((request, _) =>
        {
            bearerHeaders.Add(request.Headers.Authorization?.Parameter);
            var call = Interlocked.Increment(ref retrievalCalls);
            return Task.FromResult(call == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""{"docs":[{"content":"read-only result","title":"fixture"}]}"""));
        }));
        var settings = new NyraSettings
        {
            ClientId = "app",
            ClientSecret = "secret",
            Audience = "api",
            TokenUrl = "https://auth.test/token"
        };
        var retrievalSettings = new NyraRetrievalSettings { BaseUrl = "https://retrieval.test" };
        var service = new NyraDocumentRetrievalService(retrievalClient,
            new StaticMonitor<NyraSettings>(settings), new StaticMonitor<NyraRetrievalSettings>(retrievalSettings),
            NullLogger<NyraDocumentRetrievalService>.Instance, provider);

        var result = await service.RetrieveHybridDocumentsAsync("question", ["kb"]);

        Assert.Single(result);
        Assert.Equal("read-only result", result[0].Content);
        Assert.Equal(2, retrievalCalls);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(new[] { "token-1", "token-2" }, bearerHeaders);
    }

    [Fact]
    public async Task ColdWarmBenchmark_ReportsTokenAndDiscoveryRequestsAvoided()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(25, token);
            return Json("""{"access_token":"opaque","expires_in":600}""");
        }));
        var provider = new NyraTokenProvider(client);
        var timer = Stopwatch.StartNew();
        await provider.GetTokenAsync("https://auth.test/token", clientId: "app",
            clientSecret: "secret", audience: "api");
        var coldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await provider.GetTokenAsync("https://auth.test/token", clientId: "app",
            clientSecret: "secret", audience: "api");
        var warmMs = timer.Elapsed.TotalMilliseconds;

        Assert.Equal(1, calls);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            benchmark = "nyra-token-cache",
            coldMilliseconds = Math.Round(coldMs, 3),
            warmMilliseconds = Math.Round(warmMs, 3),
            simulatedTokenEndpointDelayMilliseconds = 25,
            tokenEndpointRequestsAvoided = 1,
            discoveryRequests = provider.DiscoveryRequests
        }));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
