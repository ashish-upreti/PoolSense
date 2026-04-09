namespace PoolSense.Api.Agents;

internal static class AiJsonResponseSanitizer
{
    public static string Normalize(string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        var normalized = response.Trim();

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = normalized.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                normalized = normalized[(firstLineBreak + 1)..];
            }

            var closingFenceIndex = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFenceIndex >= 0)
            {
                normalized = normalized[..closingFenceIndex];
            }

            normalized = normalized.Trim();
        }

        var objectStart = normalized.IndexOf('{');
        var arrayStart = normalized.IndexOf('[');
        var start = GetStartIndex(objectStart, arrayStart);

        if (start < 0)
        {
            return normalized;
        }

        var openingToken = normalized[start];
        var closingToken = openingToken == '{' ? '}' : ']';
        var end = normalized.LastIndexOf(closingToken);

        return end > start
            ? normalized[start..(end + 1)].Trim()
            : normalized[start..].Trim();
    }

    private static int GetStartIndex(int objectStart, int arrayStart)
    {
        if (objectStart < 0)
        {
            return arrayStart;
        }

        if (arrayStart < 0)
        {
            return objectStart;
        }

        return Math.Min(objectStart, arrayStart);
    }
}
