using System.Net.Http.Json;
using PoolSense.Api.Models;
using PoolSense.Application.Models;

namespace PoolSense.Api.Connectors;

public class ApiTicketConnector : ITicketSourceConnector
{
    private readonly HttpClient _httpClient;

    public ApiTicketConnector(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<TicketRequest>> GetNewTickets(CancellationToken cancellationToken = default)
    {
        var tickets = await _httpClient.GetFromJsonAsync<List<TicketRequest>>("tickets", cancellationToken);
        return tickets ?? [];
    }

    public async Task<TicketRequest?> GetTicketDetails(string ticketId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return null;
        }

        return await _httpClient.GetFromJsonAsync<TicketRequest>($"tickets/{Uri.EscapeDataString(ticketId)}", cancellationToken);
    }

    public async Task<IReadOnlyList<TicketRequest>> GetNewTickets(ProjectConfig projectConfig, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildAbsoluteUri(projectConfig, "tickets");
        var tickets = await _httpClient.GetFromJsonAsync<List<TicketRequest>>(endpoint, cancellationToken);
        return tickets ?? [];
    }

    public async Task<TicketRequest?> GetTicketDetails(ProjectConfig projectConfig, string ticketId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return null;
        }

        var endpoint = BuildAbsoluteUri(projectConfig, $"tickets/{Uri.EscapeDataString(ticketId)}");
        return await _httpClient.GetFromJsonAsync<TicketRequest>(endpoint, cancellationToken);
    }

    private static string BuildAbsoluteUri(ProjectConfig projectConfig, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(projectConfig);

        if (string.IsNullOrWhiteSpace(projectConfig.ConnectionString))
        {
            throw new InvalidOperationException($"Project '{projectConfig.ProjectId}' is missing a ticket source base URL.");
        }

        return $"{projectConfig.ConnectionString.TrimEnd('/')}/{relativePath}";
    }
}
