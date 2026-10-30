using Microsoft.AspNetCore.DataProtection;
using PoolSense.Api.Controllers;

namespace PoolSense.Api.Services.CodeRAG;

public static class CodeRAGRegistration
{
    public static IServiceCollection AddCodeKnowledge(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection("CodeRAG").Get<CodeRAGOptions>() ?? new();
        services.Configure<CodeRAGOptions>(configuration.GetSection("CodeRAG"));
        var protection = services.AddDataProtection().SetApplicationName("PoolSense.CodeKnowledge");
        if (settings.Enabled)
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Code knowledge requires Windows-protected key storage on this deployment.");
            if (settings.RefreshMinutes < 1 || settings.ContextCharacters is < 1000 or > 96000
                || settings.MaxArchiveMegabytes is < 1 or > 1000 || settings.MaxExtractedMegabytes is < 1 or > 5000
                || settings.MaxRepositoriesPerProject is < 1 or > 100)
                throw new InvalidOperationException("Invalid CodeRAG resource limits.");
            if (!Uri.TryCreate(settings.CallbackUrl, UriKind.Absolute, out var callback)
                || callback.Scheme != "https" && !(callback.IsLoopback && callback.Scheme == "http"))
                throw new InvalidOperationException("CodeRAG CallbackUrl must use HTTPS (HTTP is allowed only on loopback).");
            protection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(settings.StoragePath, environment.ContentRootPath), "keys")))
                .ProtectKeysWithDpapi();
        }
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CodeIndexFiles>();
        services.AddSingleton<GitHubOAuthStates>();
        services.AddScoped<ICodeRepositoryStore, CodeRepositoryStore>();
        services.AddScoped<ICodeRepositoryService, CodeRepositoryService>();
        services.AddScoped<ICodeKnowledgeService, CodeKnowledgeService>();
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