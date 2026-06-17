using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
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

    public SettingsController(
        ITicketAutomationSettingsProvider settingsProvider,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _settingsProvider = settingsProvider;
        _configuration = configuration;
        _environment = environment;
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
            var savedSettings = await _settingsProvider.UpdateAsync(new RuntimeTicketAutomationSettings
            {
                PollingEnabled = request.PollingEnabled,
                PollIntervalSeconds = request.PollIntervalSeconds
            }, cancellationToken);

            var effectiveSettings = await _settingsProvider.GetAsync(cancellationToken);
            effectiveSettings.PollingEnabled = savedSettings.PollingEnabled;
            effectiveSettings.PollIntervalSeconds = savedSettings.PollIntervalSeconds;
            return Ok(ToResponse(effectiveSettings, GetDeploymentInfo()));
        }
        catch (Exception ex)
        {
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