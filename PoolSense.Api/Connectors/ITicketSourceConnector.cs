using PoolSense.Application.Models;
using PoolSense.Api.Models;

namespace PoolSense.Api.Connectors;

public interface ITicketSourceConnector
{
    Task<IReadOnlyList<TicketRequest>> GetNewTickets(CancellationToken cancellationToken = default);
    Task<TicketRequest?> GetTicketDetails(string ticketId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TicketRequest>> GetNewTickets(ProjectConfig projectConfig, CancellationToken cancellationToken = default);
    Task<TicketRequest?> GetTicketDetails(ProjectConfig projectConfig, string ticketId, CancellationToken cancellationToken = default);
}
