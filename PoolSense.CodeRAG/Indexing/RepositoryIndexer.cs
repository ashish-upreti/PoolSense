using CodeRAG.Core.Configuration;
using CodeRAG.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using static CodeRAG.Core.Storage.GraphStore;

namespace CodeRAG.Core.Indexing;

public sealed record IndexResult(IndexSummary Summary, IReadOnlyList<string> Warnings, bool Reused = false);

public sealed class RepositoryIndexer(IProcessRunner? processRunner = null)
{
    private readonly IProcessRunner processes = processRunner ?? new ProcessRunner();
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", "node_modules", "packages", "testresults", "artifacts", "dist", "build", "coverage", "wwwroot" };
    private sealed record Declaration(Symbol Symbol, SyntaxNode Node, string Namespace, string TypeName);
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
        Action<string>? progress = null, CancellationToken cancellationToken = default)
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
            var lines = SourceText.Lines(text);
            var fileSymbol = new Symbol(SourceText.Hash($"{key}\0{relative}\0File"), key, relative, "File",
                Path.GetFileName(file), relative, 1, Math.Max(1, lines.Length), null, "", "");
            symbols.Add(fileSymbol);
            if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                Parse(text, fileSymbol, lines, symbols, declarations, warnings, cancellationToken);
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
                symbols.Add(new(SourceText.Hash($"{fileSymbol.Id}\0chunk\0{start}"), key, relative,
                    "SourceChunk", Path.GetFileName(file), $"{relative}:{start + 1}-{end}",
                    start + 1, end, fileSymbol.Id, string.Concat(lines[start..end]), ""));
                if (end == lines.Length) break;
                start = Math.Max(start + 1, end - 10);
            }
        }
        var edges = BuildEdges(symbols, declarations, cancellationToken);
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
            foreach (var s in symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(db, "INSERT INTO symbols VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)",
                    s.Id, key, s.FilePath, s.Kind, s.Name, s.QualifiedName, s.StartLine, s.EndLine, s.ParentId, s.Source, s.Docstring);
                Execute(db, "INSERT INTO symbol_search VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", s.Id,
                    SourceText.Searchable(s.Name), SourceText.Searchable(s.QualifiedName), s.Kind,
                    SourceText.Searchable(s.Source), SourceText.Searchable(s.Docstring));
            }
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,site_line,site_col,resolution) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                    key, edge.FromId, edge.ToId, edge.Kind, edge.SiteLine, edge.SiteColumn, edge.Resolution);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Execute(db, "COMMIT");
        }
        catch { Execute(db, "ROLLBACK"); throw; }
        return new(summary, warnings);
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
            declarations.Add(new(s, node, ns, type is null ? "" :
                string.Join(".", type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText))));
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
        var types = declarations.Where(d => d.Node is BaseTypeDeclarationSyntax).ToArray();
        var methods = declarations.Where(d => d.Node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax).ToArray();
        foreach (var d in declarations)
        {
            token.ThrowIfCancellationRequested();
            if (d.Node is BaseTypeDeclarationSyntax type && type.BaseList is not null)
                foreach (var baseType in type.BaseList.Types)
                {
                    var fullName = baseType.Type.ToString().Replace("global::", "", StringComparison.Ordinal).Split('<')[0];
                    var possible = types.Where(t => t.Symbol.QualifiedName == fullName ||
                        t.Symbol.QualifiedName == Join(d.Namespace, fullName)).ToArray();
                    if (possible.Length == 1)
                        edges.Add(new(d.Symbol.Id, possible[0].Symbol.Id, possible[0].Symbol.Kind == "Interface" &&
                            d.Symbol.Kind != "Interface" ? "IMPLEMENTS" : "INHERITS", Resolution: "scoped-syntax"));
                }
            if (d.Symbol.Kind != "Member") continue;
            foreach (var call in d.Node.DescendantNodes(n => n == d.Node || n is not LocalFunctionStatementSyntax)
                .OfType<InvocationExpressionSyntax>())
            {
                var simple = call.Expression switch
                {
                    SimpleNameSyntax s => s,
                    MemberAccessExpressionSyntax m => m.Name,
                    MemberBindingExpressionSyntax b => b.Name,
                    _ => null
                };
                if (simple is null) continue;
                var candidates = methods.Where(m => m.Symbol.Name == simple.Identifier.ValueText &&
                    MatchesArity(m.Node, call.ArgumentList.Arguments.Count, simple is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Count : null)).ToArray();
                var receiver = call.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString() : null;
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
                    var site = call.GetLocation().GetLineSpan().StartLinePosition;
                    edges.Add(new(d.Symbol.Id, candidates[0].Symbol.Id, "CALLS", site.Line + 1, site.Character, "scoped-syntax"));
                }
            }
        }
        return edges.Distinct().ToList();
    }
    private static bool MatchesArity(SyntaxNode node, int arguments, int? genericArguments)
    {
        var parameters = node switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            LocalFunctionStatementSyntax l => l.ParameterList.Parameters,
            _ => default
        };
        var genericCount = node switch
        {
            MethodDeclarationSyntax m => m.TypeParameterList?.Parameters.Count ?? 0,
            LocalFunctionStatementSyntax l => l.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0
        };
        return (!genericArguments.HasValue || genericArguments == genericCount) &&
            arguments >= parameters.Count(p => p.Default is null && !p.Modifiers.Any(SyntaxKind.ParamsKeyword)) &&
            (arguments <= parameters.Count || parameters.Any(p => p.Modifiers.Any(SyntaxKind.ParamsKeyword)));
    }
}
