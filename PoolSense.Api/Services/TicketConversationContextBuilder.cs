using System.Text.RegularExpressions;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public static class TicketConversationContextBuilder
{
    public const int MaxTurns = 6;
    public const int MaxUserCharacters = 1200;
    public const int MaxAssistantCharacters = 2400;
    public const int MaxTotalCharacters = 9000;
    public const string WithheldCodeAnswer = "The earlier assistant answer used repository evidence and is withheld. Retrieve currently authorized code again; do not infer its contents.";

    public static bool SameScope(IEnumerable<string>? first, IEnumerable<string>? second) =>
        NormalizeScope(first).SetEquals(NormalizeScope(second));

    private static HashSet<string> NormalizeScope(IEnumerable<string>? scope) =>
        (scope ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static TicketConversationContext Build(PoolChatSession session)
    {
        var turns = new List<TicketConversationTurn>();
        var remaining = MaxTotalCharacters;
        foreach (var message in session.Messages.Where(message => message.Role is "user" or "assistant").TakeLast(MaxTurns).Reverse())
        {
            if (remaining == 0) break;
            string content;
            if (message.Role == "user")
            {
                content = Plain(message.Content, MaxUserCharacters);
            }
            else if (message.Result?.CodeDocuments.Count > 0)
            {
                // Never re-send code-derived claims or snippets after access may have changed.
                content = WithheldCodeAnswer;
            }
            else if (message.Result is { } result)
            {
                content = $"Previous hypothesis (not evidence): {Plain(result.SuggestedRootCause, 350)}\n" +
                    $"Previous recommendation (not evidence): {Plain(result.SuggestedResolution, 1150)}\n" +
                    $"Previous analysis (not evidence): {Plain(result.DetailedAnalysisMarkdown, 800)}";
            }
            else
            {
                // Legacy unstructured assistant content has no provenance; do not forward it.
                content = "Earlier assistant text is unavailable as trusted context. Use the user's questions and fresh evidence.";
            }
            var limit = Math.Min(remaining, message.Role == "user" ? MaxUserCharacters : MaxAssistantCharacters);
            content = content[..Math.Min(content.Length, limit)];
            if (content.Length == 0) continue;
            turns.Add(new(message.Role, content));
            remaining -= content.Length;
        }
        turns.Reverse();
        return new(turns);
    }

    private static string Plain(string? value, int limit)
    {
        var text = value ?? "";
        text = text[..Math.Min(text.Length, 12000)];
        text = Regex.Replace(text, @"(?ms)^[ \t]*(`{3,}|~{3,}).*?(?:^[ \t]*\1[^\r\n]*|\z)", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"<[^>]{0,1000}>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return text[..Math.Min(text.Length, limit)];
    }
}
