using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record OkfSearchMetrics(bool CatalogCacheHit, long CatalogBytesRead, long ContentBytesRead,
    int ContextCharacters, IReadOnlyList<string> SelectedConcepts, IReadOnlyList<string> NeighborConcepts, int SourceLookups);

internal sealed record OkfCatalogEntry(string Id, string Path, string Title, string Type, string Description,
    string Status, OkfTrust Trust, DateTimeOffset? StaleAfter, string[] MetadataTerms, string[] BodyTerms, OkfDocument Metadata);
internal sealed record OkfCatalogData(string Status, IReadOnlyList<OkfCatalogEntry> Entries,
    IReadOnlyList<string> Errors, long BytesRead, bool HasIndex);
internal sealed record OkfSelectedConcept(OkfDocument Document, string Section, bool Neighbor, double Relevance);
internal sealed record OkfSelection(string Status, IReadOnlyList<OkfSelectedConcept> Concepts,
    IReadOnlyList<string> Errors, OkfSearchMetrics Metrics);

internal static class OkfCatalog
{
    private const int CacheEntries = 8;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheItem> Cache = new(StringComparer.Ordinal);
    private sealed record CacheItem(string Root, Lazy<OkfCatalogData> Data)
    {
        public long LastUsed { get; set; } = Environment.TickCount64;
    }

    public static void Forget(string snapshotFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotFolder)) + Path.DirectorySeparatorChar;
        lock (CacheLock)
            foreach (var key in Cache.Where(entry => entry.Value.Root.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
                Cache.Remove(key);
    }

    public static OkfCatalogWarmupResult Warm(string bundleRoot, string commit, OkfOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Enabled) return new("disabled", 0, 0, false);
        if (!Directory.Exists(bundleRoot)) return new("missing", 0, 0, false);
        var catalog = GetOrBuild(bundleRoot, commit, options, token, out var hit);
        return new(catalog.Status, catalog.Entries.Count, hit ? 0 : catalog.BytesRead, hit);
    }

    public static OkfSelection Select(string bundleRoot, string commit, string query, OkfOptions options, CancellationToken token)
    {
        var empty = new OkfSearchMetrics(false, 0, 0, 0, [], [], 0);
        if (!Directory.Exists(bundleRoot)) return new("missing", [], [], empty);
        var catalog = GetOrBuild(bundleRoot, commit, options, token, out var hit);
        var root = Path.GetFullPath(bundleRoot);
        token.ThrowIfCancellationRequested();
        if (catalog.Status is "stale" or "missing" or "invalid")
            return new(catalog.Status, [], catalog.Errors, empty with { CatalogCacheHit = hit, CatalogBytesRead = hit ? 0 : catalog.BytesRead });
        var terms = Terms(query);
        if (terms.Length == 0) return new("empty", [], catalog.Errors, empty with { CatalogCacheHit = hit });
        var now = DateTimeOffset.UtcNow;
        double Score(OkfCatalogEntry entry) =>
            terms.Count(term => entry.MetadataTerms.Contains(term, StringComparer.OrdinalIgnoreCase)) * 3 +
            terms.Count(term => entry.BodyTerms.Contains(term, StringComparer.OrdinalIgnoreCase)) +
            (entry.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 4 : 0);
        var ranked = catalog.Entries.Select(entry => (Entry: entry, Score: Score(entry))).Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.StaleAfter <= now)
            .ThenBy(candidate => candidate.Entry.Status switch { "draft" => 1, "deprecated" => 2, _ => 0 })
            .ThenByDescending(candidate => candidate.Entry.Trust)
            .ThenBy(candidate => candidate.Entry.Id, StringComparer.Ordinal).ToArray();
        var selected = new List<OkfSelectedConcept>();
        var errors = catalog.Errors.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(OkfCatalogEntry Entry, int Depth)>();
        foreach (var candidate in ranked.Take(Math.Clamp(options.MaxSelectedConcepts, 1, 8))) pending.Enqueue((candidate.Entry, 0));
        long contentBytes = 0;
        var characters = 0;
        var neighbors = 0;
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(next.Entry.Id) || next.Depth > 0 && neighbors >= Math.Clamp(options.MaxNeighborConcepts, 0, 4)) continue;
            if (characters >= Math.Clamp(options.MaxReadCharacters, 256, 24000)) break;
            var path = OkfLocalBundle.Resolve(root, "index.md", next.Entry.Path);
            if (path is null || !File.Exists(path)) { errors.Add("Selected concept is no longer available."); continue; }
            var document = next.Entry.Metadata with { Body = OkfParser.ReadBody(OkfLocalBundle.ReadBounded(path, options.MaxDocumentBytes)) };
            contentBytes += new FileInfo(path).Length;
            if (document.Metadata.GetValueOrDefault("poolsense_commit") is string documentCommit &&
                !documentCommit.Equals(commit, StringComparison.OrdinalIgnoreCase))
                return new("stale", [], ["Selected concept commit differs from snapshot."], empty);
            var remaining = Math.Clamp(options.MaxReadCharacters, 256, 24000) - characters;
            var section = Section(document.Body, terms, remaining);
            if (section.Length == 0) continue;
            characters += section.Length;
            selected.Add(new(document, section, next.Depth > 0,
                Math.Clamp(Score(next.Entry) / (terms.Length * 4d), next.Depth > 0 ? .5 : 0, 1)));
            if (next.Depth > 0) neighbors++;
            if (next.Depth >= Math.Clamp(options.MaxNeighborHops, 0, 2)) continue;
            var links = Regex.Matches(section, @"\[[^\]\r\n]*\]\(([^)\s]+)\)", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                .Select(match => match.Groups[1].Value);
            var cited = Regex.Matches(section, @"\[\^([^\]\r\n]+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            links = links.Concat(document.Sources.Where(source => source.Id is not null && cited.Contains(source.Id)).Select(source => source.Resource));
            foreach (var target in links.Distinct(StringComparer.Ordinal).Take(16))
            {
                var resolved = OkfLocalBundle.Resolve(root, next.Entry.Path, target);
                if (resolved is null) continue;
                var relative = Path.GetRelativePath(root, resolved).Replace('\\', '/');
                var neighbor = catalog.Entries.FirstOrDefault(entry => entry.Path == relative);
                if (neighbor is null || seen.Contains(neighbor.Id)) continue;
                pending.Enqueue((neighbor, next.Depth + 1));
            }
        }
        return new(selected.Count > 0 ? catalog.Status : "empty", selected.AsReadOnly(), errors.AsReadOnly(),
            new(hit, hit ? 0 : catalog.BytesRead, contentBytes, characters,
                selected.Where(value => !value.Neighbor).Select(value => value.Document.Id).ToArray(),
                selected.Where(value => value.Neighbor).Select(value => value.Document.Id).ToArray(), 0));
    }

    private static OkfCatalogData GetOrBuild(string bundleRoot, string commit, OkfOptions options,
        CancellationToken token, out bool hit)
    {
        var manifest = Manifest(bundleRoot, options, token);
        var root = Path.GetFullPath(bundleRoot);
        var key = root + "\0" + commit + "\0" + JsonSerializer.Serialize(options) + "\0" + manifest.Signature;
        CacheItem item;
        lock (CacheLock)
        {
            hit = Cache.TryGetValue(key, out item!);
            if (!hit)
            {
                if (Cache.Count >= CacheEntries)
                    Cache.Remove(Cache.MinBy(entry => entry.Value.LastUsed).Key);
                item = new(root, new(() => Build(root, commit, options, manifest.Bytes, token), LazyThreadSafetyMode.ExecutionAndPublication));
                Cache.Add(key, item);
            }
            item.LastUsed = Environment.TickCount64;
        }

        try { return item.Data.Value; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            lock (CacheLock)
                if (Cache.GetValueOrDefault(key) == item) Cache.Remove(key);
            hit = false;
            return Build(root, commit, options, manifest.Bytes, token);
        }
        catch
        {
            lock (CacheLock)
                if (Cache.GetValueOrDefault(key) == item) Cache.Remove(key);
            throw;
        }
    }

    private static string Section(string body, string[] terms, int budget)
    {
        var sections = Regex.Split(body, @"(?m)(?=^#{1,6} )", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var ranked = sections.Where(text => !string.IsNullOrWhiteSpace(text)).Select((text, index) =>
            {
                var words = Terms(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (Text: text, Index: index, Score: terms.Count(words.Contains));
            })
            .OrderByDescending(section => section.Score).ThenBy(section => section.Index).ToArray();
        var selected = (ranked.Any(section => section.Score > 0) ? ranked.Where(section => section.Score > 0).Take(2) : ranked.Take(1))
            .OrderBy(section => section.Index).Select(section => section.Text);
        var content = string.Join("\n", selected);
        return content[..Math.Min(content.Length, budget)];
    }

    internal static string[] Terms(string text) => Regex.Matches(
            Regex.Replace(text[..Math.Min(text.Length, 128000)], @"(?<=[a-z0-9])(?=[A-Z])", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)),
            @"[\p{L}\p{N}]{2,}", RegexOptions.None, TimeSpan.FromMilliseconds(100))
        .Select(match => match.Value.ToLowerInvariant()).Where(term => !StopWords.Contains(term))
        .Distinct(StringComparer.Ordinal).Take(512).ToArray();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    { "how", "what", "which", "where", "why", "does", "do", "the", "are", "is", "and", "or", "for", "with", "from", "to", "that", "this", "please", "show", "me", "can", "of", "an", "in", "it", "as", "by" };

    private static OkfCatalogData Build(string root, string commit, OkfOptions options, long bytes, CancellationToken token)
    {
        var read = OkfLocalBundle.ReadExisting(root, options, commit, token);
        var index = read.Documents.FirstOrDefault(document => document.Id == "index" && document.Kind == OkfDocumentKind.Index);
        var entries = read.Documents.Where(document => document.Kind == OkfDocumentKind.Concept).Select(document =>
        {
            var description = document.Metadata.GetValueOrDefault("description") as string ?? "";
            var tags = JsonSerializer.Serialize(document.Metadata.GetValueOrDefault("tags"));
            var aliases = JsonSerializer.Serialize(document.Metadata.GetValueOrDefault("aliases"));
            var indexLine = index?.Body.Split('\n').FirstOrDefault(line => line.Contains(document.Id + ".md", StringComparison.Ordinal)) ?? "";
            return new OkfCatalogEntry(document.Id, document.Id + ".md", document.Title, document.Type ?? "", description,
                document.Status, document.Trust, OkfParser.Timestamp(document.Metadata.GetValueOrDefault("stale_after")),
                Terms($"{document.Title} {document.Type} {description} {tags} {aliases} {indexLine}"), Terms(document.Body),
                document with { Body = "", RawMarkdown = "" });
        }).ToArray();
        return new(read.Status, entries, read.Errors, bytes, index is not null);
    }

    private static (string Signature, long Bytes) Manifest(string root, OkfOptions options, CancellationToken token)
    {
        if (OkfLocalBundle.HasReparsePoint(root)) throw new InvalidDataException("OKF catalog paths cannot contain reparse points.");
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var stamps = new List<string>();
        long bytes = 0;
        var entries = 0;
        while (pending.TryDequeue(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > Math.Clamp(options.MaxTraversalEntries, 1, 10000)) throw new InvalidDataException("OKF catalog traversal limit exceeded.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("OKF catalog paths cannot contain reparse points.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (directory.Depth < Math.Clamp(options.MaxDirectoryDepth, 0, 32)) pending.Enqueue((path, directory.Depth + 1));
                    continue;
                }
                if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                var file = new FileInfo(path);
                bytes += file.Length;
                if (bytes > Math.Clamp(options.MaxBundleBytes, 1024, 8 * 1024 * 1024)) throw new InvalidDataException("OKF catalog byte limit exceeded.");
                stamps.Add($"{Path.GetRelativePath(root, path)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}");
            }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', stamps)))), bytes);
    }
}
