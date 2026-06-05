namespace PoolSense.Api.Models;

public sealed class RuntimeTicketAutomationSettings
{
    public bool PollingEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
}