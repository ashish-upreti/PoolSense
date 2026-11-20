using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record CodeSearchCandidate(CodeRepository Repository, Symbol Symbol, int LexicalRank = 0, int SemanticRank = 0,
    double SemanticSimilarity = 0, bool KnowledgeReference = false)
{
    public double KnowledgeRelevance { get; init; }
    public IReadOnlyList<CodeRelationship> Relationships { get; init; } = [];
}

public sealed class CodeCandidateBuffer(string query, int capacity) : IEnumerable<CodeSearchCandidate>
{
    private readonly object gate = new();
    private readonly List<CodeSearchCandidate> items = [];
    private readonly Dictionary<string, double> scores = new();
    private readonly string[] terms = CodeCandidateSelection.Terms(query);
    private readonly int limit = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private static string Key(CodeSearchCandidate item) => $"{item.Repository.Id}\0{item.Repository.Generation}\0{item.Symbol.Id}";
    public bool Limited { get; private set; }
    public int Count { get { lock (gate) return items.Count; } }
    public void Add(CodeSearchCandidate candidate)
    {
        var source = SourceText.Redact(candidate.Symbol.Source);
        source = source[..Math.Min(source.Length, 6000)];
        candidate = candidate with
        {
            Symbol = candidate.Symbol with
            {
                Source = source,
                Docstring = "",
                EndLine = Math.Min(candidate.Symbol.EndLine, candidate.Symbol.StartLine + Math.Max(0, SourceText.Lines(source).Count() - 1))
            }
        };
        var score = CodeCandidateSelection.Relevance(query, candidate, terms);
        lock (gate)
        {
            var existing = items.FindIndex(item => item.Repository.Id == candidate.Repository.Id
                && item.Repository.Generation == candidate.Repository.Generation && item.Symbol.Id == candidate.Symbol.Id);
            if (existing >= 0)
            {
                var previous = items[existing];
                var symbol = string.IsNullOrEmpty(previous.Symbol.Relation) && !string.IsNullOrEmpty(candidate.Symbol.Relation)
                    ? previous.Symbol with { Relation = candidate.Symbol.Relation }
                    : previous.Symbol;
                items[existing] = previous with
                {
                    Symbol = symbol,
                    LexicalRank = PositiveMin(previous.LexicalRank, candidate.LexicalRank),
                    SemanticRank = PositiveMin(previous.SemanticRank, candidate.SemanticRank),
                    SemanticSimilarity = Math.Max(previous.SemanticSimilarity, candidate.SemanticSimilarity),
                    KnowledgeReference = previous.KnowledgeReference || candidate.KnowledgeReference,
                    KnowledgeRelevance = Math.Max(previous.KnowledgeRelevance, candidate.KnowledgeRelevance),
                    Relationships = previous.Relationships.Concat(candidate.Relationships).Distinct().Take(8).ToArray()
                };
                scores[Key(items[existing])] = CodeCandidateSelection.Relevance(query, items[existing], terms);
                return;
            }
            if (items.Count < limit) { items.Add(candidate); scores[Key(candidate)] = score; return; }
            Limited = true;
            var counts = items.GroupBy(item => item.Repository.Id).ToDictionary(group => group.Key, group => group.Count());
            var weakest = items.OrderBy(item => scores[Key(item)])
                .ThenByDescending(item => counts[item.Repository.Id]).First();
            var comparison = score.CompareTo(scores[Key(weakest)]);
            if (comparison > 0 || comparison == 0 && !counts.ContainsKey(candidate.Repository.Id))
            {
                items.Remove(weakest);
                scores.Remove(Key(weakest));
                items.Add(candidate);
                scores[Key(candidate)] = score;
            }
        }
    }
    private static int PositiveMin(int first, int second) => first == 0 ? second : second == 0 ? first : Math.Min(first, second);
    public IEnumerator<CodeSearchCandidate> GetEnumerator() { lock (gate) return items.ToArray().AsEnumerable().GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class CodeCandidateSelection
{
    private static readonly HashSet<string> StopWords = new(
        "the and for from with what which how does this that please explain describe show code repository repo workflow title description".Split(' '));
    public static string[] Terms(string query) => Regex.Matches(query, @"[A-Za-z_][A-Za-z_0-9]{2,}")
        .Select(match => match.Value).Where(word => !StopWords.Contains(word.ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();

    public static double Relevance(string query, CodeSearchCandidate candidate, string[]? terms = null)
    {
        terms ??= Terms(query);
        var symbol = candidate.Symbol;
        var matched = terms.Count(term => symbol.QualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            symbol.FilePath.Contains(term, StringComparison.OrdinalIgnoreCase) || symbol.Source.Contains(term, StringComparison.OrdinalIgnoreCase));
        var parts = symbol.QualifiedName.Split('.');
        var exact = terms.Any(term => symbol.Name.Equals(term, StringComparison.OrdinalIgnoreCase)
            || parts.Contains(term, StringComparer.OrdinalIgnoreCase))
            || query.Contains(symbol.FilePath, StringComparison.OrdinalIgnoreCase);
        // Raw BM25 scores from independent databases are not compared. Fuse within-list ranks,
        // globally comparable query-term coverage and exact identifier/path matches.
        var relationBoost = !string.IsNullOrEmpty(symbol.Relation) ? .35 : 0;
        var raw = (exact ? 5 : 0) + (terms.Length == 0 ? 0 : 2d * matched / terms.Length)
            + relationBoost
            + (candidate.LexicalRank > 0 ? 1d / (60 + candidate.LexicalRank) : 0)
            + (candidate.SemanticRank > 0 ? 1d / (60 + candidate.SemanticRank) + .2 * Math.Max(0, candidate.SemanticSimilarity) : 0)
            + (candidate.KnowledgeReference ? .02 + 1.5 * Math.Clamp(candidate.KnowledgeRelevance, 0, 1) : 0);
        return IsTestFile(symbol.FilePath) && !MentionsTests(query) ? raw * .55 : raw;
    }

    private static bool MentionsTests(string query) =>
        Regex.IsMatch(query, @"\b(tests?|testing|specs?|xunit|playwright|unit|e2e)\b", RegexOptions.IgnoreCase);

    private static bool IsTestFile(string filePath) =>
        Regex.IsMatch(filePath.Replace('\\', '/'), @"(?:^|/)(?:tests?|specs?|__tests__)/|(?:Tests?|\.(?:spec|test))\.[A-Za-z0-9]+$", RegexOptions.IgnoreCase);

    public static IReadOnlyList<CodeEvidence> Select(string query, IEnumerable<CodeSearchCandidate> input, int maxSpans, int maxCharacters)
    {
        var terms = Terms(query);
        var ranked = input
            .GroupBy(candidate => (candidate.Repository.Id, candidate.Repository.Generation, candidate.Symbol.Id))
            .Select(group =>
            {
                var first = group.First();
                var relation = group.Select(item => item.Symbol.Relation).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? "";
                return first with
                {
                    Symbol = string.IsNullOrEmpty(first.Symbol.Relation) && relation.Length > 0 ? first.Symbol with { Relation = relation } : first.Symbol,
                    LexicalRank = group.Where(item => item.LexicalRank > 0).Select(item => item.LexicalRank).DefaultIfEmpty().Min(),
                    SemanticRank = group.Where(item => item.SemanticRank > 0).Select(item => item.SemanticRank).DefaultIfEmpty().Min(),
                    SemanticSimilarity = group.Max(item => item.SemanticSimilarity),
                    KnowledgeReference = group.Any(item => item.KnowledgeReference),
                    KnowledgeRelevance = group.Max(item => item.KnowledgeRelevance),
                    Relationships = group.SelectMany(item => item.Relationships).Distinct().Take(8).ToArray()
                };
            })
            .Select(candidate => (Candidate: candidate, Score: Relevance(query, candidate, terms))).ToList();
        var hasStrongSource = ranked.Any(item => item.Score >= 1);
        var candidates = ranked.Where(item => !hasStrongSource || item.Candidate.LexicalRank > 0 || item.Candidate.SemanticRank > 0
            || !item.Candidate.KnowledgeReference || item.Score >= .1).ToList();
        var selected = new List<CodeEvidence>();
        var seenContents = new HashSet<string>(StringComparer.Ordinal);
        var perRepository = new Dictionary<string, int>();
        var used = 0;
        while (candidates.Count > 0 && selected.Count < maxSpans)
        {
            var next = candidates.OrderByDescending(item => item.Score / (1 + .25 * perRepository.GetValueOrDefault(item.Candidate.Repository.Id)))
                .ThenBy(item => item.Candidate.Repository.FullName, StringComparer.Ordinal)
                .ThenBy(item => item.Candidate.Symbol.FilePath, StringComparer.Ordinal)
                .ThenBy(item => item.Candidate.Symbol.StartLine).First();
            candidates.Remove(next);
            var (repository, symbol, _, _, _, _) = next.Candidate;
            var source = SourceText.Redact(symbol.Source);
            var allLines = SourceText.Lines(source).ToArray();
            if (allLines.Length == 0) continue;
            var sliceStart = symbol.StartLine;
            var actualEnd = symbol.StartLine + allLines.Length - 1;
            var logicalEnd = Math.Max(symbol.EndLine, actualEnd);
            var overlapping = selected.Where(item => item.RepositoryId == repository.Id && item.FilePath == symbol.FilePath &&
                item.StartLine <= logicalEnd && item.EndLine >= symbol.StartLine).ToArray();
            if (overlapping.Length > 0)
            {
                // Deduplicate enclosing parents and already-covered spans; only trim and retain forward continuations
                // (such as a method body or continuation chunk extending past a 6,000-char or 70-line boundary).
                if (overlapping.Any(item => item.StartLine > symbol.StartLine)) continue;
                sliceStart = overlapping.Max(item => item.EndLine) + 1;
                if (sliceStart > actualEnd) continue;
            }
            var lines = allLines[(sliceStart - symbol.StartLine)..];
            if (lines.Count(line => !string.IsNullOrWhiteSpace(line)) == 0 || overlapping.Length > 0 && lines.Length < 2) continue;
            var kept = new StringBuilder();
            var count = 0;
            var path = string.Join('/', symbol.FilePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            var baseUrl = $"https://github.com/{repository.FullName}/blob/{repository.IndexedCommit}/{path}";
            var relationships = next.Candidate.Relationships.Where(item => item.SiteLine >= sliceStart && item.SiteLine <= actualEnd).Take(4).ToArray();
            var relationshipSize = relationships.Length == 0 ? 0 : JsonSerializer.Serialize(relationships).Length;
            var sourceBudget = Math.Min(6000, maxCharacters - used - baseUrl.Length - symbol.QualifiedName.Length - repository.FullName.Length - relationshipSize - 150);
            if (sourceBudget < 128) continue;
            foreach (var line in lines)
            {
                if (kept.Length + line.Length > sourceBudget)
                {
                    if (count == 0) { kept.Append(line[..sourceBudget]); count++; }
                    break;
                }
                kept.Append(line); count++;
            }
            if (count == 0) continue;
            source = kept.ToString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(source, @"\s+", " "))));
            if (seenContents.Contains(hash)) continue;
            var end = sliceStart + count - 1;
            var url = $"{baseUrl}#L{sliceStart}-L{end}";
            var size = source.Length + url.Length + symbol.QualifiedName.Length + repository.FullName.Length + relationshipSize + 100;
            if (used + size > maxCharacters) continue;
            seenContents.Add(hash);
            selected.Add(new(repository.Id, repository.ProjectId, repository.FullName, repository.Branch, repository.IndexedCommit,
                symbol.FilePath, symbol.QualifiedName, sliceStart, end, source, url)
            {
                Relationships = relationships.Where(item => item.SiteLine <= end).ToArray()
            });
            used += size;
            perRepository[repository.Id] = perRepository.GetValueOrDefault(repository.Id) + 1;
        }
        return selected;
    }
}
