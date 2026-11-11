using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class OkfOptions
{
    public bool Enabled { get; set; } = true;
    public bool EnableLlmEnrichment { get; set; }
    public int MaxDocumentBytes { get; set; } = 128 * 1024;
    public int MaxYamlNodes { get; set; } = 2048;
    public int MaxYamlDepth { get; set; } = 16;
    public int MaxDocuments { get; set; } = 64;
    public int MaxDirectoryDepth { get; set; } = 8;
    public int MaxTraversalEntries { get; set; } = 512;
    public int MaxSymbols { get; set; } = 80;
    public int MaxResults { get; set; } = 12;
    public int MaxBundleBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxSelectedConcepts { get; set; } = 4;
    public int MaxNeighborConcepts { get; set; } = 2;
    public int MaxNeighborHops { get; set; } = 1;
    public int MaxReadCharacters { get; set; } = 12000;
    public int MaxLlmInputCharacters { get; set; } = 12000;
    public int MaxLlmResponseCharacters { get; set; } = 6000;
    public int LlmTimeoutSeconds { get; set; } = 30;
}

public enum OkfDocumentKind { Concept, Index, Log }
public enum OkfTrust { Unverified, MachineConfirmed, HumanReviewed }
public sealed record OkfVerification(string By, DateTimeOffset? At);
public sealed record OkfSource(string? Id, string Resource, IReadOnlyDictionary<string, object?> Metadata);

public sealed record OkfDocument(
    string Id, OkfDocumentKind Kind, string? Type, string Body,
    IReadOnlyDictionary<string, object?> Metadata, string RawMarkdown,
    IReadOnlyList<OkfSource> Sources, IReadOnlyList<OkfVerification> Verified)
{
    public string Title => Metadata.GetValueOrDefault("title") as string ?? Id.Split('/').Last();
    public string Status => Metadata.GetValueOrDefault("status") as string ?? "stable";
    public OkfTrust Trust => Verified.Any(v => v.By.StartsWith("human:", StringComparison.Ordinal))
        ? OkfTrust.HumanReviewed : Verified.Count > 0 ? OkfTrust.MachineConfirmed : OkfTrust.Unverified;
    public bool IsStale(DateTimeOffset now) =>
        OkfParser.Timestamp(Metadata.GetValueOrDefault("stale_after")) is { } at && now >= at;
    public string ToMarkdown() => RawMarkdown;
}

public static class OkfParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> SafeTags = new(StringComparer.Ordinal)
    {
        "tag:yaml.org,2002:str", "tag:yaml.org,2002:bool", "tag:yaml.org,2002:int",
        "tag:yaml.org,2002:float", "tag:yaml.org,2002:null", "tag:yaml.org,2002:timestamp",
        "tag:yaml.org,2002:seq", "tag:yaml.org,2002:map"
    };

    public static OkfDocument Parse(string relativePath, string markdown, OkfOptions? options = null)
    {
        options ??= new();
        if (Utf8.GetByteCount(markdown) > Math.Clamp(options.MaxDocumentBytes, 1, 1024 * 1024))
            throw new InvalidDataException("OKF document byte limit exceeded.");
        relativePath = relativePath.Replace('\\', '/');
        if (relativePath.StartsWith('/') || relativePath.Split('/').Any(s => s is ".." or "." or "")
            || !relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid bundle-relative document path.");
        var file = relativePath.Split('/').Last();
        var kind = file.Equals("index.md", StringComparison.OrdinalIgnoreCase) ? OkfDocumentKind.Index
            : file.Equals("log.md", StringComparison.OrdinalIgnoreCase) ? OkfDocumentKind.Log : OkfDocumentKind.Concept;
        var normalized = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n");
        var body = normalized;
        IReadOnlyDictionary<string, object?> metadata = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
        var hasFrontmatter = normalized.StartsWith("---\n", StringComparison.Ordinal);
        if (hasFrontmatter)
        {
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            while (end >= 0 && end + 4 < normalized.Length && normalized[end + 4] != '\n')
                end = normalized.IndexOf("\n---", end + 4, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("Missing OKF frontmatter delimiter.");
            var yaml = normalized[4..end];
            GuardYaml(yaml, options);
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yaml));
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
                    throw new InvalidDataException("OKF frontmatter must be one mapping.");
                metadata = ReadMapping(map);
            }
            catch (YamlException ex) { throw new InvalidDataException("Malformed OKF YAML.", ex); }
            catch (ArgumentException ex) { throw new InvalidDataException("Malformed OKF YAML mapping.", ex); }
            body = normalized[Math.Min(end + 5, normalized.Length)..];
        }
        var type = metadata.GetValueOrDefault("type") as string;
        if (kind == OkfDocumentKind.Concept && string.IsNullOrWhiteSpace(type))
            throw new InvalidDataException("OKF concepts require a non-empty type.");
        if (kind == OkfDocumentKind.Index && hasFrontmatter &&
            (relativePath.Contains('/') || metadata.Keys.Any(k => k != "okf_version")))
            throw new InvalidDataException("Only a root index may declare okf_version frontmatter.");
        if (kind == OkfDocumentKind.Log && (hasFrontmatter ||
            Regex.Matches(body, @"(?m)^##\s+(.+)$").Any(m =>
                !DateOnly.TryParseExact(m.Groups[1].Value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))))
            throw new InvalidDataException("OKF logs use ISO date headings and no frontmatter.");
        var sources = new List<OkfSource>();
        if (metadata.GetValueOrDefault("sources") is { } rawSources)
        {
            if (rawSources is not IReadOnlyList<object?> list) throw new InvalidDataException("sources must be a list.");
            foreach (var item in list)
            {
                if (item is not IReadOnlyDictionary<string, object?> entry ||
                    entry.GetValueOrDefault("resource") is not string resource || string.IsNullOrWhiteSpace(resource))
                    throw new InvalidDataException("Each source requires resource.");
                sources.Add(new(entry.GetValueOrDefault("id") as string, resource, entry));
            }
        }
        var verified = new List<OkfVerification>();
        var rawVerified = metadata.GetValueOrDefault("verified");
        var events = rawVerified is IReadOnlyDictionary<string, object?> single ? new object?[] { single }
            : rawVerified as IReadOnlyList<object?> ?? [];
        foreach (var item in events)
            if (item is IReadOnlyDictionary<string, object?> entry && entry.GetValueOrDefault("by") is string actor)
                verified.Add(new(actor, Timestamp(entry.GetValueOrDefault("at"))));
        return new(relativePath[..^3], kind, type, body, metadata, markdown,
            sources.AsReadOnly(), verified.AsReadOnly());
    }

    internal static DateTimeOffset? Timestamp(object? value)
    {
        var text = value as string;
        return text != null && Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    internal static string ReadBody(string markdown)
    {
        var normalized = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return normalized;
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        while (end >= 0 && end + 4 < normalized.Length && normalized[end + 4] != '\n')
            end = normalized.IndexOf("\n---", end + 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Missing OKF frontmatter delimiter.");
        return normalized[Math.Min(end + 5, normalized.Length)..];
    }

    private static void GuardYaml(string yaml, OkfOptions options)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            var count = 0;
            while (parser.MoveNext())
            {
                if (++count > Math.Clamp(options.MaxYamlNodes, 1, 10000))
                    throw new InvalidDataException("OKF YAML node limit exceeded.");
                if (parser.Current is AnchorAlias) throw new InvalidDataException("OKF YAML aliases are disabled.");
                if (parser.Current is NodeEvent node &&
                    (!node.Anchor.IsEmpty || (!node.Tag.IsEmpty && !SafeTags.Contains(node.Tag.ToString()))))
                    throw new InvalidDataException("OKF YAML anchors and non-core tags are disabled.");
                if (parser.Current is MappingStart or SequenceStart &&
                    ++depth > Math.Clamp(options.MaxYamlDepth, 1, 64))
                    throw new InvalidDataException("OKF YAML depth limit exceeded.");
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
        }
        catch (YamlException ex) { throw new InvalidDataException("Malformed OKF YAML.", ex); }
    }

    private static IReadOnlyDictionary<string, object?> ReadMapping(YamlMappingNode node)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in node.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } key } || !result.TryAdd(key, ReadValue(pair.Value)))
                throw new InvalidDataException("OKF metadata keys must be unique scalar strings.");
        }
        return new ReadOnlyDictionary<string, object?>(result);
    }

    private static object? ReadValue(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        YamlMappingNode map => ReadMapping(map),
        YamlSequenceNode list => Array.AsReadOnly(list.Children.Select(ReadValue).ToArray()),
        _ => throw new InvalidDataException("Unsupported YAML node.")
    };
}
