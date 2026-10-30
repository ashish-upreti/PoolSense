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
    private static string[] Words(string text) => Regex.Matches(text, @"[A-Za-z_][A-Za-z_0-9]{1,}").Select(m => m.Value).ToArray();
    private static string Fts(string text) => string.Join(" OR ", Words(text).Where(w => !StopWords.Contains(w.ToLowerInvariant()))
        .Distinct(StringComparer.Ordinal).Take(40).Select(w => $"\"{w}\"*"));
    private static string Layer(Symbol s) => Path.GetExtension(s.FilePath).ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx"
        ? "frontend" : Path.GetExtension(s.FilePath).ToLowerInvariant();
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

    public IReadOnlyList<Symbol> Retrieve(string question, SqliteConnection db, string root,
        int limit = 32, int maxChars = MaxContextChars, Action<string>? progress = null, CancellationToken token = default)
    {
        if (limit < 1 || maxChars < 1) throw new ArgumentOutOfRangeException(nameof(limit), "Retrieval limit and context budget must be positive.");
        var key = RootKey(root);
        var terms = Words(question).Where(w => !w.Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (Fts(string.Join(' ', terms)).Length == 0) return [];
        var flow = terms.Any(w => new[] { "flow", "workflow", "journey", "architecture" }.Contains(w.ToLowerInvariant())) ||
            Regex.IsMatch(question, @"\bend[\s-]+to[\s-]+end\b", RegexOptions.IgnoreCase);
        var candidates = new Dictionary<string, Symbol>();
        var scores = new Dictionary<string, double>();
        List<Symbol> Search(string text, double weight = 1, bool chunks = false, string actions = "", string[]? extensions = null)
        {
            token.ThrowIfCancellationRequested();
            var match = Fts(text);
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
        var seeds = Search(string.Join(' ', terms), 3, flow);
        if (seeds.Count == 0 && !flow) return [];
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
                    SELECT * FROM symbols WHERE repo_root=@p0 AND kind='SourceChunk'
                    AND (file_path LIKE @p1 OR file_path LIKE @p2) ORDER BY file_path,start_line LIMIT 2
                    """, ReadSymbol, key, $"%/{words[0]}Controller.cs", $"{words[0]}Controller.cs");
                if (controllers.Count > 0) anchors.AddRange(controllers.Select(s => s with { Relation = "API route convention" }));
                else
                {
                    var found = Search(words[0], 1, true, string.Join(' ', words.Skip(1)), [".cs"]);
                    if (found.Count > 0) anchors.Add(found[0]);
                }
            }
        }
        else
        {
            var names = seeds.Take(5).SelectMany(s => Regex.Matches(s.Source, @"\b([A-Za-z_]\w{7,})\s*\(").Select(m => m.Groups[1].Value))
                .Where(n => Regex.IsMatch(n, "[a-z][A-Z]")).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(16).Select(g => g.Key);
            Search(string.Join(' ', names), .7);
        }
        var frontier = seeds.Take(8).Select(s => s.Id).ToArray();
        for (var depth = 0; depth < 2 && frontier.Length > 0; depth++)
        {
            token.ThrowIfCancellationRequested();
            var ids = string.Join(',', frontier.Select((_, i) => $"@p{i + 1}"));
            var rows = Query(db, $"""
                SELECT DISTINCT s.*,e.kind AS relation,e.resolution FROM edges e JOIN symbols s
                ON s.id=CASE WHEN e.from_id IN ({ids}) THEN e.to_id ELSE e.from_id END
                WHERE e.repo_root=@p0 AND (e.from_id IN ({ids}) OR e.to_id IN ({ids}))
                AND s.source<>'' AND s.kind<>'SourceChunk'
                ORDER BY CASE e.kind WHEN 'CALLS' THEN 0 ELSE 1 END,s.file_path,s.start_line LIMIT 32
                """, r => ReadSymbol(r) with
            {
                Relation = r.GetString(r.GetOrdinal("relation")) +
                    " (" + r.GetString(r.GetOrdinal("resolution")) + ")"
            }, [key, .. frontier.Cast<object?>()]);
            var next = new List<string>();
            for (var i = 0; i < rows.Count; i++)
                if (candidates.TryAdd(rows[i].Id, rows[i]))
                { scores[rows[i].Id] = .8 / (20 + i); next.Add(rows[i].Id); }
            frontier = next.ToArray();
        }
        var ranked = candidates.Values.OrderByDescending(s => scores[s.Id]).ThenBy(s => s.FilePath, StringComparer.Ordinal)
            .ThenBy(s => s.StartLine).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        if (flow)
        {
            var layers = ranked.Where(s => Layer(s) is not ".css" and not ".scss").GroupBy(Layer).Select(g => new Queue<Symbol>(g)).ToArray();
            ranked = anchors.DistinctBy(s => s.Id).ToList();
            var seen = ranked.Select(s => s.Id).ToHashSet();
            while (layers.Any(l => l.Count > 0))
                foreach (var layer in layers)
                    if (layer.TryDequeue(out var item) && seen.Add(item.Id)) ranked.Add(item);
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
