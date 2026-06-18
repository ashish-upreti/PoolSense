namespace PoolSense.Api.Models;

public sealed class RuntimeTicketAutomationSettings
{
    public bool PollingEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
    /// <summary>Master email kill switch. When false, all outbound emails are suppressed regardless of per-application settings.</summary>
    public bool PoolSenseEmail { get; set; } = true;
}