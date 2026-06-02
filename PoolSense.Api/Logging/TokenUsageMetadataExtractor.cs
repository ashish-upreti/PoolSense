using System.Collections;
using System.Reflection;

namespace PoolSense.Api.Logging;

public sealed record TokenUsageSnapshot(int PromptTokens, int CompletionTokens, int TotalTokens, bool IsEstimated);

public static class TokenUsageMetadataExtractor
{
    public static TokenUsageSnapshot FromMetadata(object? metadata, string inputText, string outputText = "")
    {
        var usage = Extract(metadata, depth: 0);
        if (usage is not null && usage.TotalTokens > 0)
        {
            return usage;
        }

        var promptTokens = EstimateTokens(inputText);
        var completionTokens = EstimateTokens(outputText);
        return new TokenUsageSnapshot(promptTokens, completionTokens, promptTokens + completionTokens, IsEstimated: true);
    }

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
    }

    private static TokenUsageSnapshot? Extract(object? metadata, int depth)
    {
        if (metadata is null || depth > 4)
        {
            return null;
        }

        if (metadata is IEnumerable<KeyValuePair<string, object?>> dictionary)
        {
            var usage = ExtractFromDictionary(dictionary);
            if (usage is not null)
            {
                return usage;
            }

            foreach (var item in dictionary)
            {
                var nestedUsage = Extract(item.Value, depth + 1);
                if (nestedUsage is not null)
                {
                    return nestedUsage;
                }
            }
        }

        if (metadata is IDictionary nonGenericDictionary)
        {
            var usage = ExtractFromNonGenericDictionary(nonGenericDictionary);
            if (usage is not null)
            {
                return usage;
            }

            foreach (DictionaryEntry item in nonGenericDictionary)
            {
                var nestedUsage = Extract(item.Value, depth + 1);
                if (nestedUsage is not null)
                {
                    return nestedUsage;
                }
            }
        }

        return ExtractFromProperties(metadata, depth);
    }

    private static TokenUsageSnapshot? ExtractFromDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        var values = dictionary.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return BuildSnapshot(
            FindInt(values, "PromptTokens", "InputTokens", "PromptTokenCount", "InputTokenCount"),
            FindInt(values, "CompletionTokens", "OutputTokens", "CompletionTokenCount", "OutputTokenCount"),
            FindInt(values, "TotalTokens", "TotalTokenCount"));
    }

    private static TokenUsageSnapshot? ExtractFromNonGenericDictionary(IDictionary dictionary)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in dictionary)
        {
            if (item.Key is string key)
            {
                values[key] = item.Value;
            }
        }

        return BuildSnapshot(
            FindInt(values, "PromptTokens", "InputTokens", "PromptTokenCount", "InputTokenCount"),
            FindInt(values, "CompletionTokens", "OutputTokens", "CompletionTokenCount", "OutputTokenCount"),
            FindInt(values, "TotalTokens", "TotalTokenCount"));
    }

    private static TokenUsageSnapshot? ExtractFromProperties(object value, int depth)
    {
        var properties = value.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray();

        var promptTokens = FindInt(properties, value, "PromptTokens", "InputTokens", "PromptTokenCount", "InputTokenCount");
        var completionTokens = FindInt(properties, value, "CompletionTokens", "OutputTokens", "CompletionTokenCount", "OutputTokenCount");
        var totalTokens = FindInt(properties, value, "TotalTokens", "TotalTokenCount");

        var snapshot = BuildSnapshot(promptTokens, completionTokens, totalTokens);
        if (snapshot is not null)
        {
            return snapshot;
        }

        foreach (var property in properties.Where(property => property.Name.Contains("Usage", StringComparison.OrdinalIgnoreCase)))
        {
            var nestedValue = property.GetValue(value);
            var nestedUsage = Extract(nestedValue, depth + 1);
            if (nestedUsage is not null)
            {
                return nestedUsage;
            }
        }

        return null;
    }

    private static TokenUsageSnapshot? BuildSnapshot(int? promptTokens, int? completionTokens, int? totalTokens)
    {
        var prompt = Math.Max(0, promptTokens ?? 0);
        var completion = Math.Max(0, completionTokens ?? 0);
        var total = Math.Max(0, totalTokens ?? prompt + completion);

        return total > 0
            ? new TokenUsageSnapshot(prompt, completion, total, IsEstimated: false)
            : null;
    }

    private static int? FindInt(IReadOnlyDictionary<string, object?> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && TryConvertToInt(value, out var intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    private static int? FindInt(PropertyInfo[] properties, object owner, params string[] names)
    {
        foreach (var name in names)
        {
            var property = properties.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property is not null && TryConvertToInt(property.GetValue(owner), out var intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    private static bool TryConvertToInt(object? value, out int intValue)
    {
        switch (value)
        {
            case int integer:
                intValue = integer;
                return true;
            case long longValue when longValue <= int.MaxValue && longValue >= int.MinValue:
                intValue = (int)longValue;
                return true;
            case null:
                intValue = 0;
                return false;
            default:
                return int.TryParse(value.ToString(), out intValue);
        }
    }
}