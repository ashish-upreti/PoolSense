using System.Text;
using System.Text.RegularExpressions;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record OkfReadResult(string Status, IReadOnlyList<OkfDocument> Documents, IReadOnlyList<string> Errors);

/// <summary>Local-only bundle consumption. Paths and attestation resources are never executed or fetched.</summary>
public static class OkfLocalBundle
{
    public static string? Resolve(string bundleRoot, string referringDocument, string target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target) || target.StartsWith("//", StringComparison.Ordinal)
                || target.StartsWith(@"\\", StringComparison.Ordinal) || target.Contains(':'))
                return null;
            target = Uri.UnescapeDataString(target.Split('#', '?')[0]).Replace('\\', '/');
            if (target.Contains(':') || target.StartsWith("//", StringComparison.Ordinal) || target.Contains('\0'))
                return null;
            if (!SafeSegments(target) || !SafeSegments(referringDocument.Replace('\\', '/'))) return null;
            var root = Path.GetFullPath(bundleRoot);
            var referring = Path.GetFullPath(Path.Combine(root, referringDocument.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(root, referring)) return null;
            var path = Path.GetFullPath(Path.Combine(target.StartsWith('/') ? root : Path.GetDirectoryName(referring)!,
                target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            return IsWithin(root, path) && !HasReparsePoint(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static OkfReadResult ReadExisting(string bundleRoot, OkfOptions? options = null, string? expectedCommit = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!Directory.Exists(bundleRoot)) return new("missing", [], []);
        var documents = new List<OkfDocument>();
        var errors = new List<string>();
        var entries = 0;
        long bytes = 0;
        try
        {
            var root = Path.GetFullPath(bundleRoot);
            if (HasReparsePoint(root)) return new("invalid", [], ["Bundle paths cannot contain symbolic links or reparse points."]);
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.TryDequeue(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > Math.Clamp(options.MaxTraversalEntries, 1, 10000))
                        return new("limited", documents.AsReadOnly(), [.. errors, "Bundle traversal entry limit exceeded."]);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errors.Add("Skipped a symbolic link or reparse point.");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directory.Depth < Math.Clamp(options.MaxDirectoryDepth, 0, 32))
                            pending.Enqueue((entry, directory.Depth + 1));
                        else errors.Add("Bundle directory depth limit exceeded.");
                        continue;
                    }
                    if (!entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (documents.Count >= Math.Clamp(options.MaxDocuments, 1, 1000))
                        return new("limited", documents.AsReadOnly(), [.. errors, "Bundle document limit exceeded."]);
                    try
                    {
                        bytes += new FileInfo(entry).Length;
                        if (bytes > Math.Clamp(options.MaxBundleBytes, 1024, 8 * 1024 * 1024))
                            return new("limited", documents.AsReadOnly(), [.. errors, "Bundle byte limit exceeded."]);
                        var relative = Path.GetRelativePath(root, entry);
                        var document = OkfParser.Parse(relative, ReadBounded(entry, options.MaxDocumentBytes), options);
                        if (!string.IsNullOrEmpty(expectedCommit) &&
                            document.Metadata.GetValueOrDefault("poolsense_commit") is string commit &&
                            !string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase))
                            return new("stale", [], ["Bundle commit differs from the indexed snapshot."]);
                        documents.Add(document);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                               or DecoderFallbackException)
                    {
                        errors.Add($"Invalid OKF document {Path.GetRelativePath(root, entry)}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("invalid", documents.AsReadOnly(), [.. errors, ex.Message]);
        }
        return new(errors.Count == 0 ? "ready" : "partial", documents.AsReadOnly(), errors.AsReadOnly());
    }

    public static IReadOnlyList<OkfDocument> Traverse(string bundleRoot, string entryDocument, OkfOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var documents = new List<OkfDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Target, int Depth)>();
        pending.Enqueue((entryDocument, 0));
        var budget = Math.Clamp(options.MaxTraversalEntries, 1, 10000);
        while (pending.TryDequeue(out var item) && budget-- > 0 &&
               documents.Count < Math.Clamp(options.MaxDocuments, 1, 1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(bundleRoot, "index.md", item.Target);
            if (path == null || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !seen.Add(path) || !File.Exists(path))
                continue;
            try
            {
                var relative = Path.GetRelativePath(bundleRoot, path).Replace('\\', '/');
                var document = OkfParser.Parse(relative, ReadBounded(path, options.MaxDocumentBytes), options);
                documents.Add(document);
                if (item.Depth >= Math.Clamp(options.MaxDirectoryDepth, 0, 32)) continue;
                var links = Regex.Matches(document.Body, @"\[[^\]\r\n]*\]\(([^)\s]+)\)")
                    .Select(m => m.Groups[1].Value).Concat(document.Sources.Select(s => s.Resource));
                foreach (var link in links.Take(budget))
                {
                    var resolved = Resolve(bundleRoot, relative, link);
                    if (resolved != null)
                        pending.Enqueue((Path.GetRelativePath(bundleRoot, resolved), item.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // Broken and malformed linked documents do not invalidate their referring concept.
            }
        }
        return documents.AsReadOnly();
    }

    internal static string ReadBounded(string path, int maxBytes)
    {
        maxBytes = Math.Clamp(maxBytes, 1, 1024 * 1024);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new InvalidDataException("OKF document byte limit exceeded.");
        var bytes = new byte[maxBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > maxBytes) throw new InvalidDataException("OKF document byte limit exceeded.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, count);
    }

    internal static bool IsWithin(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool SafeSegments(string path) => path.Split('/').All(segment =>
        segment is "." or ".." || (!segment.EndsWith(' ') && !segment.EndsWith('.') &&
            !segment.Any(c => c < 32 || c is ':' or '"' or '<' or '>' or '|' or '*' or '?') &&
            !Regex.IsMatch(segment, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)));

    internal static bool HasReparsePoint(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
