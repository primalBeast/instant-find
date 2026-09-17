using System.Data;
using InstantFind.Models;
using Microsoft.Data.Sqlite;

namespace InstantFind.Services;

/// <summary>
/// SQLite + FTS5 index. User-mode only; database lives in %AppData%\InstantFind.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;

    public IndexDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public void Open()
    {
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                directory TEXT NOT NULL,
                extension TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                modified_utc TEXT NOT NULL,
                is_directory INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
            CREATE INDEX IF NOT EXISTS idx_files_directory ON files(directory);

            CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                name,
                path,
                content='files',
                content_rowid='id',
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS files_ai AFTER INSERT ON files BEGIN
                INSERT INTO files_fts(rowid, name, path) VALUES (new.id, new.name, new.path);
            END;

            CREATE TRIGGER IF NOT EXISTS files_ad AFTER DELETE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', old.id, old.name, old.path);
            END;

            CREATE TRIGGER IF NOT EXISTS files_au AFTER UPDATE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', old.id, old.name, old.path);
                INSERT INTO files_fts(rowid, name, path) VALUES (new.id, new.name, new.path);
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        lock (_writeLock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "DELETE FROM files; INSERT INTO files_fts(files_fts) VALUES('rebuild');";
            cmd.ExecuteNonQuery();
        }
    }

    public long Count()
    {
        using var cmd = Conn().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void UpsertBatch(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = Conn().BeginTransaction();
            using var cmd = Conn().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO files (name, path, directory, extension, size, modified_utc, is_directory)
                VALUES ($name, $path, $directory, $extension, $size, $modified, $isDir)
                ON CONFLICT(path) DO UPDATE SET
                    name = excluded.name,
                    directory = excluded.directory,
                    extension = excluded.extension,
                    size = excluded.size,
                    modified_utc = excluded.modified_utc,
                    is_directory = excluded.is_directory;
                """;
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pDir = cmd.Parameters.Add("$directory", SqliteType.Text);
            var pExt = cmd.Parameters.Add("$extension", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pMod = cmd.Parameters.Add("$modified", SqliteType.Text);
            var pIsDir = cmd.Parameters.Add("$isDir", SqliteType.Integer);

            foreach (var e in entries)
            {
                pName.Value = e.Name;
                pPath.Value = e.Path;
                pDir.Value = e.Directory;
                pExt.Value = e.Extension;
                pSize.Value = e.Size;
                pMod.Value = e.ModifiedUtc.ToString("o");
                pIsDir.Value = e.IsDirectory ? 1 : 0;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void DeleteByPath(string path)
    {
        lock (_writeLock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteUnderDirectory(string directoryPrefix)
    {
        lock (_writeLock)
        {
            var prefix = directoryPrefix.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = $exact OR path LIKE $like ESCAPE '\\';";
            cmd.Parameters.AddWithValue("$exact", prefix);
            // Escape LIKE wildcards in path
            var like = EscapeLike(prefix) + System.IO.Path.DirectorySeparatorChar + "%";
            cmd.Parameters.AddWithValue("$like", like);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FileEntry> Search(ParsedQuery query, int maxResults, bool includeDirectories, SearchOptions? options = null)
    {
        options ??= new SearchOptions { MatchMode = query.Mode };

        // No enabled drives → nothing visible
        if (options.EnabledDrivePrefixes.Count == 0)
            return Array.Empty<FileEntry>();

        // Match Case / Whole Word / wildcards / regex / path-scope listing → leave FTS
        bool leaveFts = options.MatchCase || options.WholeWord || query.HasWildcards
                        || query.UseRegex || !string.IsNullOrEmpty(query.PathScope);
        if (leaveFts)
            return SearchWithLike(query, maxResults, includeDirectories, options);

        var results = new List<FileEntry>();
        var fts = QueryParser.BuildFtsMatch(query);

        using var cmd = Conn().CreateCommand();
        var sql = new System.Text.StringBuilder();
        sql.Append("""
            SELECT f.id, f.name, f.path, f.directory, f.extension, f.size, f.modified_utc, f.is_directory
            FROM files f
            """);

        var where = new List<string>();
        if (!string.IsNullOrEmpty(fts))
        {
            sql.Append(" JOIN files_fts ON files_fts.rowid = f.id ");
            where.Add("files_fts MATCH $fts");
            cmd.Parameters.AddWithValue("$fts", fts);
        }

        if (query.Extensions.Count > 0)
        {
            var extParams = new List<string>();
            for (int i = 0; i < query.Extensions.Count; i++)
            {
                var pname = $"$ext{i}";
                extParams.Add(pname);
                cmd.Parameters.AddWithValue(pname, query.Extensions[i].ToLowerInvariant());
            }
            where.Add($"LOWER(f.extension) IN ({string.Join(",", extParams)})");
        }

        if (!includeDirectories)
            where.Add("f.is_directory = 0");

        AppendDriveFilter(where, cmd, options.EnabledDrivePrefixes, alias: "f.");
        AppendPathScopeFilter(where, cmd, query.PathScope, options.MatchCase, alias: "f.");

        // Empty query with only ext: or completely empty — allow browsing limited set
        if (where.Count == 0 && string.IsNullOrWhiteSpace(query.Raw))
            return results;

        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));

        sql.Append(" LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", maxResults);
        cmd.CommandText = sql.ToString();

        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entry = ReadEntry(reader);
                if (query.Terms.Count > 0 || query.UseRegex || !string.IsNullOrEmpty(query.PathScope))
                {
                    if (!QueryParser.Matches(query, entry.Name, entry.Path, entry.Extension, options.MatchCase, options.WholeWord))
                        continue;
                }

                results.Add(entry);
                if (results.Count >= maxResults)
                    break;
            }
        }
        catch (SqliteException)
        {
            // Bad FTS syntax — fall back to LIKE search
            return SearchWithLike(query, maxResults, includeDirectories, options);
        }

        return results;
    }

    /// <summary>
    /// Primary path for wildcard / Match Case / Whole Word queries: SQL LIKE or = on name/path.
    /// Also used as FTS failure fallback for plain terms.
    /// </summary>
    private IReadOnlyList<FileEntry> SearchWithLike(
        ParsedQuery query,
        int maxResults,
        bool includeDirectories,
        SearchOptions options)
    {
        var results = new List<FileEntry>();
        using var cmd = Conn().CreateCommand();
        var sql = new System.Text.StringBuilder();
        sql.Append("""
            SELECT id, name, path, directory, extension, size, modified_utc, is_directory
            FROM files
            """);

        var where = new List<string>();
        var termClauses = new List<string>();

        // Regex: no SQL term prefilter — stream under drive/path scope and filter in memory
        bool regexMode = query.UseRegex;
        bool regexPathOnly = regexMode && string.IsNullOrEmpty(query.RegexPattern)
                             && !string.IsNullOrEmpty(query.PathScope);

        if (!regexMode)
        {
        for (int i = 0; i < query.Terms.Count; i++)
        {
            var term = query.Terms[i];
            var pname = $"$like{i}";
            bool isWildcard = term.Contains('*') || term.Contains('?');

            if (options.WholeWord && !isWildcard)
            {
                // Exact = match on name or path segment via LIKE boundaries is refined in-memory;
                // SQL uses name = / LOWER(name) = for primary filter.
                if (options.MatchCase)
                {
                    termClauses.Add($"(name = {pname} OR path = {pname} OR path LIKE {pname}_seg ESCAPE '\\')");
                    cmd.Parameters.AddWithValue(pname, term);
                    cmd.Parameters.AddWithValue(pname + "_seg", "%\\" + QueryParser.EscapeLikeLiteral(term));
                }
                else
                {
                    termClauses.Add($"(LOWER(name) = LOWER({pname}) OR LOWER(path) = LOWER({pname}) OR LOWER(path) LIKE LOWER({pname}_seg) ESCAPE '\\')");
                    cmd.Parameters.AddWithValue(pname, term);
                    // Match ...\term or ...\term\... or ...\term.ext — refine in-memory
                    cmd.Parameters.AddWithValue(pname + "_seg", "%\\" + QueryParser.EscapeLikeLiteral(term) + "%");
                }
            }
            else
            {
                string pattern = isWildcard
                    ? QueryParser.ShellWildcardToLike(term)
                    : QueryParser.PlainTermToLike(term);

                // Filename wildcards match name only so D*.pdf cannot hit via a folder like \docs\
                // Plain terms and path-shaped wildcards may also match path.
                bool pathWildcard = isWildcard && (term.Contains('\\') || term.Contains('/'));
                string nameExpr = options.MatchCase ? "name" : "LOWER(name)";
                string pathExpr = options.MatchCase ? "path" : "LOWER(path)";
                string likeExpr = options.MatchCase ? pname : $"LOWER({pname})";

                if (isWildcard && !pathWildcard)
                    termClauses.Add($"{nameExpr} LIKE {likeExpr} ESCAPE '\\'");
                else
                    termClauses.Add($"({nameExpr} LIKE {likeExpr} ESCAPE '\\' OR {pathExpr} LIKE {likeExpr} ESCAPE '\\')");
                cmd.Parameters.AddWithValue(pname, pattern);
            }
        }
        } // end !regexMode term prefilter

        if (termClauses.Count > 0)
        {
            var joiner = query.Mode == MatchMode.Or ? " OR " : " AND ";
            where.Add("(" + string.Join(joiner, termClauses) + ")");
        }

        if (query.Extensions.Count > 0)
        {
            var extParams = new List<string>();
            for (int i = 0; i < query.Extensions.Count; i++)
            {
                var pname = $"$ext{i}";
                extParams.Add(pname);
                cmd.Parameters.AddWithValue(pname, query.Extensions[i].ToLowerInvariant());
            }
            where.Add($"LOWER(extension) IN ({string.Join(",", extParams)})");
        }

        if (!includeDirectories)
            where.Add("is_directory = 0");

        AppendDriveFilter(where, cmd, options.EnabledDrivePrefixes, alias: "");
        AppendPathScopeFilter(where, cmd, query.PathScope, options.MatchCase, alias: "");

        if (where.Count == 0 && string.IsNullOrWhiteSpace(query.Raw))
            return results;

        // Need at least drive/path/ext/term filter
        if (where.Count == 0)
            return results;

        sql.Append(" WHERE ").Append(string.Join(" AND ", where));

        // Regex (non path-only): do not LIMIT in SQL — filter in memory up to maxResults.
        // Path-only / normal LIKE: LIMIT in SQL then refine.
        bool streamForRegex = regexMode && !regexPathOnly;
        if (!streamForRegex)
        {
            sql.Append(" LIMIT $limit;");
            // Fetch a cushion when WholeWord/MatchCase may reject rows after LIKE
            cmd.Parameters.AddWithValue("$limit", maxResults);
        }
        else
        {
            sql.Append(';');
        }

        cmd.CommandText = sql.ToString();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = ReadEntry(reader);
            // Refine with in-memory matcher (wildcards / case / whole-word / regex / path scope)
            if (query.Terms.Count > 0 || query.Extensions.Count > 0 || query.UseRegex
                || !string.IsNullOrEmpty(query.PathScope))
            {
                if (!QueryParser.Matches(query, entry.Name, entry.Path, entry.Extension, options.MatchCase, options.WholeWord))
                    continue;
            }

            results.Add(entry);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    private static void AppendDriveFilter(
        List<string> where,
        SqliteCommand cmd,
        IReadOnlyList<string> prefixes,
        string alias)
    {
        if (prefixes.Count == 0)
        {
            where.Add("1 = 0");
            return;
        }

        var parts = new List<string>();
        for (int i = 0; i < prefixes.Count; i++)
        {
            var pname = $"$drv{i}";
            // Path prefix: C:\...  (also accept forward slash)
            parts.Add($"{alias}path LIKE {pname} ESCAPE '\\'");
            var prefix = prefixes[i];
            if (!prefix.EndsWith('\\') && !prefix.EndsWith('/'))
                prefix += "\\";
            cmd.Parameters.AddWithValue(pname, QueryParser.EscapeLikeLiteral(prefix) + "%");
        }
        where.Add("(" + string.Join(" OR ", parts) + ")");
    }

    /// <summary>
    /// Restricts results to the directory scope (path prefix). Includes the directory itself
    /// and all descendants. Case follows MatchCase.
    /// </summary>
    private static void AppendPathScopeFilter(
        List<string> where,
        SqliteCommand cmd,
        string? pathScope,
        bool matchCase,
        string alias)
    {
        if (string.IsNullOrEmpty(pathScope))
            return;

        var trimmed = pathScope.TrimEnd('\\', '/');
        var childPrefix = trimmed + "\\";

        if (matchCase)
        {
            where.Add($"({alias}path = $scopeExact OR {alias}path LIKE $scopeLike ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$scopeExact", trimmed);
            cmd.Parameters.AddWithValue("$scopeLike", QueryParser.EscapeLikeLiteral(childPrefix) + "%");
        }
        else
        {
            where.Add($"(LOWER({alias}path) = LOWER($scopeExact) OR LOWER({alias}path) LIKE LOWER($scopeLike) ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$scopeExact", trimmed);
            cmd.Parameters.AddWithValue("$scopeLike", QueryParser.EscapeLikeLiteral(childPrefix) + "%");
        }
    }

    private static FileEntry ReadEntry(SqliteDataReader reader)
    {
        return new FileEntry
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Path = reader.GetString(2),
            Directory = reader.GetString(3),
            Extension = reader.GetString(4),
            Size = reader.GetInt64(5),
            ModifiedUtc = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            IsDirectory = reader.GetInt64(7) != 0
        };
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    private SqliteConnection Conn() =>
        _connection ?? throw new InvalidOperationException("Database not open.");

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
