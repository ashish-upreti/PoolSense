using CodeRAG.Core.Configuration;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
using static CodeRAG.Core.Storage.GraphStore;

namespace CodeRAG.Core.Retrieval;

public sealed class Retriever
{
    public const int MaxContextChars = 96_000;
    private static readonly HashSet<string> StopWords = new(
        "a an the in on of to for from with and or is are was be does do how what where when why can could would should please give show explain describe me my this that it its code codebase repository repo through about".Split(' '));
    private static readonly HashSet<string> FlowKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "flow", "workflow", "journey", "architecture", "diagram", "graph", "connection", "connections", "dependencies", "relationships", "integration" };
    private static readonly HashSet<string> IgnoredCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArgumentNullException", "ConfigureAwait", "CreateLinkedTokenSource", "CreateScope", "Deserialize",
        "FirstOrDefault", "FromResult", "GetHashCode", "GetRequiredService", "GetService", "GetValueOrDefault",
        "IsCancellationRequested", "IsNullOrEmpty", "IsNullOrWhiteSpace", "LogCritical", "LogDebug", "LogError",
        "LogInformation", "LogTrace", "LogWarning", "OrderBy", "OrderByDescending", "SelectMany", "Serialize",
        "SingleOrDefault", "ThrowIfCancellationRequested", "ThrowIfNull", "ToArray", "ToDictionary", "ToList",
        "ToLowerInvariant", "ToString", "ToUpperInvariant", "TryGetProperty", "TryGetValue", "WhenAll"
    };
    private static string[] Words(string text) => Regex.Matches(text, @"[A-Za-z_][A-Za-z_0-9]{1,}").Select(m => m.Value).ToArray();
    private static string Fts(string text, IReadOnlyDictionary<string, string[]>? rewrite = null) => string.Join(" OR ", Words(text)
        .Where(w => !StopWords.Contains(w.ToLowerInvariant()))
        .SelectMany(w => rewrite?.GetValueOrDefault(w) ?? [w])
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).Select(w => $"\"{w}\"*"));
    private static readonly string[] Suffixes = ["ations", "ation", "ments", "ment", "ings", "ing", "ies", "als", "al", "ers", "er", "ed", "es", "s"];

    /// <summary>
    /// Prefix form of a plain English word, so "retrieval" also matches Retriever and RetrieveAsync and "indexing" matches indexer.
    /// Identifiers (mixed case, digits, underscores) and short words are returned unchanged.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length < 5 || !Regex.IsMatch(word, "^[A-Za-z][a-z]+$")) return word;
        var lower = word.ToLowerInvariant();
        foreach (var suffix in Suffixes)
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal) || lower.Length - suffix.Length < 4) continue;
            if (suffix == "s" && lower.EndsWith("ss", StringComparison.Ordinal)) break;
            var stem = lower[..^suffix.Length];
            return stem.Length > 4 && stem[^1] == stem[^2] && !"aeioulsz".Contains(stem[^1]) ? stem[..^1] : stem;
        }
        return lower;
    }

    /// <summary>
    /// Search form for each plain word of the question: its stem, or, when nothing indexed starts with the stem, the stem of the
    /// most common indexed word one edit away (two for words of eight or more letters), so a misspelt "retreival" still finds
    /// retrieval code. Stems alone rank better than stem plus exact word, which over-weights namespaces such as "Indexing".
    /// </summary>
    private static Dictionary<string, string[]> SearchForms(SqliteConnection db, IEnumerable<string> words, Dictionary<string, string> corrections)
    {
        var forms = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var plain = words.Where(word => word.Length >= 5 && !StopWords.Contains(word.ToLowerInvariant()) && Regex.IsMatch(word, "^[A-Za-z][a-z]+$"))
            .Distinct(StringComparer.Ordinal).Take(40).ToArray();
        if (plain.Length == 0) return forms;
        var vocabulary = true;
        try { Execute(db, "CREATE VIRTUAL TABLE IF NOT EXISTS temp.symbol_search_vocab USING fts5vocab(main, symbol_search, 'row')"); }
        catch (SqliteException) { vocabulary = false; }
        foreach (var word in plain)
        {
            var stem = Stem(word);
            forms[word] = [stem];
            if (!vocabulary || Indexed(db, stem)) continue;
            var lower = word.ToLowerInvariant();
            var limit = lower.Length >= 8 ? 2 : 1;
            var nearest = Query(db, """
                SELECT term, doc FROM temp.symbol_search_vocab
                WHERE term >= @p0 AND term < @p1 AND length(term) BETWEEN @p2 AND @p3
                """, reader => (Term: reader.GetString(0), Documents: reader.GetInt64(1)),
                lower[..1], lower[..1] + char.MaxValue, lower.Length - limit, lower.Length + limit)
                .Select(item => (item.Term, item.Documents, Distance: Distance(lower, item.Term, limit)))
                .Where(item => item.Distance <= limit).OrderBy(item => item.Distance).ThenByDescending(item => item.Documents)
                .ThenBy(item => item.Term, StringComparer.Ordinal).FirstOrDefault();
            if (nearest.Term is null) continue;
            forms[word] = [Stem(nearest.Term)];
            corrections[word] = nearest.Term;
        }
        return forms;
    }

    private static bool Indexed(SqliteConnection db, string prefix) => Query(db,
        "SELECT 1 FROM temp.symbol_search_vocab WHERE term >= @p0 AND term < @p1 LIMIT 1", _ => true, prefix, prefix + char.MaxValue).Count > 0;

    /// <summary>Edit distance counting a swap of adjacent letters as one edit; stops early once it exceeds <paramref name="limit"/>.</summary>
    private static int Distance(string a, string b, int limit)
    {
        var twoBack = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) value = Math.Min(value, twoBack[j - 2] + 1);
                current[j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }
            if (rowMinimum > limit) return limit + 1;
            (twoBack, previous, current) = (previous, current, twoBack);
        }
        return previous[b.Length];
    }
    private static string Layer(Symbol s) => Path.GetExtension(s.FilePath).ToLowerInvariant() switch
    {
        ".ts" or ".tsx" or ".js" or ".jsx" => "frontend",
        ".html" => "template",
        ".md" => "docs",
        ".css" or ".scss" => "style",
        ".json" or ".yml" or ".yaml" or ".ps1" or ".sh" or ".csproj" or ".sln" => "config",
        _ => "backend"
    };
    private static bool IsTestPath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        return Regex.IsMatch(normalized, @"(?:^|/)(?:tests?|specs?|__tests__)/|(?:Tests?|\.(?:spec|test))\.[A-Za-z0-9]+$", RegexOptions.IgnoreCase);
    }
    private static string[] ExtractCalledMethods(IEnumerable<Symbol> spans, int maxNames) => spans
        .SelectMany(s => Regex.Matches(s.Source, @"\b([A-Za-z_]\w{6,})\s*(?:<[^>(\r\n]{1,80}>)?\s*\(").Select(m => m.Groups[1].Value))
        .Where(n => Regex.IsMatch(n, "[a-z][A-Z]") && !IgnoredCalls.Contains(n))
        .GroupBy(n => n, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .Take(maxNames)
        .Select(g => g.Key)
        .ToArray();
    public static string Render(IEnumerable<Symbol> symbols) => string.Join("\n\n", symbols.Select(s =>
    {
        var relation = s.Relation.Length > 0 ? $" | related by {s.Relation}" : "";
        var language = SourceText.Languages.GetValueOrDefault(Path.GetExtension(s.FilePath), "text");
        var numbered = string.Join("\n", SourceText.Lines(s.Source).Select((line, i) => $"{s.StartLine + i}: {line.TrimEnd('\r', '\n')}"));
        return $"### {s.Kind}: {s.QualifiedName} ({s.FilePath}:{s.StartLine}-{s.EndLine}{relation})\n{s.Docstring}\n```{language}\n{numbered}\n```";
    }));
    public IReadOnlyList<Symbol> ProjectContext(string question, string database, Project project,
        Action<string>? progress = null, CancellationToken token = default)
    {
        using var db = Open(database, readOnly: true);
        var roots = project.Roots.Select(root => Retrieve(question, db, root.Path,
            Math.Max(8, 32 / project.Roots.Count), MaxContextChars, progress, token)
            .Select(s => project.Roots.Count == 1 ? s : s with
            { FilePath = $"{root.Label}/{s.FilePath}", QualifiedName = $"{root.Label}/{s.QualifiedName}" }).ToArray()).ToArray();
        var result = new List<Symbol>();
        for (var i = 0; i < roots.Max(r => r.Length); i++)
            foreach (var spans in roots)
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= 32) return result;
                if (i < spans.Length && SourceText.Length(Render(result.Append(spans[i]))) <= MaxContextChars) result.Add(spans[i]);
            }
        return result;
    }

    /// <summary>Misspelt question words corrected by the last <see cref="Retrieve"/> call, mapped to the indexed word searched instead.</summary>
    public IReadOnlyDictionary<string, string> Corrections { get; private set; } = new Dictionary<string, string>();

    public IReadOnlyList<Symbol> Retrieve(string question, SqliteConnection db, string root,
        int limit = 32, int maxChars = MaxContextChars, Action<string>? progress = null, CancellationToken token = default)
    {
        if (limit < 1 || maxChars < 1) throw new ArgumentOutOfRangeException(nameof(limit), "Retrieval limit and context budget must be positive.");
        var key = RootKey(root);
        var terms = Words(question).Where(w => !w.Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (Fts(string.Join(' ', terms)).Length == 0) return [];
        var corrections = new Dictionary<string, string>(StringComparer.Ordinal);
        var forms = SearchForms(db, terms, corrections);
        Corrections = corrections;
        var flow = terms.Any(FlowKeywords.Contains) ||
            Regex.IsMatch(question, @"\bend[\s-]+to[\s-]+end\b", RegexOptions.IgnoreCase);
        var mentionsTests = Regex.IsMatch(question, @"\b(tests?|testing|specs?|xunit|playwright|unit|e2e)\b", RegexOptions.IgnoreCase);
        var candidates = new Dictionary<string, Symbol>();
        var scores = new Dictionary<string, double>();
        List<Symbol> Search(string text, double weight = 1, bool chunks = false, string actions = "", string[]? extensions = null)
        {
            token.ThrowIfCancellationRequested();
            var match = Fts(text, forms);
            if (match.Length == 0) return [];
            if (actions.Length > 0) match = $"({match}) AND ({Fts(actions)})";
            var args = new List<object?> { match, key, chunks ? 1 : 0 };
            var filter = "";
            if (extensions is { Length: > 0 })
            {
                filter = " AND (" + string.Join(" OR ", extensions.Select((_, i) => $"s.file_path LIKE @p{i + 3}")) + ")";
                args.AddRange(extensions.Select(e => "%" + e));
            }
            var found = Query(db, $"""
                SELECT s.* FROM symbol_search JOIN symbols s ON s.id=symbol_search.symbol_id
                WHERE symbol_search MATCH @p0 AND s.repo_root=@p1 AND s.source<>''
                  AND (@p2=0 OR s.kind='SourceChunk') {filter}
                ORDER BY bm25(symbol_search,0.0,4.0,2.0,0.0,1.0,0.5),s.file_path,s.start_line,s.id LIMIT 100
                """, ReadSymbol, args.ToArray());
            for (var i = 0; i < found.Count; i++)
            {
                candidates.TryAdd(found[i].Id, found[i]);
                scores[found[i].Id] = scores.GetValueOrDefault(found[i].Id) + weight / (20 + i);
            }
            return found;
        }
        progress?.Invoke("Searching indexed source, identifiers and documentation.");
        var seeds = Search(string.Join(' ', terms), 3, chunks: false);
        if (flow)
            seeds = seeds.Concat(Search(string.Join(' ', terms), 1.5, chunks: true)).DistinctBy(s => s.Id).ToList();
        var identifiers = terms.Where(term => !StopWords.Contains(term.ToLowerInvariant()) && !FlowKeywords.Contains(term))
            .Distinct(StringComparer.Ordinal).Take(40).ToArray();
        if (identifiers.Length > 0)
        {
            var placeholders = string.Join(',', identifiers.Select((_, index) => $"@p{index + 1}"));
            var exact = Query(db, $"""
                SELECT * FROM symbols WHERE repo_root=@p0 AND source<>'' AND name IN ({placeholders})
                ORDER BY CASE kind WHEN 'Member' THEN 0 WHEN 'Class' THEN 1 ELSE 2 END,file_path,start_line,id LIMIT 20
                """, ReadSymbol, [key, .. identifiers.Cast<object?>()]);
            foreach (var symbol in exact)
            {
                candidates.TryAdd(symbol.Id, symbol);
                scores[symbol.Id] = scores.GetValueOrDefault(symbol.Id) + .5;
            }
            seeds = exact.Concat(seeds).DistinctBy(symbol => symbol.Id).ToList();
        }
        if (seeds.Count == 0 && !flow) return [];
        List<Symbol> ExpandCalledMembers(IEnumerable<Symbol> fromSpans, double memberWeight, int maxNames)
        {
            var names = ExtractCalledMethods(fromSpans, maxNames);
            if (names.Length == 0) return [];
            Search(string.Join(' ', names), .7);
            if (!flow) return [];
            var placeholders = string.Join(',', names.Select((_, index) => $"@p{index + 1}"));
            var members = Query(db, $"""
                SELECT s.* FROM symbols s LEFT JOIN symbols p ON p.id=s.parent_id
                WHERE s.repo_root=@p0 AND s.kind='Member' AND s.source<>'' AND s.name IN ({placeholders})
                ORDER BY CASE WHEN coalesce(p.kind,'')='Interface' THEN 1 ELSE 0 END,
                         CASE WHEN s.file_path LIKE '%Tests.cs' OR s.file_path LIKE '%.spec.ts' THEN 1 ELSE 0 END,
                         length(s.source) DESC,s.file_path,s.start_line LIMIT 24
                """, r => ReadSymbol(r) with { Relation = "CALLS (member)" }, [key, .. names.Cast<object?>()]);
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (candidates.TryAdd(member.Id, member) || string.IsNullOrEmpty(candidates[member.Id].Relation))
                    candidates[member.Id] = member;
                scores[member.Id] = Math.Max(scores.GetValueOrDefault(member.Id), memberWeight / (1 + .08 * i));
            }
            return members;
        }
        var anchors = new List<Symbol>();
        if (flow)
        {
            progress?.Invoke("Expanding flow evidence across UI, API, orchestration and docs.");
            var facets = new List<(string, string)> { ("progress result response workflow", "process stream render") };
            if (terms.Any(t => new[] { "user", "question", "questions", "ui", "journey", "chat" }.Contains(t.ToLowerInvariant())))
                facets = [("question message input prompt scope", "send submit ask composer"),
                    ("login session authentication", "login session"), .. facets, ("feedback", "submit helpful")];
            var docs = Search(string.Join(' ', terms), chunks: true, extensions: [".md"]);
            if (docs.Count > 0) anchors.Add(docs.FirstOrDefault(s => Path.GetFileName(s.FilePath).Equals("readme.md", StringComparison.OrdinalIgnoreCase)) ?? docs[0]);
            foreach (var (facet, actions) in facets)
                foreach (var extensions in new[] { new[] { ".ts", ".tsx", ".js", ".jsx" }, [".html"], [".cs"] })
                {
                    var found = Search(facet, 1.5, true, actions, extensions);
                    if (found.Count == 0) continue;
                    anchors.Add(found[0]);
                    if (facet.StartsWith("login", StringComparison.Ordinal) && extensions.Contains(".ts"))
                    {
                        var another = found.FirstOrDefault(s => s.FilePath != found[0].FilePath);
                        if (another is not null) anchors.Add(another);
                    }
                }
            var routes = anchors.SelectMany(s => Regex.Matches(s.Source, """['"`](/[\w/-]+)['"`]""").Select(m => m.Groups[1].Value))
                .Where(r => r.Trim('/').Split('/').Length >= 2).Distinct().Take(6).ToArray();
            foreach (var route in routes)
            {
                var words = Words(route).Where(w => !StopWords.Contains(w.ToLowerInvariant())).ToArray();
                if (words.Length < 2) continue;
                var controllers = Query(db, """
                    SELECT * FROM symbols WHERE repo_root=@p0 AND kind IN ('Member','Class','SourceChunk')
                    AND (file_path LIKE @p1 OR file_path LIKE @p2)
                    ORDER BY CASE kind WHEN 'Member' THEN 0 WHEN 'Class' THEN 1 ELSE 2 END,file_path,start_line LIMIT 4
                    """, ReadSymbol, key, $"%/{words[0]}Controller.cs", $"{words[0]}Controller.cs");
                if (controllers.Count > 0)
                {
                    foreach (var ctrl in controllers)
                    {
                        var tagged = ctrl with { Relation = "API route convention" };
                        candidates.TryAdd(tagged.Id, tagged);
                        scores[tagged.Id] = Math.Max(scores.GetValueOrDefault(tagged.Id), 1.25);
                    }
                    anchors.AddRange(controllers.Take(2).Select(s => s with { Relation = "API route convention" }));
                }
                else
                {
                    var found = Search(words[0], 1, true, string.Join(' ', words.Skip(1)), [".cs"]);
                    if (found.Count > 0) anchors.Add(found[0]);
                }
            }
            ExpandCalledMembers(anchors.Concat(seeds.Where(s => !IsTestPath(s.FilePath))).DistinctBy(s => s.Id).Take(12), 1.15, 20);
        }
        else
        {
            ExpandCalledMembers(seeds.Take(5), .7, 16);
        }
        var frontier = (flow
            ? candidates.Values.Where(s => s.Kind != "SourceChunk" && (mentionsTests || !IsTestPath(s.FilePath)))
                .OrderByDescending(s => scores.GetValueOrDefault(s.Id)).Take(14)
            : seeds.Take(8)).Select(s => s.Id).Distinct(StringComparer.Ordinal).ToArray();
        for (var depth = 0; depth < 2 && frontier.Length > 0; depth++)
        {
            token.ThrowIfCancellationRequested();
            var ids = string.Join(',', frontier.Select((_, i) => $"@p{i + 1}"));
            var rows = Query(db, $"""
                SELECT DISTINCT s.*,e.kind AS relation,e.resolution FROM (
                    SELECT to_id AS symbol_id,kind,resolution FROM edges
                    WHERE repo_root=@p0 AND from_id IN ({ids})
                    UNION ALL
                    SELECT from_id AS symbol_id,kind,resolution FROM edges
                    WHERE repo_root=@p0 AND to_id IN ({ids}) AND from_id NOT IN ({ids})
                ) e JOIN symbols s ON s.id=e.symbol_id
                WHERE s.source<>'' AND s.kind<>'SourceChunk'
                ORDER BY CASE e.kind WHEN 'CALLS' THEN 0 WHEN 'IMPLEMENTS' THEN 1 ELSE 2 END,s.file_path,s.start_line LIMIT 32
                """, r => ReadSymbol(r) with
            {
                Relation = r.GetString(r.GetOrdinal("relation")) +
                    " (" + r.GetString(r.GetOrdinal("resolution")) + ")"
            }, [key, .. frontier.Cast<object?>()]);
            var next = new List<string>();
            for (var i = 0; i < rows.Count; i++)
                if (candidates.TryAdd(rows[i].Id, rows[i]))
                { scores[rows[i].Id] = (flow ? 1.1 : .8) / (20 + i); next.Add(rows[i].Id); }
            frontier = next.ToArray();
        }
        if (flow)
        {
            // Large orchestrator/worker methods (> 6,000 chars) are truncated per span; include their continuation chunks
            // and expand downstream service calls invoked later in those methods.
            var largeMembers = candidates.Values
                .Where(s => s.Kind == "Member" && s.Source.Length > 6000 && (mentionsTests || !IsTestPath(s.FilePath)))
                .OrderByDescending(s => scores.GetValueOrDefault(s.Id))
                .Take(3)
                .ToArray();
            var continuations = new List<Symbol>();
            foreach (var member in largeMembers)
            {
                var approxCoveredLines = SourceText.Lines(member.Source[..6000]).Length;
                var resumeFrom = member.StartLine + Math.Max(1, approxCoveredLines - 12);
                var extraChunks = Query(db, """
                    SELECT * FROM symbols WHERE repo_root=@p0 AND file_path=@p1 AND kind='SourceChunk'
                    AND end_line>@p2 AND start_line<=@p3 ORDER BY start_line LIMIT 3
                    """, r => ReadSymbol(r) with { Relation = "Method continuation" },
                    key, member.FilePath, resumeFrom, member.EndLine);
                for (var i = 0; i < extraChunks.Count; i++)
                {
                    var chunk = extraChunks[i];
                    candidates[chunk.Id] = chunk;
                    scores[chunk.Id] = Math.Max(scores.GetValueOrDefault(chunk.Id), Math.Max(.9, scores.GetValueOrDefault(member.Id) - .05 * (i + 1)));
                    continuations.Add(chunk);
                }
            }
            if (continuations.Count > 0)
                ExpandCalledMembers(continuations, .95, 16);
        }
        if (!mentionsTests)
            foreach (var id in candidates.Keys.ToArray())
                if (IsTestPath(candidates[id].FilePath))
                    scores[id] = scores.GetValueOrDefault(id) * .55;
        var ranked = candidates.Values.OrderByDescending(s => scores.GetValueOrDefault(s.Id)).ThenBy(s => s.FilePath, StringComparer.Ordinal)
            .ThenBy(s => s.StartLine).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        if (flow)
        {
            var filtered = ranked.Where(s => Layer(s) != "style").ToList();
            ranked = anchors.Where(s => mentionsTests || !IsTestPath(s.FilePath)).DistinctBy(s => s.Id).ToList();
            var seen = ranked.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var wantsFrontend = terms.Any(t => new[] { "ui", "frontend", "angular", "component", "page", "screen", "journey", "browser", "chat" }.Contains(t.ToLowerInvariant()));
            if (wantsFrontend)
            {
                foreach (var bucket in new[] { "frontend", "template", "docs" })
                    foreach (var item in filtered.Where(s => Layer(s) == bucket).Take(2))
                        if (seen.Add(item.Id)) ranked.Add(item);
            }
            foreach (var item in filtered)
                if (seen.Add(item.Id)) ranked.Add(item);
        }
        var context = new List<Symbol>();
        foreach (var candidate in ranked)
        {
            token.ThrowIfCancellationRequested();
            if (context.Count >= limit) break;
            var sameFile = context.Where(s => s.FilePath == candidate.FilePath).ToArray();
            if (sameFile.Length >= (flow ? 8 : 5) || sameFile.Any(s =>
                (s.Kind == candidate.Kind || candidate.Kind == "SourceChunk") &&
                s.StartLine <= candidate.StartLine && s.EndLine >= candidate.EndLine)) continue;
            var symbol = candidate;
            if (symbol.Kind == "SourceChunk" && Layer(symbol) == "frontend")
            {
                var next = Query(db, """
                    SELECT * FROM symbols WHERE repo_root=@p0 AND file_path=@p1 AND kind='SourceChunk'
                    AND start_line>@p2 AND start_line<=@p3 ORDER BY start_line LIMIT 1
                    """, ReadSymbol, key, symbol.FilePath, symbol.StartLine, symbol.EndLine + 1).FirstOrDefault();
                if (next is not null)
                    symbol = symbol with { Source = symbol.Source + string.Concat(SourceText.Lines(next.Source).Skip(symbol.EndLine - next.StartLine + 1)) };
            }
            var kept = new List<string>();
            var size = 0;
            foreach (var line in SourceText.Lines(SourceText.Redact(symbol.Source)))
            {
                var length = SourceText.Length(line);
                if (kept.Count > 0 && size + length > 6000) break;
                kept.Add(line); size += length;
            }
            symbol = symbol with
            {
                Source = string.Concat(kept),
                EndLine = symbol.StartLine + Math.Max(0, kept.Count - 1),
                Docstring = SourceText.Redact(symbol.Docstring)
            };
            if (SourceText.Length(Render(context.Append(symbol))) <= maxChars) context.Add(symbol);
        }
        progress?.Invoke($"Selected {context.Count} source spans across {context.Select(s => s.FilePath).Distinct().Count()} files; {SourceText.Length(Render(context))} context characters.");
        return context;
    }
}
