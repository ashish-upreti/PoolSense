namespace PoolSense.Api.Models;

/// <summary>Server-only context loaded from owned history, never bound from an HTTP request.</summary>
public sealed record TicketConversationTurn(string Role, string Content);

public sealed record TicketConversationContext(IReadOnlyList<TicketConversationTurn> Turns)
{
    public static TicketConversationContext Empty { get; } = new([]);
    public bool HasTurns => Turns.Count > 0;
}
