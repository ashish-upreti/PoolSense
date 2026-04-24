using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Models;
using PoolSense.Application.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Sends ticket recommendation emails via SQL Server Database Mail (msdb.dbo.sp_send_dbmail)
/// on the TicketSourceSqlServer. Use this when the API host cannot reach the SMTP server directly.
/// </summary>
public class DatabaseMailEmailService : ITicketRecommendationEmailService
{
    private readonly TicketAutomationSettings _settings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseMailEmailService> _logger;

    public DatabaseMailEmailService(
        IOptions<TicketAutomationSettings> settings,
        IConfiguration configuration,
        ILogger<DatabaseMailEmailService> logger)
    {
        _settings = settings.Value;
        _configuration = configuration;
        _logger = logger;
    }

    public string GetConfiguredRecipients(ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return RecommendationEmailRecipientResolver.ResolveRecipients(project, _settings.Email);
    }

    public async Task<bool> SendRecommendationAsync(
        ProjectConfig project,
        TicketRequest ticket,
        TicketWorkflowResult workflowResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(workflowResult);

        var email = _settings.Email;
        var recipients = RecommendationEmailRecipientResolver.ResolveRecipients(project, email);

        if (string.IsNullOrWhiteSpace(recipients)
            || string.IsNullOrWhiteSpace(email.DatabaseMailProfile))
        {
            _logger.LogWarning(
                "Database Mail email skipped: Recipient or DatabaseMailProfile is not configured.");
            return false;
        }

        var connectionString = _configuration.GetConnectionString("TicketSourceSqlServer")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:TicketSourceSqlServer is required for DatabaseMail delivery mode.");

        var subject = RecommendationEmailContent.BuildSubject(ticket);
        var body = RecommendationEmailContent.BuildBody(ticket, workflowResult);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand("msdb.dbo.sp_send_dbmail", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@profile_name", email.DatabaseMailProfile);
    cmd.Parameters.AddWithValue("@recipients", recipients);
        cmd.Parameters.AddWithValue("@subject", subject);
        cmd.Parameters.AddWithValue("@body", body);
        cmd.Parameters.AddWithValue("@body_format", "HTML");

        if (!string.IsNullOrWhiteSpace(email.FromAddress))
        {
            cmd.Parameters.AddWithValue("@from_address", email.FromAddress);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Sent recommendation email via Database Mail for source event {SourceEventId} to {Recipients} using profile '{Profile}'.",
            ticket.SourceEventId, recipients, email.DatabaseMailProfile);

        return true;
    }
}
