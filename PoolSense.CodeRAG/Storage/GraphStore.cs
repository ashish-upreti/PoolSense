using Microsoft.Data.Sqlite;

namespace CodeRAG.Core.Storage;

public sealed record Symbol(string Id, string RepoRoot, string FilePath, string Kind, string Name,
    string QualifiedName, int StartLine, int EndLine, string? ParentId, string Source, string Docstring,
    string Relation = "");
public sealed record Edge(string FromId, string ToId, string Kind, int? SiteLine = null,
    int? SiteColumn = null, string Resolution = "syntax");
public sealed record IndexSummary(string Root, string IndexedAt, int FileCount, int SymbolCount, int EdgeCount, int Version);

public static class GraphStore
{
    public const int Version = 1;
    public const int ApplicationId = 0x43524743;
    public const string Parser = "roslyn-5-csharp";
    public static string RootKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        path = Path.GetFullPath(path);
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 15
        }.ToString());
        try
        {
            db.Open();
            var id = Convert.ToInt32(Scalar(db, "PRAGMA application_id"));
            var tables = Convert.ToInt32(Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type='table'"));
            if (id != ApplicationId && (id != 0 || tables != 0 || readOnly))
                throw new InvalidDataException("Not a C# CodeRAG database. Choose a separate C# index path.");
            Execute(db, "PRAGMA foreign_keys=ON");
            if (!readOnly)
            {
                Execute(db, $"PRAGMA application_id={ApplicationId}");
                Execute(db, """
                    CREATE TABLE IF NOT EXISTS repositories(
                      root TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, file_count INTEGER NOT NULL,
                      symbol_count INTEGER NOT NULL, edge_count INTEGER NOT NULL, index_version INTEGER NOT NULL,
                      parser TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS symbols(
                      id TEXT PRIMARY KEY, repo_root TEXT NOT NULL REFERENCES repositories(root) ON DELETE CASCADE,
                      file_path TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, qualified_name TEXT NOT NULL,
                      start_line INTEGER NOT NULL, end_line INTEGER NOT NULL,
                      parent_id TEXT REFERENCES symbols(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
                      source TEXT NOT NULL, docstring TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS symbols_repo_kind ON symbols(repo_root,kind);
                    CREATE INDEX IF NOT EXISTS symbols_repo_name ON symbols(repo_root,name);
                    CREATE INDEX IF NOT EXISTS symbols_repo_file ON symbols(repo_root,file_path,start_line);
                    CREATE TABLE IF NOT EXISTS edges(
                      id INTEGER PRIMARY KEY, repo_root TEXT NOT NULL REFERENCES repositories(root) ON DELETE CASCADE,
                      from_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
                      to_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
                      kind TEXT NOT NULL, site_line INTEGER, site_col INTEGER, resolution TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS edges_from ON edges(repo_root,from_id);
                    CREATE INDEX IF NOT EXISTS edges_to ON edges(repo_root,to_id);
                    CREATE VIRTUAL TABLE IF NOT EXISTS symbol_search USING fts5(
                      symbol_id UNINDEXED,name,qualified_name,kind,source,docstring,tokenize='unicode61');
                    """);
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    public static SqliteCommand Command(SqliteConnection db, string sql, params object?[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        return command;
    }
    public static int Execute(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }
    public static object? Scalar(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteScalar();
    }
    public static List<T> Query<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var command = Command(db, sql, args);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }
    public static Symbol ReadSymbol(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("repo_root")),
        r.GetString(r.GetOrdinal("file_path")), r.GetString(r.GetOrdinal("kind")),
        r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("qualified_name")),
        r.GetInt32(r.GetOrdinal("start_line")), r.GetInt32(r.GetOrdinal("end_line")),
        r.IsDBNull(r.GetOrdinal("parent_id")) ? null : r.GetString(r.GetOrdinal("parent_id")),
        r.GetString(r.GetOrdinal("source")), r.GetString(r.GetOrdinal("docstring")));
    public static List<IndexSummary> Summaries(SqliteConnection db) => Query(db,
        "SELECT root,indexed_at,file_count,symbol_count,edge_count,index_version FROM repositories ORDER BY root",
        r => new IndexSummary(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
    public static bool IsReady(string path, IEnumerable<string> roots, bool requireFiles = false)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var db = Open(path, readOnly: true);
            return roots.All(root => Convert.ToInt32(Scalar(db,
                "SELECT count(*) FROM repositories WHERE root=@p0 AND index_version=@p1 AND parser=@p2 AND (@p3=0 OR file_count>0)",
                RootKey(root), Version, Parser, requireFiles ? 1 : 0)) == 1);
        }
        catch (SqliteException) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
