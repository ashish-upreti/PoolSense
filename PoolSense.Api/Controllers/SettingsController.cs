using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PoolSense.Api.Logging;
using PoolSense.Api.Models;
using PoolSense.Api.Services;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ITicketAutomationSettingsProvider _settingsProvider;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IUserActivityAuditLogger _auditLogger;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ITicketAutomationSettingsProvider settingsProvider,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IUserActivityAuditLogger auditLogger,
        ILogger<SettingsController> logger)
    {
        _settingsProvider = settingsProvider;
        _configuration = configuration;
        _environment = environment;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    [HttpGet("ticket-automation")]
    public async Task<IActionResult> GetTicketAutomationSettings(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsProvider.GetAsync(cancellationToken);
            return Ok(ToResponse(settings, GetDeploymentInfo()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving ticket automation settings.");
            return StatusCode(500, $"An error occurred while retrieving ticket automation settings: {ex.Message}");
        }
    }

    [AllowAnonymous]
    [HttpGet("deployment")]
    public IActionResult GetDeployment()
    {
        return Ok(GetDeploymentInfo());
    }

    [HttpPut("ticket-automation")]
    public async Task<IActionResult> UpdateTicketAutomationSettings([FromBody] RuntimeTicketAutomationSettings request, CancellationToken cancellationToken)
    {
        if (request.PollIntervalSeconds < 10 || request.PollIntervalSeconds > 3600)
        {
            ModelState.AddModelError(nameof(RuntimeTicketAutomationSettings.PollIntervalSeconds), "PollIntervalSeconds must be between 10 and 3600.");
            return ValidationProblem(ModelState);
        }

        try
        {
            var before = await _settingsProvider.GetAsync(cancellationToken);

            var savedSettings = await _settingsProvider.UpdateAsync(new RuntimeTicketAutomationSettings
            {
                PollingEnabled = request.PollingEnabled,
                PollIntervalSeconds = request.PollIntervalSeconds,
                PoolSenseEmail = request.PoolSenseEmail
            }, cancellationToken);

            var effectiveSettings = await _settingsProvider.GetAsync(cancellationToken);
            effectiveSettings.PollingEnabled = savedSettings.PollingEnabled;
            effectiveSettings.PollIntervalSeconds = savedSettings.PollIntervalSeconds;
            effectiveSettings.PoolSenseEmail = savedSettings.PoolSenseEmail;

            var details = $"pollingEnabled: {before.PollingEnabled} → {savedSettings.PollingEnabled}; " +
                          $"pollIntervalSeconds: {before.PollIntervalSeconds} → {savedSettings.PollIntervalSeconds}; " +
                          $"poolSenseEmail: {before.PoolSenseEmail} → {savedSettings.PoolSenseEmail}";
            _logger.LogInformation("Ticket automation settings updated. {Details}", details);
            await _auditLogger.LogAsync("UpdateTicketAutomationSettings", "TicketAutomationSettings", "master_polling", details, cancellationToken: cancellationToken);

            return Ok(ToResponse(effectiveSettings, GetDeploymentInfo()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ticket automation settings.");
            await _auditLogger.LogAsync("UpdateTicketAutomationSettings", "TicketAutomationSettings", "master_polling",
                $"Error: {ex.Message}", success: false, cancellationToken);
            return StatusCode(500, $"An error occurred while updating ticket automation settings: {ex.Message}");
        }
    }

    private object GetDeploymentInfo()
    {
        var environmentName = _environment.EnvironmentName;
        return new
        {
            environmentName,
            environmentLabel = _environment.IsDevelopment() ? "DEV" : "PROD",
            machineName = Environment.MachineName,
            poolSenseDatabaseName = GetDatabaseName(_configuration.GetConnectionString("PoolSenseSqlServer")),
            ticketSourceDatabaseName = GetDatabaseName(_configuration.GetConnectionString("TicketSourceSqlServer"))
        };
    }

    private static string GetDatabaseName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static object ToResponse(Configuration.TicketAutomationSettings settings, object deploymentInfo)
    {
        return new
        {
            deployment = deploymentInfo,
            pollingEnabled = settings.PollingEnabled,
            pollIntervalSeconds = settings.PollIntervalSeconds,
            poolSenseEmail = settings.PoolSenseEmail,
            closedStatusName = settings.ClosedStatusName,
            newStatusName = settings.NewStatusName,
            similaritySearchLimit = settings.SimilaritySearchLimit,
            email = new
            {
                recipient = settings.Email.Recipient,
                fromAddress = settings.Email.FromAddress,
                deliveryMode = settings.Email.DeliveryMode.ToString(),
                smtpHost = settings.Email.SmtpHost,
                port = settings.Email.Port,
                timeoutMs = settings.Email.TimeoutMs,
                databaseMailProfile = settings.Email.DatabaseMailProfile,
                databaseMailConnectionName = settings.Email.DatabaseMailConnectionName
            }
        };
    }
}