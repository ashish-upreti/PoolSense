using CodeRAG.Core.Configuration;
using CodeRAG.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.Json;
using static CodeRAG.Core.Storage.GraphStore;

namespace CodeRAG.Core.Indexing;

public sealed record IndexResult(IndexSummary Summary, IReadOnlyList<string> Warnings, bool Reused = false)
{
    public int ParsedFiles { get; init; }
    public int ReusedFiles { get; init; }
}

public sealed class RepositoryIndexer(IProcessRunner? processRunner = null)
{
    private readonly IProcessRunner processes = processRunner ?? new ProcessRunner();
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", "node_modules", "packages", "testresults", "artifacts", "dist", "build", "coverage", "wwwroot" };
    private sealed record Call(string Name, int Arguments, int? GenericArguments, string? Receiver, int Line, int Column);
    private sealed record Declaration(Symbol Symbol, string Namespace, string TypeName, bool Callable,
        int MinimumArguments, int MaximumArguments, int GenericArguments, string[] BaseTypes, Call[] Calls);
    private sealed record CachedFile(string Hash, Symbol[] Symbols, Declaration[] Declarations, string[] Warnings);
    public static bool IsCurrent(string database, string root)
    {
        if (!IsReady(database, [root])) return false;
        using var db = Open(database, readOnly: true);
        return Convert.ToInt32(Scalar(db, "SELECT count(*) FROM repositories WHERE root=@p0 AND parser=@p1", RootKey(root), Parser)) == 1;
    }
    public static string TypeScriptCachePath(string database) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, ".coderag-typescript", Path.GetFileName(database) + ".json");
    public async Task<IReadOnlyList<IndexResult>> IndexProjectAsync(Project project, string database, bool refresh = false,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (var root in project.Roots)
            if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException($"Repository folder does not exist: {root.Label}: {root.Path}");
        var results = new List<IndexResult>();
        foreach (var root in project.Roots)
            results.Add(await IndexAsync(root.Path, database, refresh, progress, cancellationToken));
        return results;
    }
    public async Task<IndexResult> IndexAsync(string root, string database, bool refresh = false,
        Action<string>? progress = null, CancellationToken cancellationToken = default, string? previousDatabase = null)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository folder does not exist: {root}");
        if (Linked(root)) throw new IOException("Linked repository roots are not indexed.");
        using var db = Open(database);
        var key = RootKey(root);
        if (!refresh && IsReady(database, [root]))
        {
            progress?.Invoke($"Reusing saved index: {root}. Use --refresh to update source changes.");
            return new(Summaries(db).Single(s => s.Root == key), [], true);
        }
        var files = await DiscoverAsync(root, cancellationToken);
        var symbols = new List<Symbol>();
        var declarations = new List<Declaration>();
        var warnings = new List<string>();
        using var previous = previousDatabase is not null && File.Exists(previousDatabase)
            ? Open(previousDatabase, readOnly: true) : null;
        var cacheDb = previous ?? db;
        var canReuse = Convert.ToInt32(Scalar(cacheDb,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='source_files'")) > 0 &&
            Convert.ToInt32(Scalar(cacheDb, "SELECT count(*) FROM repositories WHERE parser=@p0", Parser)) > 0;
        var cachedFiles = new List<(string Path, CachedFile Data)>();
        var parsedFiles = 0;
        var reusedFiles = 0;
        var fileCount = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            progress?.Invoke($"Parsing {++fileCount}/{files.Count}: {relative}");
            if (new FileInfo(file).Length > 2_000_000) { warnings.Add($"{relative}: exceeds 2 MB source limit"); continue; }
            if (Linked(file)) { warnings.Add($"{relative}: linked source skipped"); continue; }
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            string text;
            try
            {
                if (bytes.Contains((byte)0)) throw new DecoderFallbackException();
                text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException) { warnings.Add($"{relative}: binary or unsupported text encoding"); continue; }
            var hash = SourceText.Hash(text);
            CachedFile? cached = null;
            if (canReuse)
            {
                var raw = Query(cacheDb, """
                    SELECT parse_data FROM source_files WHERE file_path=@p0 AND content_hash=@p1
                    AND length(parse_data)<=16777216 LIMIT 1
                    """, reader => reader.GetString(0), relative, hash).FirstOrDefault();
                if (raw is not null)
                {
                    try { cached = JsonSerializer.Deserialize<CachedFile>(raw); }
                    catch (JsonException) { warnings.Add($"{relative}: invalid parse cache; reparsing source"); }
                }
            }
            if (cached is not null)
            {
                cached = Rebase(cached, key, relative);
                symbols.AddRange(cached.Symbols);
                declarations.AddRange(cached.Declarations);
                warnings.AddRange(cached.Warnings);
                cachedFiles.Add((relative, cached));
                reusedFiles++;
                continue;
            }
            var fileSymbols = new List<Symbol>();
            var fileDeclarations = new List<Declaration>();
            var fileWarnings = new List<string>();
            var lines = SourceText.Lines(text);
            var fileSymbol = new Symbol(SourceText.Hash($"{key}\0{relative}\0File"), key, relative, "File",
                Path.GetFileName(file), relative, 1, Math.Max(1, lines.Length), null, "", "");
            fileSymbols.Add(fileSymbol);
            if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                Parse(text, fileSymbol, lines, fileSymbols, fileDeclarations, fileWarnings, cancellationToken);
            for (var start = 0; start < lines.Length;)
            {
                var end = start;
                var length = 0;
                while (end < lines.Length && end - start < 70)
                {
                    var next = SourceText.Length(lines[end]);
                    if (end > start && length + next > 6000) break;
                    length += next;
                    end++;
                }
                fileSymbols.Add(new(SourceText.Hash($"{fileSymbol.Id}\0chunk\0{start}"), key, relative,
                    "SourceChunk", Path.GetFileName(file), $"{relative}:{start + 1}-{end}",
                    start + 1, end, fileSymbol.Id, string.Concat(lines[start..end]), ""));
                if (end == lines.Length) break;
                start = Math.Max(start + 1, end - 10);
            }
            var parsed = Rebase(new(hash, fileSymbols.ToArray(), fileDeclarations.ToArray(), fileWarnings.ToArray()), key, relative);
            symbols.AddRange(parsed.Symbols);
            declarations.AddRange(parsed.Declarations);
            warnings.AddRange(parsed.Warnings);
            cachedFiles.Add((relative, parsed));
            parsedFiles++;
        }
        var edges = BuildEdges(symbols, declarations, cancellationToken);
        var scriptFiles = symbols.Where(symbol => symbol.Kind == "File" &&
            Path.GetExtension(symbol.FilePath).ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx")
            .Select(symbol => symbol.FilePath).ToArray();
        if (scriptFiles.Length > 0)
        {
            var cachePath = TypeScriptCachePath(database);
            string? temporaryCache = null;
            if (Path.GetRelativePath(root, cachePath) is var relativeCache && !relativeCache.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativeCache))
            {
                temporaryCache = Path.Combine(Path.GetTempPath(), "poolsense-typescript", Guid.NewGuid().ToString("N"));
                cachePath = Path.Combine(temporaryCache, "cache.json");
                warnings.Add("TypeScript parse caching requires an index database outside the source root; using a temporary parse cache.");
            }
            try
            {
                var previousCache = previousDatabase is null ? cachePath : TypeScriptCachePath(previousDatabase);
                var scriptIndex = await new TypeScriptIndexer(processes).IndexAsync(root, scriptFiles, cachePath,
                    temporaryCache is null ? previousCache : null, cancellationToken);
                symbols.AddRange(scriptIndex.Symbols);
                edges.AddRange(scriptIndex.Edges);
                warnings.AddRange(scriptIndex.Warnings);
            }
            finally
            {
                if (temporaryCache is not null && Directory.Exists(temporaryCache)) Directory.Delete(temporaryCache, true);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var summary = new IndexSummary(key, DateTimeOffset.UtcNow.ToString("O"),
            symbols.Count(s => s.Kind == "File"), symbols.Count, edges.Count, GraphStore.Version);
        Execute(db, "BEGIN IMMEDIATE");
        try
        {
            Execute(db, "DELETE FROM symbol_search WHERE symbol_id IN (SELECT id FROM symbols WHERE repo_root=@p0)", key);
            Execute(db, "DELETE FROM repositories WHERE root=@p0", key);
            Execute(db, "INSERT INTO repositories VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                key, summary.IndexedAt, summary.FileCount, summary.SymbolCount, summary.EdgeCount, GraphStore.Version, Parser);
            using var insertSymbol = Prepare(db, "INSERT INTO symbols VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)", 11);
            using var insertSearch = Prepare(db, "INSERT INTO symbol_search VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", 6);
            using var insertEdge = Prepare(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,site_line,site_col,resolution) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)", 7);
            using var insertFile = Prepare(db, "INSERT INTO source_files VALUES(@p0,@p1,@p2,@p3)", 4);
            foreach (var s in symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExecutePrepared(insertSymbol,
                    s.Id, key, s.FilePath, s.Kind, s.Name, s.QualifiedName, s.StartLine, s.EndLine, s.ParentId, s.Source, s.Docstring);
                ExecutePrepared(insertSearch, s.Id,
                    SourceText.Searchable(s.Name), SourceText.Searchable(s.QualifiedName), s.Kind,
                    SourceText.Searchable(s.Source), SourceText.Searchable(s.Docstring));
            }
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExecutePrepared(insertEdge,
                    key, edge.FromId, edge.ToId, edge.Kind, edge.SiteLine, edge.SiteColumn, edge.Resolution);
            }
            foreach (var file in cachedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = JsonSerializer.Serialize(file.Data);
                if (data.Length <= 16_777_216) ExecutePrepared(insertFile, key, file.Path, file.Data.Hash, data);
                else warnings.Add($"{file.Path}: parsed source exceeds cache limit; next refresh will reparse it");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Execute(db, "COMMIT");
        }
        catch { Execute(db, "ROLLBACK"); throw; }
        return new(summary, warnings) { ParsedFiles = parsedFiles, ReusedFiles = reusedFiles };
    }

    private static CachedFile Rebase(CachedFile file, string key, string path)
    {
        var fileId = SourceText.Hash($"{key}\0{path}\0File");
        var ids = file.Symbols.Select((symbol, index) => (symbol.Id, Next: index == 0 ? fileId : SourceText.Hash($"{fileId}\0symbol\0{index}")))
            .ToDictionary(item => item.Id, item => item.Next, StringComparer.Ordinal);
        Symbol Map(Symbol symbol) => symbol with { Id = ids[symbol.Id], RepoRoot = key, ParentId = symbol.ParentId is null ? null : ids[symbol.ParentId] };
        return file with
        {
            Symbols = file.Symbols.Select(Map).ToArray(),
            Declarations = file.Declarations.Select(item => item with { Symbol = Map(item.Symbol) }).ToArray()
        };
    }

    public async Task<IReadOnlyList<string>> DiscoverAsync(string root, CancellationToken token = default)
    {
        HashSet<string>? gitFiles = null;
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
        {
            var result = await processes.RunAsync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                root, TimeSpan.FromMinutes(5), token);
            if (result.ExitCode != 0) throw new IOException("Git source discovery failed. Check repository access.");
            gitFiles = new(result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        }
        var resultFiles = new List<string>();
        void Visit(string directory)
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || Linked(path)) continue;
                if (Directory.Exists(path))
                {
                    if (!Excluded.Contains(name)) Visit(path);
                }
                else if (SourceText.Languages.ContainsKey(Path.GetExtension(path)) &&
                    (gitFiles is null || gitFiles.Contains(Path.GetRelativePath(root, path).Replace('\\', '/'))))
                    resultFiles.Add(path);
            }
        }
        Visit(root);
        return resultFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void Parse(string text, Symbol file, string[] lines, List<Symbol> symbols,
        List<Declaration> declarations, List<string> warnings, CancellationToken token)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: token);
        if (tree.GetDiagnostics(token).Any(d => d.Severity == DiagnosticSeverity.Error))
            warnings.Add($"{file.FilePath}: C# parser recovery; graph is best-effort, full text remains searchable.");
        var nodes = new Dictionary<SyntaxNode, Symbol>();
        foreach (var node in tree.GetRoot(token).DescendantNodes())
        {
            token.ThrowIfCancellationRequested();
            var name = Name(node);
            if (name is null) continue;
            var parent = node.Ancestors().FirstOrDefault(nodes.ContainsKey);
            var ns = string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
            var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            var owner = parent is null ? file : nodes[parent];
            var qualified = parent is null ? Join(ns, name) : Join(owner.QualifiedName, name);
            var location = tree.GetLineSpan(node.Span, token);
            var start = location.StartLinePosition.Line + 1;
            var end = Math.Min(lines.Length, tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(
                Math.Max(node.Span.Start, node.Span.End - 1), 0), token).EndLinePosition.Line + 1);
            var docs = string.Join("\n", node.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)).Select(t => t.ToFullString().Trim()));
            var kind = node switch
            {
                ClassDeclarationSyntax => "Class",
                StructDeclarationSyntax => "Struct",
                InterfaceDeclarationSyntax => "Interface",
                EnumDeclarationSyntax => "Enum",
                RecordDeclarationSyntax => "Record",
                _ => "Member"
            };
            var s = new Symbol(SourceText.Hash($"{file.Id}\0{kind}\0{qualified}\0{node.Span.Start}\0{node.Span.Length}"),
                file.RepoRoot, file.FilePath, kind, name, qualified, start, end, owner.Id,
                string.Concat(lines[(start - 1)..end]), SourceText.Take(docs, 2000));
            nodes[node] = s;
            symbols.Add(s);
            var parameters = node switch
            {
                MethodDeclarationSyntax method => method.ParameterList.Parameters,
                ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters,
                LocalFunctionStatementSyntax local => local.ParameterList.Parameters,
                _ => default
            };
            var genericCount = node switch
            {
                MethodDeclarationSyntax method => method.TypeParameterList?.Parameters.Count ?? 0,
                LocalFunctionStatementSyntax local => local.TypeParameterList?.Parameters.Count ?? 0,
                _ => 0
            };
            var calls = new List<Call>();
            if (kind == "Member")
                foreach (var invocation in node.DescendantNodes(n => n == node || n is not LocalFunctionStatementSyntax)
                    .OfType<InvocationExpressionSyntax>())
                {
                    var simple = invocation.Expression switch
                    {
                        SimpleNameSyntax simpleName => simpleName,
                        MemberAccessExpressionSyntax member => member.Name,
                        MemberBindingExpressionSyntax binding => binding.Name,
                        _ => null
                    };
                    if (simple is null) continue;
                    var site = invocation.GetLocation().GetLineSpan().StartLinePosition;
                    calls.Add(new(simple.Identifier.ValueText, invocation.ArgumentList.Arguments.Count,
                        simple is GenericNameSyntax generic ? generic.TypeArgumentList.Arguments.Count : null,
                        invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString() : null,
                        site.Line + 1, site.Character));
                }
            declarations.Add(new(s, ns, type is null ? "" :
                string.Join(".", type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText)),
                node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax,
                parameters.Count(parameter => parameter.Default is null && !parameter.Modifiers.Any(SyntaxKind.ParamsKeyword)),
                parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ParamsKeyword)) ? int.MaxValue : parameters.Count,
                genericCount,
                node is BaseTypeDeclarationSyntax { BaseList: { } bases } ? bases.Types
                    .Select(baseType => baseType.Type.ToString().Replace("global::", "", StringComparison.Ordinal).Split('<')[0]).ToArray() : [],
                calls.ToArray()));
        }
    }
    private static string Join(string a, string b) => a.Length == 0 ? b : a + "." + b;
    private static string? Name(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax n => n.Identifier.ValueText,
        MethodDeclarationSyntax n => n.Identifier.ValueText,
        ConstructorDeclarationSyntax n => n.Identifier.ValueText,
        PropertyDeclarationSyntax n => n.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        LocalFunctionStatementSyntax n => n.Identifier.ValueText,
        _ => null
    };
    private static List<Edge> BuildEdges(List<Symbol> symbols, List<Declaration> declarations, CancellationToken token)
    {
        var edges = symbols.Where(s => s.ParentId is not null).Select(s => new Edge(s.ParentId!, s.Id, "DEFINES")).ToList();
        var types = declarations.Where(d => d.Symbol.Kind is "Class" or "Struct" or "Interface" or "Enum" or "Record")
            .ToLookup(d => d.Symbol.QualifiedName, StringComparer.Ordinal);
        var methods = declarations.Where(d => d.Callable).ToLookup(d => d.Symbol.Name, StringComparer.Ordinal);
        foreach (var d in declarations)
        {
            token.ThrowIfCancellationRequested();
            foreach (var fullName in d.BaseTypes)
                {
                    var possible = types[fullName].Concat(types[Join(d.Namespace, fullName)]).DistinctBy(t => t.Symbol.Id).ToArray();
                    if (possible.Length == 1)
                        edges.Add(new(d.Symbol.Id, possible[0].Symbol.Id, possible[0].Symbol.Kind == "Interface" &&
                            d.Symbol.Kind != "Interface" ? "IMPLEMENTS" : "INHERITS", Resolution: "scoped-syntax"));
                }
            if (d.Symbol.Kind != "Member") continue;
            foreach (var call in d.Calls)
            {
                var candidates = methods[call.Name].Where(m =>
                    (!call.GenericArguments.HasValue || call.GenericArguments == m.GenericArguments) &&
                    call.Arguments >= m.MinimumArguments && call.Arguments <= m.MaximumArguments).ToArray();
                var receiver = call.Receiver;
                var scoped = candidates.Where(m => m.Namespace == d.Namespace && m.TypeName == d.TypeName).ToArray();
                if (receiver is null or "this")
                {
                    if (scoped.Length == 1) candidates = scoped;
                    else continue;
                }
                else
                {
                    candidates = candidates.Where(m => receiver == m.TypeName || receiver == Join(m.Namespace, m.TypeName)).ToArray();
                    if (candidates.Length != 1) continue;
                }
                if (candidates.Length == 1)
                {
                    edges.Add(new(d.Symbol.Id, candidates[0].Symbol.Id, "CALLS", call.Line, call.Column, "scoped-syntax"));
                }
            }
        }
        return edges.Distinct().ToList();
    }
}
