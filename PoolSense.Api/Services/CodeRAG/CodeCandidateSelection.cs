using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;

namespace PoolSense.Api.Services.CodeRAG;

public sealed record CodeSearchCandidate(CodeRepository Repository, Symbol Symbol, int LexicalRank = 0, int SemanticRank = 0,
    double SemanticSimilarity = 0, bool KnowledgeReference = false)
{
    public double KnowledgeRelevance { get; init; }
}

public sealed class CodeCandidateBuffer(string query, int capacity) : IEnumerable<CodeSearchCandidate>
{
    private readonly object gate = new();
    private readonly List<CodeSearchCandidate> items = [];
    private readonly Dictionary<string, double> scores = new();
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
        var score = CodeCandidateSelection.Relevance(query, candidate);
        lock (gate)
        {
            var existing = items.FindIndex(item => item.Repository.Id == candidate.Repository.Id
                && item.Repository.Generation == candidate.Repository.Generation && item.Symbol.Id == candidate.Symbol.Id);
            if (existing >= 0)
            {
                var previous = items[existing];
                items[existing] = previous with
                {
                    LexicalRank = PositiveMin(previous.LexicalRank, candidate.LexicalRank),
                    SemanticRank = PositiveMin(previous.SemanticRank, candidate.SemanticRank),
                    SemanticSimilarity = Math.Max(previous.SemanticSimilarity, candidate.SemanticSimilarity),
                    KnowledgeReference = previous.KnowledgeReference || candidate.KnowledgeReference,
                    KnowledgeRelevance = Math.Max(previous.KnowledgeRelevance, candidate.KnowledgeRelevance)
                };
                scores[Key(items[existing])] = CodeCandidateSelection.Relevance(query, items[existing]);
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

    public static double Relevance(string query, CodeSearchCandidate candidate)
    {
        var terms = Terms(query);
        var symbol = candidate.Symbol;
        var text = $"{symbol.QualifiedName} {symbol.FilePath} {symbol.Source}";
        var matched = terms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        var exact = terms.Any(term => symbol.Name.Equals(term, StringComparison.OrdinalIgnoreCase)
            || symbol.QualifiedName.Split('.').Contains(term, StringComparer.OrdinalIgnoreCase))
            || query.Contains(symbol.FilePath, StringComparison.OrdinalIgnoreCase);
        // Raw BM25 scores from independent databases are not compared. Fuse within-list ranks,
        // globally comparable query-term coverage and exact identifier/path matches.
        return (exact ? 5 : 0) + (terms.Length == 0 ? 0 : 2d * matched / terms.Length)
            + (candidate.LexicalRank > 0 ? 1d / (60 + candidate.LexicalRank) : 0)
            + (candidate.SemanticRank > 0 ? 1d / (60 + candidate.SemanticRank) + .2 * Math.Max(0, candidate.SemanticSimilarity) : 0)
            + (candidate.KnowledgeReference ? .02 + 1.5 * Math.Clamp(candidate.KnowledgeRelevance, 0, 1) : 0);
    }

    public static IReadOnlyList<CodeEvidence> Select(string query, IEnumerable<CodeSearchCandidate> input, int maxSpans, int maxCharacters)
    {
        var all = input.ToArray();
        var hasStrongSource = all.Any(candidate => Relevance(query, candidate) >= 1);
        var candidates = all.Where(candidate => !hasStrongSource || candidate.LexicalRank > 0 || candidate.SemanticRank > 0
                || !candidate.KnowledgeReference || Relevance(query, candidate) >= .1)
            .GroupBy(candidate => (candidate.Repository.Id, candidate.Repository.Generation, candidate.Symbol.Id))
            .Select(group => group.First() with
            {
                LexicalRank = group.Where(item => item.LexicalRank > 0).Select(item => item.LexicalRank).DefaultIfEmpty().Min(),
                SemanticRank = group.Where(item => item.SemanticRank > 0).Select(item => item.SemanticRank).DefaultIfEmpty().Min(),
                SemanticSimilarity = group.Max(item => item.SemanticSimilarity),
                KnowledgeReference = group.Any(item => item.KnowledgeReference),
                KnowledgeRelevance = group.Max(item => item.KnowledgeRelevance)
            })
            .Select(candidate => (Candidate: candidate, Score: Relevance(query, candidate))).ToList();
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
            if (selected.Any(item => item.RepositoryId == repository.Id && item.FilePath == symbol.FilePath &&
                item.StartLine <= symbol.EndLine && item.EndLine >= symbol.StartLine)) continue;
            var source = SourceText.Redact(symbol.Source);
            var lines = SourceText.Lines(source).ToArray();
            var kept = new StringBuilder();
            var count = 0;
            var path = string.Join('/', symbol.FilePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            var baseUrl = $"https://github.com/{repository.FullName}/blob/{repository.IndexedCommit}/{path}";
            var sourceBudget = Math.Min(6000, maxCharacters - used - baseUrl.Length - symbol.QualifiedName.Length - repository.FullName.Length - 150);
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
            var end = symbol.StartLine + count - 1;
            var url = $"{baseUrl}#L{symbol.StartLine}-L{end}";
            var size = source.Length + url.Length + symbol.QualifiedName.Length + repository.FullName.Length + 100;
            if (used + size > maxCharacters) continue;
            seenContents.Add(hash);
            selected.Add(new(repository.Id, repository.ProjectId, repository.FullName, repository.Branch, repository.IndexedCommit,
                symbol.FilePath, symbol.QualifiedName, symbol.StartLine, end, source, url));
            used += size;
            perRepository[repository.Id] = perRepository.GetValueOrDefault(repository.Id) + 1;
        }
        return selected;
    }
}
