using Microsoft.AspNetCore.DataProtection;
using PoolSense.Api.Controllers;

namespace PoolSense.Api.Services.CodeRAG;

public static class CodeRAGRegistration
{
    public static IServiceCollection AddCodeKnowledge(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection("CodeRAG").Get<CodeRAGOptions>() ?? new();
        services.Configure<CodeRAGOptions>(configuration.GetSection("CodeRAG"));
        services.Configure<CodeSemanticOptions>(configuration.GetSection("CodeRAG:Semantic"));
        services.Configure<OkfOptions>(configuration.GetSection("CodeRAG:Knowledge"));
        var protection = services.AddDataProtection().SetApplicationName("PoolSense.CodeKnowledge");
        if (settings.Enabled)
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Code knowledge requires Windows-protected key storage on this deployment.");
            if (settings.RefreshMinutes < 1 || settings.ContextCharacters is < 1000 or > 96000
                || settings.MaxArchiveMegabytes is < 1 or > 1000 || settings.MaxExtractedMegabytes is < 1 or > 5000
                || settings.MaxRepositoriesPerProject is < 1 or > 100
                || settings.SearchConcurrency is < 1 or > 8 || !double.IsFinite(settings.SearchDeadlineSeconds) || settings.SearchDeadlineSeconds is < .05 or > 120
                || settings.RepositoryShortlistSize is < 1 or > 16 || settings.CandidatesPerRepository is < 1 or > 100
                || settings.GlobalCandidateLimit is < 24 or > 1024
                || settings.MaxEvidenceSpans is < 1 or > 40 || settings.BackgroundConcurrency is < 1 or > 4
                || settings.BackgroundBatchSize is < 1 or > 20 || settings.BackgroundCheckSeconds is < 1 or > 300
                || settings.StartupJitterSeconds is < 0 or > 30)
                throw new InvalidOperationException("Invalid CodeRAG resource limits.");
            if (settings.Semantic.Enabled && settings.Semantic.Dimensions != 1536)
                throw new InvalidOperationException("CodeRAG semantic dimensions must match the configured NYRA embedding dimensions (1536).");
            if (settings.Semantic.MaxChunks is < 1 or > 768 || settings.Semantic.MaxNewEmbeddingsPerBuild is < 0 or > 64
                || settings.Semantic.BatchCooldownSeconds is < 60 or > 3600
                || settings.Semantic.SmallRepositoryTarget is < 1 or > 768
                || settings.Semantic.MediumRepositoryTarget is < 1 or > 768
                || settings.Semantic.LargeRepositoryTarget is < 1 or > 768
                || settings.Semantic.SmallRepositoryThreshold < 1
                || settings.Semantic.MediumRepositoryThreshold < settings.Semantic.SmallRepositoryThreshold
                || settings.Semantic.MaxManifestBytes is < 1024 or > 8388608)
                throw new InvalidOperationException("Invalid CodeRAG semantic coverage/batch limits. Total capacity is at most 768, new-vector batches at most 64, cooldown 60-3600 seconds, and manifests at most 8 MiB.");
            var sharedToken = !string.IsNullOrWhiteSpace(settings.GitHubToken);
            if (!sharedToken && (!Uri.TryCreate(settings.CallbackUrl, UriKind.Absolute, out var callback)
                || callback.Scheme != "https" && !(callback.IsLoopback && callback.Scheme == "http")))
                throw new InvalidOperationException("CodeRAG CallbackUrl must use HTTPS (HTTP is allowed only on loopback).");
            var storagePath = CodeStoragePath.Resolve(settings.StoragePath, environment.ContentRootPath);
            CodeStoragePath.EnsureWritable(storagePath);
            protection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storagePath, "keys")))
                .ProtectKeysWithDpapi();
        }
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CodeIndexFiles>();
        services.AddSingleton<CodeSnapshotWarmupQueue>();
        services.AddSingleton<ICodeSnapshotWarmupQueue>(sp => sp.GetRequiredService<CodeSnapshotWarmupQueue>());
        services.AddSingleton<GitHubOAuthStates>();
        services.AddScoped<CodeRepositoryStore>();
        services.AddSingleton<SharedGitHubConnection>();
        services.AddHttpClient(SharedGitHubConnection.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        if (string.IsNullOrWhiteSpace(settings.GitHubToken))
            services.AddScoped<ICodeRepositoryStore>(sp => sp.GetRequiredService<CodeRepositoryStore>());
        else
            services.AddScoped<ICodeRepositoryStore, SharedGitHubConnectionStore>();
        services.AddScoped<ICodeRepositoryService, CodeRepositoryService>();
        services.AddScoped<ICodeKnowledgeService, CodeKnowledgeService>();
        services.AddSingleton<CodeQueryEmbeddingCache>();
        services.AddScoped<CodeSemanticIndex>();
        services.AddScoped<Func<ILLMService>>(sp => () => sp.GetRequiredService<ILLMService>());
        services.AddScoped<ICodeChatHistoryStore, CodeChatHistoryStore>();
        services.AddScoped<ICodeChatService, CodeChatService>();
        services.AddScoped<CodeFeedbackPolicy>();
        services.AddScoped<ICodeSnapshotBuilder, CodeSnapshotBuilder>();
        services.AddScoped<CodeIndexCoordinator>();
        services.AddScoped<CodeApiFilter>();
        services.AddHttpClient<IGitHubCodeClient, GitHubCodeClient>(client => client.Timeout = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddHttpClient<GitHubOAuthService>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddHostedService<CodeIndexWorker>();
        return services;
    }
}