using System.ComponentModel;
using System.Text.Json;
using CodeRAG.Core.Storage;

namespace CodeRAG.Core.Indexing;

public sealed record TypeScriptIndexResult(IReadOnlyList<Symbol> Symbols, IReadOnlyList<Edge> Edges,
    int ParsedFiles, int ReusedFiles, IReadOnlyList<string> Warnings);

public sealed class TypeScriptIndexer(IProcessRunner? processRunner = null)
{
    private readonly IProcessRunner runner = processRunner ?? new ProcessRunner();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<TypeScriptIndexResult> IndexAsync(string root, IReadOnlyList<string> relativePaths,
        string cachePath, string? previousCachePath = null, CancellationToken cancellationToken = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        CheckLinks(root);
        var paths = relativePaths.Select(relative =>
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split(['/', '\\']).Any(p => p is "" or "." or "..") || relative.Contains(':'))
                throw new InvalidDataException($"Unsafe TypeScript source path: {relative}");
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsWithin(full, root)) throw new InvalidDataException($"Source path escapes root: {relative}");
            CheckLinks(full);
            if (!File.Exists(full)) throw new FileNotFoundException("TypeScript source file is missing.", full);
            if (!new[] { ".ts", ".tsx", ".js", ".jsx" }.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsupported TypeScript source path: {relative}");
            return relative.Replace('\\', '/');
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        cachePath = ValidateOutput(cachePath, root);
        if (previousCachePath is not null) previousCachePath = ValidateOutput(previousCachePath, root);
        var script = Path.Combine(AppContext.BaseDirectory, "Indexing", "TypeScript", "parser.cjs");
        if (!File.Exists(script))
            throw new InvalidOperationException("The deployed TypeScript parser is missing: " + script);
        var compiler = Path.Combine(Path.GetDirectoryName(script)!, "node_modules", "typescript", "lib", "typescript.js");
        if (!File.Exists(compiler))
            throw new InvalidOperationException("The pinned TypeScript compiler is missing. Run npm ci in Indexing\\TypeScript before building/publishing.");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var requestPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".request.json";
        var outputPath = requestPath + ".result.json";
        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                root, paths, cachePath, previousCachePath, outputPath
            }), cancellationToken);
            ProcessResult process;
            try
            {
                process = await runner.RunAsync("node", [script, requestPath], Path.GetDirectoryName(script)!,
                    TimeSpan.FromMinutes(2), cancellationToken);
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Node.js is required for TypeScript indexing. Install Node.js on the server and make node available on PATH.", error);
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"TypeScript compiler indexing failed (exit {process.ExitCode}): {process.Error}");
            using var stream = File.OpenRead(outputPath);
            var result = await JsonSerializer.DeserializeAsync<ParserResult>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("TypeScript parser returned no result.");
            var key = GraphStore.RootKey(root);
            string Id(string descriptorKey) => SourceText.Hash($"{key}\0{descriptorKey}");
            var symbols = result.Symbols.Select(s => new Symbol(Id(s.Id), key, s.FilePath, s.Kind, s.Name,
                s.QualifiedName, s.StartLine, s.EndLine, Id(s.ParentId),
                SourceText.Redact(s.Source), SourceText.Redact(s.Docstring))).ToArray();
            var edges = result.Edges.Select(e => new Edge(Id(e.FromId), Id(e.ToId), e.Kind,
                e.SiteLine, e.SiteColumn, "typescript-syntax")).ToArray();
            return new(symbols, edges, result.ParsedFiles, result.ReusedFiles, result.Warnings);
        }
        finally
        {
            File.Delete(requestPath);
            File.Delete(outputPath);
        }
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ValidateOutput(string path, string root)
    {
        path = Path.GetFullPath(path);
        if (IsWithin(path, root)) throw new InvalidDataException("TypeScript cache/output must be outside the indexed source root.");
        CheckLinks(path);
        return path;
    }

    private static void CheckLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked paths are not allowed for TypeScript indexing: " + current);
        }
    }

    private sealed record ParserSymbol(string Id, string FilePath, string Kind, string Name,
        string QualifiedName, int StartLine, int EndLine, string ParentId, string Source, string Docstring);
    private sealed record ParserEdge(string FromId, string ToId, string Kind, int? SiteLine, int? SiteColumn);
    private sealed record ParserResult(ParserSymbol[] Symbols, ParserEdge[] Edges,
        int ParsedFiles, int ReusedFiles, string[] Warnings);
}
