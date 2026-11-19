namespace PoolSense.Api.Models;

public sealed record TicketSourcePlan(bool Pool, bool Wiki, bool Code, string Label, string Origin,
    ResolutionAnswerIntent Intent, string? ClarificationMessage = null);
