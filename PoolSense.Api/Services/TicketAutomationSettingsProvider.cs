using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public interface ITicketAutomationSettingsProvider
{
    Task<TicketAutomationSettings> GetAsync(CancellationToken cancellationToken = default);
    Task<RuntimeTicketAutomationSettings> UpdateAsync(RuntimeTicketAutomationSettings settings, CancellationToken cancellationToken = default);
}

public sealed class TicketAutomationSettingsProvider : ITicketAutomationSettingsProvider
{
    private readonly IOptionsMonitor<TicketAutomationSettings> _settingsMonitor;
    private readonly ITicketAutomationSettingsRepository _repository;

    public TicketAutomationSettingsProvider(
        IOptionsMonitor<TicketAutomationSettings> settingsMonitor,
        ITicketAutomationSettingsRepository repository)
    {
        _settingsMonitor = settingsMonitor;
        _repository = repository;
    }

    public async Task<TicketAutomationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var effectiveSettings = Clone(_settingsMonitor.CurrentValue);
        var runtimeSettings = await _repository.GetAsync(cancellationToken);
        if (runtimeSettings is null)
        {
            return effectiveSettings;
        }

        effectiveSettings.PollingEnabled = runtimeSettings.PollingEnabled;
        effectiveSettings.PollIntervalSeconds = runtimeSettings.PollIntervalSeconds;
        return effectiveSettings;
    }

    public async Task<RuntimeTicketAutomationSettings> UpdateAsync(RuntimeTicketAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await _repository.UpsertAsync(settings, cancellationToken);
    }

    private static TicketAutomationSettings Clone(TicketAutomationSettings source)
    {
        return new TicketAutomationSettings
        {
            PollingEnabled = source.PollingEnabled,
            PollIntervalSeconds = source.PollIntervalSeconds,
            ClosedStatusName = source.ClosedStatusName,
            NewStatusName = source.NewStatusName,
            SimilaritySearchLimit = source.SimilaritySearchLimit,
            Email = new EmailDeliverySettings
            {
                Recipient = source.Email.Recipient,
                FromAddress = source.Email.FromAddress,
                DeliveryMode = source.Email.DeliveryMode,
                SmtpHost = source.Email.SmtpHost,
                Port = source.Email.Port,
                TimeoutMs = source.Email.TimeoutMs,
                DatabaseMailProfile = source.Email.DatabaseMailProfile,
                DatabaseMailConnectionName = source.Email.DatabaseMailConnectionName
            }
        };
    }
}