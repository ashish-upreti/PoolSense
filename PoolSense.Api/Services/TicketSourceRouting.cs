using System.Text.RegularExpressions;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

public static class TicketSourceRouting
{
    [Flags] private enum Sources { None = 0, Pool = 1, Wiki = 2, Code = 4, All = 7 }
    private const string Alias = @"(?:historical\s+(?:tickets?|incidents?)|incident\s+history|source\s+code|code|pool(?:\s+(?:db|database))?|issues?|tickets?|nyra(?:\s+(?:wiki|kb))?|wiki|knowledge\s+base|repositories|repository|repos?)";
    private const string Commands = @"(?:search|look|find|query|check|consult|use)";
    private const string Qualifiers = @"(?:(?:only|just|in|into|through|at|from|within|the|our|my|for|up)\s+){0,6}";
    private static Sources Kind(string name) =>
        Regex.IsMatch(name, @"^(?:nyra|wiki|knowledge)", RegexOptions.IgnoreCase) ? Sources.Wiki :
        Regex.IsMatch(name, @"(?:code|repo)", RegexOptions.IgnoreCase) ? Sources.Code : Sources.Pool;

    private static (Sources Positive, Sources Negative) Directives(string text)
    {
        var positive = Sources.None;
        var negative = Sources.None;
        var pattern = $@"\b{Commands}\s+{Qualifiers}(?<source>{Alias})(?:\s*(?:and|or|,|&|\+)\s*{Qualifiers}(?<source>{Alias}))*\b";
        foreach (var segment in Regex.Split(text, @"[?;\r\n]+"))
        {
            // Questions ABOUT source integrations are not imperative source selection.
            if (Regex.IsMatch(segment, @"^\s*(what|why|how|when|where|does|is)\b", RegexOptions.IgnoreCase)) continue;
            foreach (Match match in Regex.Matches(segment, pattern, RegexOptions.IgnoreCase))
            {
                if (Regex.IsMatch(segment[(match.Index + match.Length)..], @"^\s+\d{3}\b") && match.Groups["source"].Value.Equals("code", StringComparison.OrdinalIgnoreCase)) continue;
                var before = segment[..match.Index];
                var negated = Regex.IsMatch(before, @"(?:do\s+not|don't|not|never|avoid|exclude)\s*$", RegexOptions.IgnoreCase);
                foreach (Capture capture in match.Groups["source"].Captures)
                    if (negated) negative |= Kind(capture.Value); else positive |= Kind(capture.Value);
            }
            foreach (Match match in Regex.Matches(segment, $@"\b(?:not|avoid|exclude)\s+(?:the\s+)?(?<source>{Alias})\b", RegexOptions.IgnoreCase))
                negative |= Kind(match.Groups["source"].Value);
        }
        return (positive, negative);
    }

    public static TicketSourcePlan? Explicit(string title, string description)
    {
        var text = title + "\n" + description;
        var (positive, negative) = Directives(text);
        if (positive == Sources.None) return null;
        var intent = ProcessAnswerPolicy.ReportsFailure(text) ? ResolutionAnswerIntent.Incident : ResolutionAnswerIntent.Explanation;
        if (positive.HasFlag(Sources.Pool) && negative != Sources.None)
            return new(false, false, false, "Clarification needed", "explicit-conflict", intent,
                "Pool/issues searches include Pool, Wiki and code. That conflicts with the excluded source. Choose Pool/all three or a restricted Wiki/code search.");
        var selected = positive.HasFlag(Sources.Pool) ? Sources.All : positive & ~negative;
        return Create(selected, intent, "explicit");
    }

    public static TicketSourcePlan Classified(string category, string title, string description, bool fallback = false)
    {
        var intent = category == "Info" ? ResolutionAnswerIntent.Explanation : ResolutionAnswerIntent.Incident;
        var selected = category == "Info" ? Sources.Wiki | Sources.Code : Sources.All;
        var directives = Directives(title + "\n" + description);
        selected &= ~directives.Negative;
        return Create(selected, intent, fallback ? "classification-fallback" : "classification");
    }

    public static TicketSourcePlan Background() => Create(Sources.All, ResolutionAnswerIntent.Incident, "background");
    private static TicketSourcePlan Create(Sources sources, ResolutionAnswerIntent intent, string origin)
    {
        if (sources == Sources.None) return new(false, false, false, "Clarification needed", origin, intent, "All sources were excluded. Select Wiki, code, or Pool/all three.");
        var label = sources == Sources.All ? "Pool, Wiki and code" : sources == Sources.Wiki ? "Wiki only" :
            sources == Sources.Code ? "Code only" : sources == (Sources.Wiki | Sources.Code) ? "Wiki and code" : "Selected sources";
        return new(sources.HasFlag(Sources.Pool), sources.HasFlag(Sources.Wiki), sources.HasFlag(Sources.Code), label, origin, intent);
    }
}
