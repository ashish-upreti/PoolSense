using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Models;
using PoolSense.Api.Services;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ITicketAutomationSettingsProvider _settingsProvider;

    public SettingsController(ITicketAutomationSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    [HttpGet("ticket-automation")]
    public async Task<IActionResult> GetTicketAutomationSettings(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsProvider.GetAsync(cancellationToken);
            return Ok(ToResponse(settings));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving ticket automation settings: {ex.Message}");
        }
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
            return Ok(ToResponse(effectiveSettings));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while updating ticket automation settings: {ex.Message}");
        }
    }

    private static object ToResponse(Configuration.TicketAutomationSettings settings)
    {
        return new
        {
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
                databaseMailProfile = settings.Email.DatabaseMailProfile
            }
        };
    }
}