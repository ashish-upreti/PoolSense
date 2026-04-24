using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Models;
using PoolSense.Application.Models;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace PoolSense.Api.Services;

public interface ITicketRecommendationEmailService
{
    Task<bool> SendRecommendationAsync(ProjectConfig project, TicketRequest ticket, TicketWorkflowResult workflowResult, CancellationToken cancellationToken = default);
    string GetConfiguredRecipients(ProjectConfig project);
}

public class TicketRecommendationEmailService : ITicketRecommendationEmailService
{
    private readonly TicketAutomationSettings _settings;
    private readonly ILogger<TicketRecommendationEmailService> _logger;

    public TicketRecommendationEmailService(IOptions<TicketAutomationSettings> settings, ILogger<TicketRecommendationEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string GetConfiguredRecipients(ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return RecommendationEmailRecipientResolver.ResolveRecipients(project, _settings.Email);
    }

    public async Task<bool> SendRecommendationAsync(ProjectConfig project, TicketRequest ticket, TicketWorkflowResult workflowResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(workflowResult);

        var recipients = RecommendationEmailRecipientResolver.ResolveRecipients(project, _settings.Email);

        if (string.IsNullOrWhiteSpace(recipients)
            || string.IsNullOrWhiteSpace(_settings.Email.FromAddress)
            || string.IsNullOrWhiteSpace(_settings.Email.SmtpHost))
        {
            _logger.LogWarning("Ticket recommendation email skipped because SMTP/email settings are incomplete.");
            return false;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_settings.Email.FromAddress),
            Subject = BuildSubject(ticket),
            Body = BuildBody(ticket, workflowResult),
            IsBodyHtml = true
        };

        foreach (var recipient in RecommendationEmailRecipientResolver.SplitRecipients(recipients))
        {
            message.To.Add(recipient);
        }

        using var client = new SmtpClient(_settings.Email.SmtpHost, _settings.Email.Port)
        {
            EnableSsl = false,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = _settings.Email.TimeoutMs
        };

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("Sent recommendation email for source event {SourceEventId} to {Recipients}.", ticket.SourceEventId, recipients);
        return true;
    }

    private static string BuildSubject(TicketRequest ticket) => RecommendationEmailContent.BuildSubject(ticket);

    private static string BuildBody(TicketRequest ticket, TicketWorkflowResult workflowResult) =>
        RecommendationEmailContent.BuildBody(ticket, workflowResult);
}

internal static class RecommendationEmailRecipientResolver
{
    internal static string ResolveRecipients(ProjectConfig project, EmailDeliverySettings emailSettings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(emailSettings);

        return string.Join(
            ", ",
            SplitRecipients(project.EmailRecipients)
                .Concat(SplitRecipients(emailSettings.Recipient))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> SplitRecipients(string? recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients))
        {
            return [];
        }

        return recipients
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Shared email content builders used by both SMTP and Database Mail delivery services.</summary>
internal static class RecommendationEmailContent
{
    internal static string BuildSubject(TicketRequest ticket)
    {
        return $"PoolSense recommendation for IssueID {ticket.SourceEventId}";
    }

    internal static string BuildBody(TicketRequest ticket, TicketWorkflowResult workflowResult)
    {
        var b = new StringBuilder();
        b.AppendLine("<html><body style='font-family:Calibri,Arial,sans-serif;font-size:14px;color:#222;'>");

        // ── Ticket metadata ───────────────────────────────────────────────────
        b.AppendLine("<table style='border-collapse:collapse;margin-bottom:16px;'>");
        Row(b, "Source Event ID", H(ticket.SourceEventId));
        Row(b, "Application",    H(ticket.Application));
        Row(b, "Status",         H(ticket.EventStatusName));
        if (!string.IsNullOrWhiteSpace(ticket.Issue))
            Row(b, "What is the problem you are experiencing", H(ticket.Issue));
        b.AppendLine("</table>");

        b.AppendLine("<hr style='border:none;border-top:1px solid #ccc;margin:12px 0;'>");

        // ── AI sections ───────────────────────────────────────────────────────
        Section(b, "Suggested Root Cause",  H(workflowResult.SuggestedRootCause));
        Section(b, "Suggested Resolution",  H(workflowResult.SuggestedResolution));
        Section(b, "Confidence",            $"{workflowResult.Confidence:P0}");
        Section(b, "Reasoning",             H(workflowResult.Reasoning));

        // ── Similar incidents ─────────────────────────────────────────────────
        b.AppendLine("<p style='margin:16px 0 4px;'><strong>Similar Incidents:</strong></p>");
        b.AppendLine("<table style='border-collapse:collapse;width:100%;'>");
        b.AppendLine("<tr style='background:#f0f0f0;'>");
        b.AppendLine("  <th style='text-align:left;padding:6px 10px;border:1px solid #ccc;'>ID</th>");
        b.AppendLine("  <th style='text-align:left;padding:6px 10px;border:1px solid #ccc;'>Problem</th>");
        b.AppendLine("  <th style='text-align:left;padding:6px 10px;border:1px solid #ccc;'>Resolution</th>");
        b.AppendLine("  <th style='text-align:left;padding:6px 10px;border:1px solid #ccc;'>Link</th>");
        b.AppendLine("</tr>");
        foreach (var incident in workflowResult.SimilarIncidents)
        {
            b.AppendLine("<tr>");
            b.AppendLine($"  <td style='padding:6px 10px;border:1px solid #ccc;vertical-align:top;'>{H(incident.TicketId)}</td>");
            b.AppendLine($"  <td style='padding:6px 10px;border:1px solid #ccc;vertical-align:top;'>{H(incident.Problem)}</td>");
            b.AppendLine($"  <td style='padding:6px 10px;border:1px solid #ccc;vertical-align:top;'>{H(incident.Resolution)}</td>");
            b.AppendLine($"  <td style='padding:6px 10px;border:1px solid #ccc;vertical-align:top;'><a href='https://pool.intel.com/Edit/{H(incident.TicketId)}'>View</a></td>");
            b.AppendLine("</tr>");
        }
        b.AppendLine("</table>");

        b.AppendLine("</body></html>");
        return b.ToString();
    }

    // Writes a two-column metadata row: bold label | value
    private static void Row(StringBuilder b, string label, string value)
    {
        b.AppendLine($"<tr><td style='padding:4px 12px 4px 0;font-weight:bold;vertical-align:top;white-space:nowrap;'>{label}:</td>"
                   + $"<td style='padding:4px 0;'>{value}</td></tr>");
    }

    // Writes a bold heading followed by a paragraph
    private static void Section(StringBuilder b, string heading, string content)
    {
        b.AppendLine($"<p style='margin:16px 0 4px;'><strong>{heading}:</strong></p>");
        b.AppendLine($"<p style='margin:0 0 8px;white-space:pre-wrap;'>{content}</p>");
    }

    // HTML-encodes a string to prevent injection from raw ticket data
    private static string H(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}