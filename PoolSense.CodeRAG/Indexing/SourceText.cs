using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeRAG.Core.Indexing;

public static partial class SourceText
{
    public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".py"] = "python",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".js"] = "javascript",
        [".jsx"] = "jsx",
        [".html"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".md"] = "markdown",
        [".sql"] = "sql"
    };
    public static int Length(string text) => text.EnumerateRunes().Count();
    public static string Take(string text, int count)
    {
        var b = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(count)) b.Append(rune);
        return b.ToString();
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string[] Lines(string text) => Regex.Matches(text, @"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$")
        .Select(m => m.Value).ToArray();
    public static string Searchable(string source)
    {
        var split = Regex.Replace(source, "([a-z0-9])([A-Z])", "$1 $2");
        split = Regex.Replace(split, "([A-Z])([A-Z][a-z])", "$1 $2").Replace('_', ' ');
        return source + "\n" + split;
    }
    public static string Redact(string source)
    {
        source = SecretAssignment().Replace(source, m =>
            m.Groups[1].Value + "\"[REDACTED]" + string.Concat(m.Groups[2].Value.Where(c => c is '\r' or '\n')) + "\"");
        return TokenPattern().Replace(source, "[REDACTED]");
    }
    [GeneratedRegex("""(?ix)((?:password|passwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\s*(?:=|:)\s*)(@?"(?:""|\\.|[^"])*"|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)""", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex SecretAssignment();
    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]+)?)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex TokenPattern();
}
