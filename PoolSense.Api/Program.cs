using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Connectors;
using PoolSense.Api.Data;
using PoolSense.Api.Options;
using PoolSense.Api.Orchestration;
using PoolSense.Api.Services;
using PoolSense.Api.Logging;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
const string PoolSenseUiCorsPolicy = "PoolSenseUi";

builder.Logging.AddProvider(new SqlServerApplicationLoggerProvider(
    builder.Configuration,
    builder.Environment.ApplicationName,
    builder.Environment.EnvironmentName));

static bool HasHttpsBinding(IConfiguration configuration)
{
    var candidateUrls = new[]
    {
        configuration["URLS"],
        configuration["ASPNETCORE_URLS"],
        configuration["DOTNET_URLS"],
        configuration["https_port"] is { Length: > 0 } httpsPort ? $"https://localhost:{httpsPort}" : null,
        configuration["HTTPS_PORT"] is { Length: > 0 } uppercaseHttpsPort ? $"https://localhost:{uppercaseHttpsPort}" : null
    };

    return candidateUrls
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(value => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}

static bool HasRequiredAiSettings(AiSettings aiSettings)
{
    return !string.IsNullOrWhiteSpace(aiSettings.BaseUrl)
        && !string.IsNullOrWhiteSpace(aiSettings.ApiKey)
        && !string.IsNullOrWhiteSpace(aiSettings.Models.Chat)
        && !string.IsNullOrWhiteSpace(aiSettings.Models.Embeddings);
}

static void ValidateAiSettings(AiSettings aiSettings)
{
    if (!HasRequiredAiSettings(aiSettings))
    {
        throw new InvalidOperationException("Azure OpenAI configuration is incomplete. Configure AiSettings:BaseUrl, AiSettings:ApiKey, AiSettings:Models:Chat, and AiSettings:Models:Embeddings.");
    }
}

static HttpMessageHandler CreateAzureOpenAiHttpMessageHandler(AiSettings aiSettings, ILogger logger)
{
    if (!aiSettings.Http.AllowCertificateRevocationUnknown)
    {
        return new HttpClientHandler();
    }

    logger.LogWarning("Azure OpenAI HTTP client is configured to allow certificate chains whose only validation errors are unknown or offline revocation status.");
    return new HttpClientHandler
    {
        CheckCertificateRevocationList = true,
        ServerCertificateCustomValidationCallback = ValidateCertificateAllowingRevocationUnknown
    };
}

static bool ValidateCertificateAllowingRevocationUnknown(
    HttpRequestMessage request,
    X509Certificate2? certificate,
    X509Chain? chain,
    SslPolicyErrors sslPolicyErrors)
{
    if (sslPolicyErrors == SslPolicyErrors.None)
    {
        return true;
    }

    if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || chain is null)
    {
        return false;
    }

    const X509ChainStatusFlags allowedRevocationStatusErrors =
        X509ChainStatusFlags.RevocationStatusUnknown |
        X509ChainStatusFlags.OfflineRevocation;

    return chain.ChainStatus.All(status =>
        status.Status == X509ChainStatusFlags.NoError ||
        (status.Status & ~allowedRevocationStatusErrors) == 0);
}

// Add services to the container.
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new AuthorizeFilter());
});
builder.Services.Configure<AiSettings>(builder.Configuration.GetSection("AiSettings"));
builder.Services.Configure<ActiveDirectoryOptions>(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<TicketAutomationSettings>(builder.Configuration.GetSection("TicketAutomation"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRsaKeyMaterialProvider, RsaKeyMaterialProvider>();
builder.Services.AddSingleton<ISessionPasswordStore, SessionPasswordStore>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IActiveDirectoryAuthService, ActiveDirectoryAuthService>();
builder.Services.AddAuthentication(PoolSenseJwtAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PoolSenseJwtAuthenticationHandler>(PoolSenseJwtAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddPolicy(PoolSenseUiCorsPolicy, policy =>
    {
        var configuredOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>()
            ?? [];
        var allowedOrigins = configuredOrigins.Length == 0
            ? ["http://localhost:4200", "http://localhost:5173"]
            : configuredOrigins;

        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddScoped<ITicketAnalyzerAgent, TicketAnalyzerAgent>();
builder.Services.AddScoped<IResolutionAgent, ResolutionAgent>();
builder.Services.AddScoped<IQueryVariantGeneratorAgent, QueryVariantGeneratorAgent>();
builder.Services.AddScoped<IFailurePatternAgent, FailurePatternAgent>();

builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<ILLMService, LLMService>();
builder.Services.AddScoped<IKnowledgeEnrichmentService, KnowledgeEnrichmentService>();
builder.Services.AddScoped<IFailurePatternService, FailurePatternService>();
builder.Services.AddScoped<ITicketIngestionService, TicketIngestionService>();
builder.Services.AddScoped<InteractionLogger>();
builder.Services.AddScoped<ILlmTokenUsageRepository, LlmTokenUsageRepository>();

var emailDeliveryMode = builder.Configuration
    .GetSection("TicketAutomation:Email:DeliveryMode")
    .Get<EmailDeliveryMode>();
if (emailDeliveryMode == EmailDeliveryMode.DatabaseMail)
{
    builder.Services.AddScoped<ITicketRecommendationEmailService, DatabaseMailEmailService>();
}
else
{
    builder.Services.AddScoped<ITicketRecommendationEmailService, TicketRecommendationEmailService>();
}

builder.Services.AddScoped<IncidentContextBuilder>();

builder.Services.AddSingleton<InMemoryVectorStoreCache>();
builder.Services.AddSingleton<IVectorStoreCacheInvalidator>(sp => sp.GetRequiredService<InMemoryVectorStoreCache>());
builder.Services.AddScoped<IPoolSenseSqlConnectionFactory, PoolSenseSqlConnectionFactory>();
builder.Services.AddScoped<IAuthUserRepository, AuthUserRepository>();
builder.Services.AddScoped<IVectorSimilaritySearch, CosineVectorSimilaritySearch>();
builder.Services.AddScoped<SqlServerVectorStore>();
builder.Services.AddScoped<IVectorStore>(sp => sp.GetRequiredService<SqlServerVectorStore>());
builder.Services.AddScoped<ITicketKnowledgeEmbeddingStore>(sp => sp.GetRequiredService<SqlServerVectorStore>());
builder.Services.AddScoped<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddScoped<IFailurePatternRepository, FailurePatternRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IIngestionStatusRepository, IngestionStatusRepository>();
builder.Services.AddScoped<IProcessedSourceEventRepository, ProcessedSourceEventRepository>();
builder.Services.AddScoped<ITicketAutomationSettingsRepository, TicketAutomationSettingsRepository>();
builder.Services.AddScoped<ITicketAutomationSettingsProvider, TicketAutomationSettingsProvider>();

builder.Services.AddScoped<SqlTicketConnector>();
builder.Services.AddHttpClient<ApiTicketConnector>();
builder.Services.AddHttpClient("AzureOpenAI")
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var aiSettings = sp.GetRequiredService<IOptionsMonitor<AiSettings>>().CurrentValue;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AzureOpenAIHttpClient");
        return CreateAzureOpenAiHttpMessageHandler(aiSettings, logger);
    });

builder.Services.AddScoped<ITicketWorkflowOrchestrator, TicketWorkflowOrchestrator>();
builder.Services.AddHostedService<BackgroundTicketPollingService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PoolSense API",
        Version = "v1",
        Description = "AI-powered pool maintenance assistant API."
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Use a PoolSense JWT bearer token or authenticate through /api/auth/login for the auth cookie."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            []
        }
    });
});

builder.Services.AddScoped<Kernel>(sp =>
{
    var aiSettings = sp.GetRequiredService<IOptionsMonitor<AiSettings>>().CurrentValue;
    ValidateAiSettings(aiSettings);

    var kernelBuilder = Kernel.CreateBuilder();
    if (aiSettings.Http.AllowCertificateRevocationUnknown)
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var chatHttpClient = httpClientFactory.CreateClient("AzureOpenAI");
        var embeddingHttpClient = httpClientFactory.CreateClient("AzureOpenAI");

        kernelBuilder.AddAzureOpenAIChatCompletion(
            deploymentName: aiSettings.Models.Chat,
            endpoint: aiSettings.BaseUrl,
            apiKey: aiSettings.ApiKey,
            httpClient: chatHttpClient);

#pragma warning disable SKEXP0010
        kernelBuilder.AddAzureOpenAIEmbeddingGenerator(
            deploymentName: aiSettings.Models.Embeddings,
            endpoint: aiSettings.BaseUrl,
            apiKey: aiSettings.ApiKey,
            httpClient: embeddingHttpClient);
#pragma warning restore SKEXP0010
    }
    else
    {
        kernelBuilder.AddAzureOpenAIChatCompletion(
            deploymentName: aiSettings.Models.Chat,
            endpoint: aiSettings.BaseUrl,
            apiKey: aiSettings.ApiKey);

#pragma warning disable SKEXP0010
        kernelBuilder.AddAzureOpenAIEmbeddingGenerator(
            deploymentName: aiSettings.Models.Embeddings,
            endpoint: aiSettings.BaseUrl,
            apiKey: aiSettings.ApiKey);
#pragma warning restore SKEXP0010
    }

    return kernelBuilder.Build();
});

builder.Services.AddScoped<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    sp.GetRequiredService<Kernel>()
      .Services
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());


var app = builder.Build();
var spaIndexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupSecurity");
var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
if (string.IsNullOrWhiteSpace(authOptions.JwtSecret) || authOptions.JwtSecret.Length < 32)
{
    startupLogger.LogWarning(
        "Auth:JwtSecret is missing or too short. Configure a strong secret via environment-specific secret storage before production use.");
}

app.Logger.LogInformation(
    "PoolSense API starting on host {MachineName} as user {UserName}. Environment: {EnvironmentName}. ProcessId: {ProcessId}. ContentRoot: {ContentRoot}. OS: {OSDescription}.",
    Environment.MachineName,
    Environment.UserName,
    app.Environment.EnvironmentName,
    Environment.ProcessId,
    app.Environment.ContentRootPath,
    System.Runtime.InteropServices.RuntimeInformation.OSDescription);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
    app.Logger.LogInformation(
        "PoolSense API stopping on host {MachineName} as user {UserName}. ProcessId: {ProcessId}.",
        Environment.MachineName,
        Environment.UserName,
        Environment.ProcessId));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "PoolSense API v1");
        c.RoutePrefix = "swagger";
    });
}

if (HasHttpsBinding(app.Configuration))
{
    app.UseHttpsRedirection();
}

app.UseCors(PoolSenseUiCorsPolicy);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Unhandled HTTP request exception. Method: {Method}. Path: {Path}. RemoteIp: {RemoteIp}. User: {User}.",
            context.Request.Method,
            context.Request.Path.Value,
            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            context.User.Identity?.Name ?? string.Empty);
        throw;
    }
});

if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

if (File.Exists(spaIndexPath))
{
    app.MapFallbackToFile("index.html");
}

app.Run();