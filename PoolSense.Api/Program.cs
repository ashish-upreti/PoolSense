using Microsoft.Extensions.AI;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using PoolSense.Api.Agents;
using PoolSense.Api.Configuration;
using PoolSense.Api.Connectors;
using PoolSense.Api.Data;
using PoolSense.Api.Orchestration;
using PoolSense.Api.Services;
using PoolSense.Api.Logging;

var builder = WebApplication.CreateBuilder(args);
const string PoolSenseUiCorsPolicy = "PoolSenseUi";

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

// Add services to the container.
builder.Services.AddControllers();
builder.Services.Configure<TicketAutomationSettings>(builder.Configuration.GetSection("TicketAutomation"));
builder.Services.AddCors(options =>
{
    options.AddPolicy(PoolSenseUiCorsPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddScoped<ITicketAnalyzerAgent, TicketAnalyzerAgent>();
builder.Services.AddScoped<IResolutionAgent, ResolutionAgent>();
builder.Services.AddScoped<IQueryVariantGeneratorAgent, QueryVariantGeneratorAgent>();
builder.Services.AddScoped<IFailurePatternAgent, FailurePatternAgent>();

builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IKnowledgeEnrichmentService, KnowledgeEnrichmentService>();
builder.Services.AddScoped<IFailurePatternService, FailurePatternService>();
builder.Services.AddScoped<ITicketIngestionService, TicketIngestionService>();
builder.Services.AddScoped<InteractionLogger>();

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

builder.Services.AddScoped<IPgVectorRepository, PgVectorRepository>();
builder.Services.AddScoped<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddScoped<IFailurePatternRepository, FailurePatternRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IIngestionStatusRepository, IngestionStatusRepository>();
builder.Services.AddScoped<IProcessedSourceEventRepository, ProcessedSourceEventRepository>();

builder.Services.AddScoped<SqlTicketConnector>();
builder.Services.AddHttpClient<ApiTicketConnector>();

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
});

var aiSettings = builder.Configuration.GetSection("AiSettings").Get<AiSettings>()
    ?? throw new InvalidOperationException("AiSettings configuration is missing.");

builder.Services.AddScoped<Kernel>(_ =>
{
    var kernelBuilder = Kernel.CreateBuilder();

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

    return kernelBuilder.Build();
});

builder.Services.AddScoped<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    sp.GetRequiredService<Kernel>()
      .Services
      .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());


var app = builder.Build();
var spaIndexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");

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

if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthorization();

app.MapControllers();

if (File.Exists(spaIndexPath))
{
    app.MapFallbackToFile("index.html");
}

app.Run();