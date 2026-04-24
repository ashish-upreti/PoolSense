namespace PoolSense.Api.Configuration;

public sealed class TicketAutomationSettings
{
    public bool PollingEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public string ClosedStatusName { get; set; } = "Closed";
    public string NewStatusName { get; set; } = "New";
    public string SourceDatabaseName { get; set; } = string.Empty;
    public int SimilaritySearchLimit { get; set; } = 5;
    public EmailDeliverySettings Email { get; set; } = new();
}

public enum EmailDeliveryMode
{
    /// <summary>Send directly via SMTP from the API process.</summary>
    Smtp,
    /// <summary>Relay through SQL Server Database Mail (msdb.dbo.sp_send_dbmail) on the TicketSourceSqlServer.</summary>
    DatabaseMail
}

public sealed class EmailDeliverySettings
{
    public string Recipient { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    /// <summary>Controls which delivery mechanism is used. Default: Smtp.</summary>
    public EmailDeliveryMode DeliveryMode { get; set; } = EmailDeliveryMode.Smtp;
    // ?? SMTP settings (used when DeliveryMode = Smtp) ??????????????????????
    public string SmtpHost { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    /// <summary>SMTP connection/send timeout in milliseconds. Default: 30000 (30s).</summary>
    public int TimeoutMs { get; set; } = 30_000;
    // ?? Database Mail settings (used when DeliveryMode = DatabaseMail) ?????
    /// <summary>Database Mail profile name configured in msdb on the TicketSourceSqlServer.</summary>
    public string DatabaseMailProfile { get; set; } = string.Empty;
}