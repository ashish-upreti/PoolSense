using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;

namespace PoolSense.Api.Services.CodeRAG;

/// <summary>Bounded, syntax-only knowledge extraction. It never resolves dynamic calls or external resources.</summary>
internal static class OkfConceptProducer
{
    internal sealed record Fact(string Text, IReadOnlyList<Symbol> Sources);
    internal sealed record Concept(string Id, string Title, string Type, string Description,
        IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, IReadOnlyList<Fact> Facts);
    internal sealed record Selection(List<Symbol> Symbols, List<Edge> Edges, List<string> Limitations)
    {
        internal IReadOnlyList<string> Errors { get; init; } = [];
    }
    internal sealed record FittedConcept(Concept Concept, string Markdown, bool Compacted, IReadOnlyList<string> Limitations);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static string Clean(string text) => SourceText.Redact(text).Replace("`", "'")
        .Replace("[", "").Replace("]", "").Replace("<", "").Replace(">", "");
    private static string Label(Symbol s)
    {
        var label = Clean(s.QualifiedName).Replace("\r", " ").Replace("\n", " ");
        return label.Length <= 240 ? label : label[..240] + "… (display shortened)";
    }

    internal static FittedConcept? Fit(Concept concept, OkfOptions options,
        Func<Concept, bool, string> render, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var markdown = render(concept, false);
        if (Fits(concept.Id + ".md", markdown, options))
            return new(concept, markdown, false, []);
        var remaining = concept.Facts.ToList();
        while (remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var supportedAliases = remaining.SelectMany(f => f.Sources).SelectMany(Aliases)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = concept with
            {
                Facts = remaining.ToArray(),
                Aliases = concept.Aliases.Where(supportedAliases.Contains).ToArray()
            };
            markdown = render(candidate, true);
            if (Fits(concept.Id + ".md", markdown, options))
                return new(candidate, markdown, true,
                    [$"Budget-limited concept {concept.Id}: retained {remaining.Count} of {concept.Facts.Count} source-backed entries; optional presentation metadata compacted."]);
            // Drop entire grounded entries, never shorten authoritative symbol names, hashes or URLs.
            if (remaining.Count == 1) break;
            var removable = concept.Type == "Workflow" ? remaining.Skip(1) : remaining;
            var largest = removable.OrderByDescending(f => f.Text.Length +
                f.Sources.Sum(s => (long)s.QualifiedName.Length + s.FilePath.Length)).First();
            remaining.Remove(largest);
        }
        return null;
    }

    internal static bool Fits(string path, string markdown, OkfOptions options)
    {
        if (Encoding.UTF8.GetByteCount(markdown) > Math.Clamp(options.MaxDocumentBytes, 1, 1024 * 1024))
            return false;
        try { OkfParser.Parse(path, markdown, options); return true; }
        catch (InvalidDataException ex) when (ex.Message is "OKF document byte limit exceeded." or
                                                 "OKF YAML node limit exceeded." or "OKF YAML depth limit exceeded.")
        {
            return false;
        }
    }

    internal static Selection Select(SqliteConnection db, string root, OkfOptions options,
        Func<Symbol, bool> valid, CancellationToken token)
    {
        var maximum = Math.Clamp(options.MaxSymbols, 1, 256);
        var candidateLimit = Math.Min(1024, maximum * 4);
        var sourceLimit = Math.Clamp(options.MaxDocumentBytes / 2, 1, 64 * 1024);
        var rootKey = GraphStore.RootKey(root);
        var limitations = new List<string>();
        var sourceErrors = new List<string>();
        var selected = new List<Symbol>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var pool = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        var validation = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsValid(Symbol symbol)
        {
            token.ThrowIfCancellationRequested();
            if (validation.TryGetValue(symbol.Id, out var cached)) return cached;
            var matches = valid(symbol);
            validation[symbol.Id] = matches;
            if (!matches) sourceErrors.Add("An indexed span no longer matched its local source and was omitted.");
            return matches;
        }
        void FetchEndpoints(IEnumerable<Edge> edges)
        {
            var ids = edges.SelectMany(e => new[] { e.FromId, e.ToId }).Distinct()
                .Where(id => !pool.ContainsKey(id)).Take(Math.Max(0, candidateLimit / 2 - pool.Count)).ToArray();
            if (ids.Length == 0) return;
            foreach (var symbol in GraphStore.Query(db, """
                SELECT * FROM symbols WHERE repo_root=@p0 AND id IN (SELECT value FROM json_each(@p1))
                  AND length(source) BETWEEN 1 AND @p2 LIMIT @p3
                """, GraphStore.ReadSymbol, rootKey, JsonSerializer.Serialize(ids), sourceLimit, ids.Length))
                pool.TryAdd(symbol.Id, symbol);
        }
        bool AddPair(Edge edge, int ceiling)
        {
            if (!pool.TryGetValue(edge.FromId, out var from) || !pool.TryGetValue(edge.ToId, out var to)) return false;
            var missing = new[] { from, to }.DistinctBy(s => s.Id).Where(s => !selectedIds.Contains(s.Id)).ToList();
            if (selected.Count + missing.Count > ceiling || !IsValid(from) || !IsValid(to)) return false;
            foreach (var symbol in missing)
                if (selectedIds.Add(symbol.Id)) selected.Add(symbol);
            return true;
        }
        static Edge ReadEdge(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5));

        // Interleave kinds and files before taking the bounded candidate pool, rather than types in the first files.
        db.CreateFunction<string, string>("okf_source_rank", SourceText.Hash, isDeterministic: true);
        var seedEdges = maximum < 2 ? [] : GraphStore.Query(db, """
            SELECT from_id,to_id,kind,site_line,site_col,resolution FROM (
              SELECT *,ROW_NUMBER() OVER(PARTITION BY kind ORDER BY entry_rank,file_rank,
                okf_source_rank(from_file || char(0) || from_name),from_line,to_id) AS kind_rank
              FROM (
                SELECT e.from_id,e.to_id,e.kind,e.site_line,e.site_col,e.resolution,
                  f.file_path AS from_file,f.qualified_name AS from_name,f.start_line AS from_line,
                  CASE WHEN lower(f.name) LIKE 'handle%' OR lower(f.name) LIKE 'execute%'
                    OR lower(f.name) LIKE 'process%' OR lower(f.name) LIKE 'run%' THEN 0 ELSE 1 END AS entry_rank,
                  ROW_NUMBER() OVER(PARTITION BY e.kind,f.file_path ORDER BY
                    CASE WHEN lower(f.name) LIKE 'handle%' OR lower(f.name) LIKE 'execute%'
                      OR lower(f.name) LIKE 'process%' OR lower(f.name) LIKE 'run%' THEN 0 ELSE 1 END,
                    f.start_line,t.file_path,t.start_line) AS file_rank
                FROM edges e JOIN symbols f ON f.id=e.from_id AND f.repo_root=e.repo_root
                  JOIN symbols t ON t.id=e.to_id AND t.repo_root=e.repo_root
                WHERE e.repo_root=@p0 AND upper(e.kind) IN ('CALLS','IMPLEMENTS','INHERITS')
                  AND length(f.source) BETWEEN 1 AND @p1 AND length(t.source) BETWEEN 1 AND @p1
                  AND (e.site_line IS NULL OR e.site_line BETWEEN f.start_line AND f.end_line)
              )
            ) ORDER BY kind_rank,kind LIMIT @p2
            """, ReadEdge, rootKey, sourceLimit, Math.Min(32, Math.Max(1, candidateLimit / 8)));
        FetchEndpoints(seedEdges);
        var graphCeiling = Math.Min(24, Math.Min(maximum, Math.Max(2, maximum / 2)));
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var rootFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in seedEdges.Where(e => e.Kind.Equals("CALLS", StringComparison.OrdinalIgnoreCase)))
        {
            if (roots.Count >= 3) break;
            if (!pool.TryGetValue(edge.FromId, out var from) || rootFiles.Contains(from.FilePath)) continue;
            if (AddPair(edge, graphCeiling))
            {
                roots.Add(edge.FromId);
                rootFiles.Add(from.FilePath);
            }
        }
        var contractPairs = 0;
        foreach (var edge in seedEdges.Where(e => e.Kind.ToUpperInvariant() is "IMPLEMENTS" or "INHERITS"))
        {
            if (contractPairs >= 2) break;
            if (AddPair(edge, graphCeiling)) contractPairs++;
        }
        var neighborEdges = roots.Count == 0 ? [] : GraphStore.Query(db, """
            SELECT e.from_id,e.to_id,e.kind,e.site_line,e.site_col,e.resolution
            FROM edges e JOIN symbols f ON f.id=e.from_id AND f.repo_root=e.repo_root
              JOIN symbols t ON t.id=e.to_id AND t.repo_root=e.repo_root
            WHERE e.repo_root=@p0 AND upper(e.kind)='CALLS'
              AND e.from_id IN (SELECT value FROM json_each(@p1))
              AND length(f.source) BETWEEN 1 AND @p2 AND length(t.source) BETWEEN 1 AND @p2
              AND (e.site_line IS NULL OR e.site_line BETWEEN f.start_line AND f.end_line)
            ORDER BY f.file_path,f.start_line,t.file_path,t.start_line LIMIT @p3
            """, ReadEdge, rootKey, JsonSerializer.Serialize(selected.Select(s => s.Id)),
            sourceLimit, Math.Min(64, candidateLimit / 4));
        FetchEndpoints(neighborEdges);
        foreach (var edge in neighborEdges) AddPair(edge, graphCeiling);

        var configLimit = Math.Min(Math.Min(16, maximum), Math.Max(0, candidateLimit - pool.Count));
        var configCandidates = GraphStore.Query(db, """
            SELECT * FROM symbols WHERE repo_root=@p0 AND lower(kind)='member'
              AND lower(file_path) LIKE '%.cs' AND length(source) BETWEEN 1 AND @p1
              AND (instr(source,'GetSection')>0 OR instr(source,'GetConnectionString')>0
                OR instr(source,'GetEnvironmentVariable')>0)
            ORDER BY okf_source_rank(file_path || char(0) || qualified_name),start_line LIMIT @p2
            """, GraphStore.ReadSymbol, rootKey, sourceLimit, configLimit);
        foreach (var symbol in configCandidates) pool.TryAdd(symbol.Id, symbol);
        var configCount = 0;
        foreach (var symbol in configCandidates)
        {
            if (selected.Count >= maximum || configCount >= Math.Min(4, Math.Max(1, maximum / 10))) break;
            if (!selectedIds.Contains(symbol.Id) && IsValid(symbol))
            {
                selectedIds.Add(symbol.Id);
                selected.Add(symbol);
                configCount++;
            }
        }
        var candidates = GraphStore.Query(db, """
            SELECT * FROM (
              SELECT *, ROW_NUMBER() OVER(PARTITION BY category ORDER BY file_rank,
                okf_source_rank(file_path || char(0) || qualified_name), start_line) AS category_rank
              FROM (
                SELECT *, ROW_NUMBER() OVER(PARTITION BY lower(kind), file_path
                  ORDER BY CASE WHEN lower(kind)='member' AND
                    (lower(name) LIKE 'handle%' OR lower(name) LIKE 'execute%' OR lower(name) LIKE 'process%'
                     OR lower(name) LIKE 'calculate%' OR lower(name) LIKE 'compute%' OR lower(name) LIKE 'validate%')
                    THEN 0 ELSE 1 END, start_line,qualified_name) AS file_rank,
                  CASE WHEN lower(file_path) LIKE '%.sql' THEN 0
                       WHEN lower(kind)='member' THEN 1 WHEN lower(kind)='interface' THEN 2
                       WHEN lower(kind) IN ('class','record','struct','enum') THEN 3 ELSE 4 END AS category
                FROM symbols WHERE repo_root=@p0 AND length(source)>0 AND length(source)<=@p1
              )
            ) ORDER BY category_rank,category LIMIT @p2
            """, GraphStore.ReadSymbol, rootKey, sourceLimit, Math.Max(0, candidateLimit - pool.Count));
        // Stable hash ordering within a round removes alphabetical first-file bias across rebuilds.
        foreach (var round in candidates.GroupBy(s => (s.Kind.ToLowerInvariant(), s.FilePath))
                     .SelectMany(g => g.Select((s, i) => (Symbol: s, Round: i)))
                     .GroupBy(s => s.Round).OrderBy(g => g.Key))
        {
            foreach (var item in round.GroupBy(s => Category(s.Symbol)).SelectMany(g => g
                         .OrderBy(s => SourceText.Hash(s.Symbol.FilePath + "\0" + s.Symbol.QualifiedName), StringComparer.Ordinal)
                         .Select((s, i) => (s.Symbol, Category: g.Key, Rank: i)))
                         .OrderBy(s => s.Rank).ThenBy(s => s.Category))
            {
                token.ThrowIfCancellationRequested();
                if (selected.Count >= maximum) break;
                if (selectedIds.Contains(item.Symbol.Id) || !IsValid(item.Symbol)) continue;
                selectedIds.Add(item.Symbol.Id);
                selected.Add(item.Symbol);
                if (selected.Count == maximum) break;
            }
            if (selected.Count == maximum) break;
        }
        var total = Convert.ToInt64(GraphStore.Scalar(db,
            "SELECT count(*) FROM symbols WHERE repo_root=@p0 AND length(source)>0",
            GraphStore.RootKey(root)));
        if (total > selected.Count)
            limitations.Add($"Partial source coverage: selected {selected.Count} of {total} indexed nonempty spans; symbol, candidate and source-byte bounds apply.");
        var ids = JsonSerializer.Serialize(selected.Select(s => s.Id));
        var edgeRoom = 512 - seedEdges.Count - neighborEdges.Count;
        var edges = GraphStore.Query(db, """
            SELECT e.from_id,e.to_id,e.kind,e.site_line,e.site_col,e.resolution FROM edges e
            JOIN symbols f ON f.id=e.from_id AND f.repo_root=e.repo_root
            JOIN symbols t ON t.id=e.to_id AND t.repo_root=e.repo_root
            WHERE e.repo_root=@p0 AND e.from_id IN (SELECT value FROM json_each(@p1))
              AND e.to_id IN (SELECT value FROM json_each(@p1))
              AND upper(e.kind) IN ('CALLS','WRITES','READS','NOTIFIES','IMPLEMENTS','INHERITS')
            ORDER BY f.file_path,f.start_line,t.file_path,t.start_line,e.kind LIMIT @p2
            """, ReadEdge, rootKey, ids, edgeRoom);
        if (edges.Count == edgeRoom) limitations.Add("Partial graph coverage: the 512-edge producer query bound was reached.");
        var selectedEdges = seedEdges.Concat(neighborEdges).Concat(edges)
            .Where(e => selectedIds.Contains(e.FromId) && selectedIds.Contains(e.ToId)).Distinct().ToList();
        return new(selected, selectedEdges, limitations.Distinct().ToList())
        {
            Errors = sourceErrors.Distinct().ToArray()
        };
    }

    internal static List<Concept> Generate(Selection selection, CancellationToken token)
    {
        var symbols = selection.Symbols;
        var concepts = new List<Concept>();
        var declarations = symbols.Where(s => s.Kind is not ("SourceChunk" or "File"))
            .OrderBy(s => SourceText.Hash(s.FilePath + s.QualifiedName), StringComparer.Ordinal).Take(8).ToList();
        if (declarations.Count == 0) declarations = symbols.Take(4).ToList();
        concepts.Add(new("overview", "Source-backed knowledge map", "Reference",
            "Map of selected indexed source declarations with exact source locations.",
            [], ["source-map"], declarations.Select(s => new Fact(
                $"`{Label(s)}` is an indexed `{Clean(s.Kind)}` declaration in `{Clean(s.FilePath)}` (lines {s.StartLine}–{s.EndLine}).",
                [s])).ToList()));

        var terms = symbols.Where(s => s.Kind.ToLowerInvariant() is "class" or "record" or "enum" or "struct" or "interface")
            .OrderByDescending(s => !string.IsNullOrWhiteSpace(s.Docstring)).ThenBy(s => Label(s), StringComparer.Ordinal).Take(8).ToList();
        if (terms.Count > 0)
        {
            var domain = terms.Any(s => Matches(s.FilePath.Replace('\\', '/'), @"(?:^|/)(?:Domain|Models)/").Count > 0);
            concepts.Add(new(domain ? "domain" : "glossary", "Source terminology and domain declarations", "Glossary",
                "Identifier-derived terms and explicit declaration documentation; acronyms are not expanded.",
                terms.SelectMany(Aliases).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray(),
                ["glossary", "domain"], terms.Select(s =>
                {
                    var aliases = Aliases(s);
                    var docs = s.Source.Contains(s.Docstring.Trim(), StringComparison.Ordinal) ? Clean(s.Docstring) : "";
                    if (docs.Length > 400) docs = docs[..400] + " … (excerpt)";
                    return new Fact($"### {Label(s)}\n\nDeclared `{Clean(s.Kind)}`; identifier words: " +
                                    $"`{string.Join("`, `", aliases)}`." +
                                    (docs.Length == 0 ? "" : $"\n\nExplicit documentation excerpt: {docs}"), [s]);
                }).ToList()));
        }

        var byId = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var flowEdges = selection.Edges.Where(e => e.Kind.ToUpperInvariant() is "CALLS" or "WRITES" or "READS" or "NOTIFIES").ToList();
        if (flowEdges.Select(e => e.FromId).Distinct().Count() > 3)
            selection.Limitations.Add("Partial workflow coverage: at most three entry roots are documented, with two hops, twelve relations and eight sources per workflow.");
        var roots = flowEdges.Select(e => e.FromId).Distinct().OrderByDescending(id =>
            Matches(byId[id].Name, @"^(?:Handle|Execute|Process|Run|Create|Update|Delete|Get|Post)").Count > 0)
            .ThenBy(id => Label(byId[id]), StringComparer.Ordinal).Take(3);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var facts = new List<Fact>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var frontier = new List<string> { root };
            var used = new HashSet<string>(StringComparer.Ordinal) { root };
            for (var depth = 0; depth < 2 && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var edge in flowEdges.Where(e => frontier.Contains(e.FromId)).Take(16))
                {
                    if (facts.Count >= 12 || (used.Count >= 8 && !used.Contains(edge.ToId))) break;
                    var from = byId[edge.FromId];
                    var to = byId[edge.ToId];
                    if (edge.SiteLine is { } line && (line < from.StartLine || line > from.EndLine)) continue;
                    facts.Add(new($"`{Label(from)}` → **{Clean(edge.Kind)}** → `{Label(to)}`" +
                        (edge.SiteLine is { } site ? $" at source line {site}" : "") +
                        $". Indexed resolution: `{Clean(edge.Resolution)}`; syntax evidence, not proof of runtime dispatch.", [from, to]));
                    used.Add(to.Id);
                    if (visited.Add(to.Id)) next.Add(to.Id);
                }
                frontier = next;
            }
            if (facts.Count == 0) continue;
            concepts.Add(new("workflow-" + StableKey(byId[root]), $"{Label(byId[root])} workflow", "Workflow",
                "Bounded indexed calls and effects, preserving graph resolution strength and partial coverage.",
                Aliases(byId[root]), ["workflow", "partial", "syntax-resolution"], facts));
        }

        foreach (var symbol in symbols.Where(s => s.FilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            token.ThrowIfCancellationRequested();
            // Strip comments and string literals before naming syntactic inputs/outputs. Never join separate statements by name.
            var syntax = Regex.Replace(symbol.Source, @"--[^\r\n]*|/\*[\s\S]*?\*/|'(?:''|[^'])*'",
                m => string.Concat(m.Value.Select(c => c is '\r' or '\n' ? c : ' ')),
                RegexOptions.CultureInvariant, RegexTimeout);
            var reads = Matches(syntax, @"\b(?:FROM|JOIN)\s+([\[\]\w.]+)").Select(m => m.Groups[1].Value).Distinct().Take(8).ToList();
            var writes = Matches(syntax, @"\b(?:INSERT\s+INTO|MERGE\s+INTO|UPDATE)\s+([\[\]\w.]+)")
                .Select(m => m.Groups[1].Value).Distinct().Take(8).ToList();
            var hasCalculation = Matches(syntax, @"\b(?:SELECT|SUM|AVG|COUNT|CASE|COALESCE)\b").Count > 0;
            if (reads.Count + writes.Count == 0 && !hasCalculation) continue;
            var facts = new List<Fact>
            {
                new($"SQL syntax in `{Label(symbol)}` names read inputs: {Names(reads)}; write targets: {Names(writes)}. " +
                    "Names are local syntax tokens, not resolved database objects; CTEs/aliases may occur. No cross-file or same-name lineage is inferred.", [symbol])
            };
            foreach (var projection in Matches(syntax, @"\bSELECT\s+([\s\S]{1,600}?)\s+FROM\b").Take(2))
                facts.Add(new("Explicit SQL projection/output expression (bounded literal source fragment):\n\n" +
                    Fence(SourceText.Redact(symbol.Source.Substring(projection.Groups[1].Index, projection.Groups[1].Length).Trim()), 600), [symbol]));
            foreach (var output in Matches(syntax, @"\bINSERT\s+INTO\s+([\[\]\w.]+)\s*\(([^)]{1,300})\)").Take(2))
                facts.Add(new($"Explicit insert output columns for `{Clean(output.Groups[1].Value)}`: " +
                    $"`{Clean(output.Groups[2].Value)}`. Projection-to-column mapping is not inferred.", [symbol]));
            var joins = Matches(syntax, @"\bJOIN\s+[\[\]\w.]+(?:\s+(?:AS\s+)?\w+)?\s+ON\s+[^\r\n;]{1,240}").Take(4);
            foreach (var join in joins)
                facts.Add(new("Explicit JOIN clause excerpt:\n\n" +
                    Fence(SourceText.Redact(symbol.Source.Substring(join.Index, join.Length)), 240), [symbol]));
            foreach (var predicate in Matches(syntax, @"\b(?:WHERE|HAVING|WHEN)\s+[^\r\n;]{1,400}").Take(4))
                facts.Add(new("SQL guard/branch predicate (bounded literal source fragment; may be partial):\n\n" +
                    Fence(SourceText.Redact(symbol.Source.Substring(predicate.Index, predicate.Length)), 400), [symbol]));
            facts.Add(new("SQL projection/calculation and predicate evidence (bounded source excerpt):\n\n" + Snippet(symbol, 1400), [symbol]));
            concepts.Add(new("calculation-" + StableKey(symbol), $"{Clean(Path.GetFileName(symbol.FilePath))} SQL calculation and lineage", "Calculation",
                "SQL read/write tokens and explicit joins with source-bound calculation evidence; syntax only.",
                Aliases(symbol), ["sql", "calculation", "data-lineage", "partial"], facts));
        }
        if (symbols.Count(s => s.FilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) > 3)
            selection.Limitations.Add("Partial SQL coverage: at most three indexed SQL spans are documented; snippets are bounded and statements split across chunks are not stitched.");

        var calculations = new List<Fact>();
        var contracts = new List<Fact>();
        var configuration = new List<Fact>();
        foreach (var symbol in symbols.Where(s => s.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                     s.Kind.ToLowerInvariant() is "member" or "interface").Take(128))
        {
            token.ThrowIfCancellationRequested();
            var tree = CSharpSyntaxTree.ParseText(symbol.Source, cancellationToken: token);
            var node = (SyntaxNode?)SyntaxFactory.ParseMemberDeclaration(symbol.Source) ?? tree.GetRoot(token);
            var ownedNodes = node.DescendantNodes(n => n is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax).ToList();
            var branches = ownedNodes.Where(n => n is IfStatementSyntax or ElseClauseSyntax or
                    ConditionalExpressionSyntax or SwitchSectionSyntax or SwitchExpressionArmSyntax)
                .Take(4).ToList();
            var expressions = ownedNodes.Where(n => n is ReturnStatementSyntax or ArrowExpressionClauseSyntax)
                .Where(n => branches.Count > 0 || n.DescendantNodes().Any(e => e is BinaryExpressionSyntax or ConditionalExpressionSyntax))
                .Take(3).ToList();
            if ((expressions.Count > 0 || branches.Count > 0) && calculations.Count < 6)
            {
                var method = node.DescendantNodesAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var inputs = method?.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [];
                calculations.Add(new($"`{Label(symbol)}` declares inputs {Names(inputs)} and return expression evidence " +
                    "(syntax only; no inferred business rule):\n\n" +
                    string.Join("\n\n", expressions.Select(e => Fence(SourceText.Redact(e.ToString()), 700))) +
                    (branches.Count == 0 ? "" : "\n\nLiteral guard/branch fragments (bounded, potentially partial; no inferred execution dependency):\n\n" +
                        string.Join("\n\n", branches.Select(b => Fence(SourceText.Redact(b.ToString()), 500)))), [symbol]));
            }
            if (symbol.Kind.Equals("Interface", StringComparison.OrdinalIgnoreCase) && contracts.Count < 6)
            {
                var iface = node.DescendantNodesAndSelf().OfType<InterfaceDeclarationSyntax>().FirstOrDefault();
                if (iface != null)
                    contracts.Add(new($"`{Label(symbol)}` declares interface members: " +
                        Names(iface.Members.Select(m => Clean(m.ToString().Split('{')[0].Trim())).Take(10).ToList()) +
                        ". This declares a contract, not an external integration or implementation guarantee.", [symbol]));
            }
            var keys = node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax a && a.Name.Identifier.ValueText is
                    "GetSection" or "GetConnectionString" or "GetEnvironmentVariable")
                .Select(i => (Call: i.Expression.ToString(), Literal: i.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax))
                .Where(i => i.Literal?.IsKind(SyntaxKind.StringLiteralExpression) == true).Take(6).ToList();
            if (keys.Count > 0 && configuration.Count < 6)
                configuration.Add(new($"`{Label(symbol)}` contains explicit configuration/environment accessor syntax: " +
                    string.Join(", ", keys.Select(k => $"`{Clean(k.Call)}(\"{Clean(k.Literal!.Token.ValueText)}\")`")) +
                    ". Presence does not establish a required prerequisite or successful runtime configuration.", [symbol]));
        }
        if (calculations.Count > 0)
            concepts.Add(new("calculations", "Declared calculations and return rules", "Calculation",
                "Explicit parameter names, return expressions and literal guard/branch fragments, without inferred business semantics.",
                [], ["calculation", "rule", "syntax-resolution"], calculations));
        foreach (var edge in selection.Edges.Where(e => e.Kind.ToUpperInvariant() is "IMPLEMENTS" or "INHERITS").Take(4))
            contracts.Add(new($"`{Label(byId[edge.FromId])}` → `{Clean(edge.Kind)}` → `{Label(byId[edge.ToId])}`; " +
                $"indexed resolution `{Clean(edge.Resolution)}`.", [byId[edge.FromId], byId[edge.ToId]]));
        if (contracts.Count > 0)
            concepts.Add(new("contracts", "Declared interfaces and implementation relations",
                symbols.Any(s => s.Kind.Equals("Interface", StringComparison.OrdinalIgnoreCase)) ? "Interface" : "Reference",
                "Explicit interface member declarations and indexed implementation relations.",
                [], ["interface", "contract"], contracts.Take(8).ToList()));
        if (configuration.Count > 0)
            concepts.Add(new("configuration", "Source-declared configuration access", "Configuration",
                "Literal configuration and environment keys referenced by source accessor syntax; no secret values.",
                [], ["configuration"], configuration));
        var firstWorkflow = concepts.FirstOrDefault(c => c.Type == "Workflow")?.Id;
        return concepts.OrderBy(c => c.Id == "overview" ? 0 : c.Type == "Glossary" ? 1 :
            c.Id == firstWorkflow ? 2 : c.Id == "contracts" ? 3 : c.Id == "configuration" ? 4 :
            c.Id == "calculations" ? 5 : c.Type == "Calculation" ? 6 : 7).ToList();
    }

    private static string Names(IReadOnlyCollection<string> names) => names.Count == 0 ? "not established in this span" :
        string.Join(", ", names.Select(n => "`" + Clean(n) + "`"));
    private static int Category(Symbol s) => s.FilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? 0 :
        s.Kind.ToLowerInvariant() switch { "member" => 1, "interface" => 2, "class" or "record" or "enum" or "struct" => 3, _ => 4 };
    private static string StableKey(Symbol s) => SourceText.Hash($"{s.FilePath}\0{s.QualifiedName}\0{s.StartLine}")[..12];
    private static IReadOnlyList<string> Aliases(Symbol symbol)
    {
        var name = Path.GetFileNameWithoutExtension(symbol.Name);
        var words = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2", RegexOptions.CultureInvariant, RegexTimeout);
        words = Regex.Replace(words, "([A-Z])([A-Z][a-z])", "$1 $2", RegexOptions.CultureInvariant, RegexTimeout);
        words = Regex.Replace(words, @"[_\-.]+", " ", RegexOptions.CultureInvariant, RegexTimeout).Trim();
        return new[] { Clean(name), Clean(words) }.Where(s => s.Length is > 1 and <= 120)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Snippet(Symbol symbol, int maximum) => Fence(SourceText.Redact(symbol.Source), maximum);
    private static string Fence(string text, int maximum)
    {
        var excerpt = text.Length > maximum ? text[..maximum] + "\n… (excerpt truncated)" : text;
        var delimiter = new string('~', Math.Max(4, Matches(excerpt, "~+").Select(m => m.Length).DefaultIfEmpty(0).Max() + 1));
        return delimiter + "text\n" + excerpt + "\n" + delimiter;
    }
}
