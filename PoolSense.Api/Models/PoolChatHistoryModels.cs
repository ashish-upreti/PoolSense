using PoolSense.Application.Models;

namespace PoolSense.Api.Models;

/// <summary>Summary row for a saved PoolSense chat session shown in the history rail.</summary>
public sealed record PoolChatSessionSummary(string Id, string Title, IReadOnlyList<string> SelectedGroupIds, bool IsPinned,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

/// <summary>A single stored message with the optional full workflow result payload.</summary>
public sealed record PoolChatStoredMessage(string Role, string Content, TicketWorkflowResult? Result, DateTimeOffset CreatedAt);

/// <summary>Full owned conversation returned when reopening a saved session.</summary>
public sealed record PoolChatSession(PoolChatSessionSummary Session, IReadOnlyList<PoolChatStoredMessage> Messages);

/// <summary>Original context saved atomically when the first exchange creates a conversation.</summary>
public sealed record PoolChatInitialExchange(string Question, TicketWorkflowResult Result);

/// <summary>Optional fields for renaming or pinning a session; omitted fields are left unchanged.</summary>
public sealed record PoolChatSessionPatchRequest
{
    public string? Title { get; init; }
    public bool? IsPinned { get; init; }
}
