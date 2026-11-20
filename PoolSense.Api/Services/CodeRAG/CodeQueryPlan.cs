using System.Text.RegularExpressions;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record CodeQueryPlan(string Query, string Kind, string? Identifier)
{
    public bool IsSymbol => Kind == "symbol";
    public bool IsWorkflow => Kind == "workflow";

    public static string Contextualize(string question, IReadOnlyList<CodeChatTurn> history)
    {
        var dependent = question.Length <= 240 && Regex.IsMatch(question,
            @"\b(it|its|this|that|these|those|above|previous|same|more|again)\b|\b(?:step|option)\s+(?:\d+|one|two|three)\b|\b(?:first|second|third)\s+(?:step|option)\b|^(?:why|how|where)\??$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var previous = history.LastOrDefault(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content;
        return dependent && !string.IsNullOrWhiteSpace(previous)
            ? $"{previous[..Math.Min(previous.Length, 1200)]}\nFollow-up: {question}"
            : question;
    }

    public static CodeQueryPlan Create(string query)
    {
        if (Regex.IsMatch(query, @"\b(flow|workflow|journey|architecture|diagram|graph|connections?|dependencies|relationships|integration|across)\b|end[\s-]+to[\s-]+end",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return new(query, "workflow", null);
        var terms = CodeCandidateSelection.Terms(query);
        var identifiers = terms.Where(term => Regex.IsMatch(term, @"[a-z][A-Z]|[A-Za-z]_[A-Za-z]")).ToArray();
        if (identifiers.Length == 1)
            return new(query, "symbol", identifiers[0]);
        if (Regex.IsMatch(query, @"^\s*[A-Z][A-Za-z0-9_]+\s*$"))
            return new(query, "symbol", query.Trim());
        return new(query, "concept", null);
    }

    public bool MatchesIdentifier(CodeSearchCandidate candidate) => Identifier is not null &&
        candidate.Symbol.Name.Equals(Identifier, StringComparison.Ordinal);
    public int Candidates(int configured) => IsSymbol ? Math.Min(configured, 12) : IsWorkflow && configured >= 20 ? Math.Max(configured, 32) : configured;
    public int Spans(int configured) => IsSymbol ? Math.Min(configured, 6) : IsWorkflow && configured >= 12 ? Math.Max(configured, 18) : configured;
    public int Characters(int configured) => IsSymbol ? Math.Min(configured, 12000) : IsWorkflow && configured >= 24000 ? Math.Max(configured, 36000) : configured;
}
