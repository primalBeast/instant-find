using System.Data;
using System.IO;
using InstantFind.Models;
using Microsoft.Data.Sqlite;

namespace InstantFind.Services;

/// <summary>
/// SQLite + FTS5 index. User-mode only; database lives in %AppData%\InstantFind.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    public const string RebuildFileName = "index-rebuild.db";

    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;

    public string DatabasePath { get; }

    public IndexDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private + no pooling: Dispose must release the OS handle before File.Replace
            // of index.db (Shared/pool left WAL/SHM held → ERROR_SHARING_VIOLATION 0x80070020).
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5 // seconds; pairs with PRAGMA busy_timeout
        }.ToString();
    }

    /// <summary>Path of the side-by-side rebuild temp DB (e.g. index-rebuild.db).</summary>
    public static string GetRebuildPath(string liveDatabasePath)
    {
        var dir = Path.GetDirectoryName(liveDatabasePath)
                  ?? throw new ArgumentException("Invalid database path.", nameof(liveDatabasePath));
        return Path.Combine(dir, RebuildFileName);
    }

    /// <summary>
    /// On startup: discard leftover rebuild artifacts from a crash mid-rebuild.
    /// Keeps the previous live index intact.
    /// </summary>
    public static void DiscardStaleRebuildArtifacts(string liveDatabasePath)
    {
        var rebuild = GetRebuildPath(liveDatabasePath);
        TryDeleteSqliteFiles(rebuild);
    }

    public void Open()
    {
        if (_connection is not null)
            return;

        Exception? last = null;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                var conn = new SqliteConnection(_connectionString);
                conn.Open();
                _connection = conn;
                last = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
            {
                last = ex;
                try { Thread.Sleep(50 * attempt); } catch { }
            }
        }

        if (_connection is null)
            throw last ?? new IOException("Failed to open index database after retries: " + DatabasePath);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout=5000;
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
                is_directory INTEGER NOT NULL DEFAULT 0,
                attributes TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
            CREATE INDEX IF NOT EXISTS idx_files_directory ON files(directory);
            CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);
            CREATE INDEX IF NOT EXISTS idx_files_size ON files(size);
            CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_utc);

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

        EnsureSchemaMigrations();
    }

    /// <summary>True while a live SqliteConnection is open.</summary>
    public bool IsOpen => _connection is not null;

    /// <summary>
    /// Fully close and release the live SQLite connection (checkpoint WAL, close, dispose, null out,
    /// clear pools). Must run before any File.Replace / Move / Delete of index.db or -wal/-shm.
    /// </summary>
    public void Close()
    {
        lock (_writeLock)
        {
            if (_connection is not null)
            {
                try
                {
                    // Flush WAL so the main file is consistent and locks release before swap/delete
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                catch { /* best-effort */ }

                try { _connection.Close(); } catch { }
                try { _connection.Dispose(); } catch { }
                _connection = null;
            }
        }

        // Belt-and-suspenders: release any pooled/native handles before file ops
        try { SqliteConnection.ClearAllPools(); } catch { }
    }

    /// <summary>Path that last failed during Replace/Move/Delete (for error log).</summary>
    public string? LastFailedFilePath { get; private set; }

    /// <summary>
    /// Atomically replace the live DB file with a successful rebuild DB.
    /// Ensures this instance is fully Closed first (WAL checkpoint + dispose + clear pools),
    /// deletes live/rebuild -wal/-shm, then File.Replace with longer retries for AV scanners.
    /// Caller should Open() after success (or after failure to keep previous index).
    /// </summary>
    public void ReplaceWithRebuildFile(string rebuildDatabasePath)
    {
        if (string.Equals(rebuildDatabasePath, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rebuild path must differ from live database path.");

        // Never swap while our own live connection (or pool) still holds index.db
        Close();

        LastFailedFilePath = null;

        // Encourage release of any lingering native handles before file ops
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { SqliteConnection.ClearAllPools(); } catch { }

        const int maxAttempts = 20; // ~2s at 100ms — AV scanners often release quickly after close
        const int delayMs = 100;
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Drop WAL/SHM sidecars so the main file is consistent before swap.
                TryDeleteSidecars(DatabasePath);
                TryDeleteSidecars(rebuildDatabasePath);

                var dir = Path.GetDirectoryName(DatabasePath)!;
                var backup = Path.Combine(dir, "index.db.bak");
                TryDeleteSqliteFiles(backup);

                if (File.Exists(DatabasePath))
                {
                    LastFailedFilePath = DatabasePath;
                    File.Replace(rebuildDatabasePath, DatabasePath, backup, ignoreMetadataErrors: true);
                    TryDeleteSqliteFiles(backup);
                }
                else
                {
                    LastFailedFilePath = rebuildDatabasePath;
                    File.Move(rebuildDatabasePath, DatabasePath);
                }

                // Sidecars for the new live file come from the rebuild copy; drop stale ones
                TryDeleteSidecars(DatabasePath);
                TryDeleteSqliteFiles(rebuildDatabasePath);
                LastFailedFilePath = null;
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(delayMs);
                try { SqliteConnection.ClearAllPools(); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                Thread.Sleep(delayMs);
                try { SqliteConnection.ClearAllPools(); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        var msg = $"Failed to swap rebuild database after {maxAttempts} retries"
                  + (LastFailedFilePath is not null ? $": {LastFailedFilePath}" : ".");
        throw last ?? new IOException(msg);
    }

    public static bool IsSharingViolation(Exception ex)
    {
        if (ex is IOException io)
        {
            // ERROR_SHARING_VIOLATION 32, ERROR_LOCK_VIOLATION 33
            var hr = io.HResult & 0xFFFF;
            if (hr is 32 or 33) return true;
            var msg = io.Message ?? "";
            if (msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                return true;
            if (msg.Contains("sharing violation", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

    public void UpsertBatch(IReadOnlyList<FileEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        lock (_writeLock)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = Conn().BeginTransaction();
            using var cmd = Conn().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO files (name, path, directory, extension, size, modified_utc, is_directory, attributes)
                VALUES ($name, $path, $directory, $extension, $size, $modified, $isDir, $attrs)
                ON CONFLICT(path) DO UPDATE SET
                    name = excluded.name,
                    directory = excluded.directory,
                    extension = excluded.extension,
                    size = excluded.size,
                    modified_utc = excluded.modified_utc,
                    is_directory = excluded.is_directory,
                    attributes = excluded.attributes;
                """;
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pDir = cmd.Parameters.Add("$directory", SqliteType.Text);
            var pExt = cmd.Parameters.Add("$extension", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pMod = cmd.Parameters.Add("$modified", SqliteType.Text);
            var pIsDir = cmd.Parameters.Add("$isDir", SqliteType.Integer);
            var pAttrs = cmd.Parameters.Add("$attrs", SqliteType.Text);

            for (int i = 0; i < entries.Count; i++)
            {
                // Check often so Cancel does not sit behind a large batch.
                if ((i & 31) == 0)
                    ct.ThrowIfCancellationRequested();

                var e = entries[i];
                pName.Value = e.Name;
                pPath.Value = e.Path;
                pDir.Value = e.Directory;
                pExt.Value = e.Extension;
                pSize.Value = e.Size;
                pMod.Value = e.ModifiedUtc.ToString("o");
                pIsDir.Value = e.IsDirectory ? 1 : 0;
                pAttrs.Value = e.AttributesText ?? string.Empty;
                cmd.ExecuteNonQuery();
            }

            ct.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Delete the folder itself, all descendant paths, and any rows whose
    /// <c>directory</c> equals the folder or is under it.
    /// </summary>
    public void DeleteUnderDirectory(string directoryPrefix)
    {
        lock (_writeLock)
        {
            var patterns = IndexPathHelpers.BuildDeletePatterns(directoryPrefix);
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = """
                DELETE FROM files WHERE
                    path = $exact
                    OR path LIKE $pathLike ESCAPE '\'
                    OR directory = $exact
                    OR directory LIKE $dirLike ESCAPE '\';
                """;
            cmd.Parameters.AddWithValue("$exact", patterns.Exact);
            cmd.Parameters.AddWithValue("$pathLike", patterns.PathLike);
            cmd.Parameters.AddWithValue("$dirLike", patterns.DirectoryLike);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batched prune: remove indexed paths under <paramref name="prefix"/> that no longer
    /// exist on disk. Returns number of rows deleted.
    /// </summary>
    public int PruneMissing(string? prefix = null, int batchSize = 500, CancellationToken ct = default)
    {
        var deleted = 0;
        var normalized = string.IsNullOrWhiteSpace(prefix)
            ? null
            : IndexPathHelpers.NormalizePrefix(prefix);

        long lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = new List<(long Id, string Path)>(batchSize);
            lock (_writeLock)
            {
                using var cmd = Conn().CreateCommand();
                if (normalized is null)
                {
                    cmd.CommandText = """
                        SELECT id, path FROM files
                        WHERE id > $last
                        ORDER BY id
                        LIMIT $limit;
                        """;
                }
                else
                {
                    var patterns = IndexPathHelpers.BuildDeletePatterns(normalized);
                    cmd.CommandText = """
                        SELECT id, path FROM files
                        WHERE id > $last
                          AND (path = $exact OR path LIKE $pathLike ESCAPE '\'
                               OR directory = $exact OR directory LIKE $dirLike ESCAPE '\')
                        ORDER BY id
                        LIMIT $limit;
                        """;
                    cmd.Parameters.AddWithValue("$exact", patterns.Exact);
                    cmd.Parameters.AddWithValue("$pathLike", patterns.PathLike);
                    cmd.Parameters.AddWithValue("$dirLike", patterns.DirectoryLike);
                }
                cmd.Parameters.AddWithValue("$last", lastId);
                cmd.Parameters.AddWithValue("$limit", batchSize);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    batch.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            if (batch.Count == 0)
                break;

            var missingIds = new List<long>();
            foreach (var (id, path) in batch)
            {
                lastId = id;
                try
                {
                    if (!File.Exists(path) && !Directory.Exists(path))
                        missingIds.Add(id);
                }
                catch
                {
                    // If we cannot probe, leave the row
                }
            }

            if (missingIds.Count > 0)
            {
                lock (_writeLock)
                {
                    using var tx = Conn().BeginTransaction();
                    using var cmd = Conn().CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM files WHERE id = $id;";
                    var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                    foreach (var id in missingIds)
                    {
                        pId.Value = id;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                deleted += missingIds.Count;
            }
        }

        return deleted;
    }

    /// <summary>Delete specific paths if they no longer exist; returns deleted paths.</summary>
    public IReadOnlyList<string> PruneMissingPaths(IEnumerable<string> paths)
    {
        var removed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                    continue;
            }
            catch
            {
                continue;
            }

            DeleteByPath(path);
            removed.Add(path);
        }
        return removed;
    }

    public IReadOnlyList<FileEntry> Search(ParsedQuery query, int maxResults, bool includeDirectories, SearchOptions? options = null)
    {
        options ??= new SearchOptions { MatchMode = query.Mode };

        // No enabled drives → nothing visible
        if (options.EnabledDrivePrefixes.Count == 0)
            return Array.Empty<FileEntry>();

        // Match Case / Whole Word / wildcards / regex / NOT → leave FTS.
        // PathScope alone does NOT leave FTS — keep FTS + SQL path prefix filter.
        bool leaveFts = options.MatchCase || options.WholeWord || query.HasWildcards
                        || query.UseRegex || query.HasNotTerms;
        if (leaveFts)
            return SearchWithLike(query, maxResults, includeDirectories, options);

        var results = new List<FileEntry>();
        var fts = QueryParser.BuildFtsMatch(query);
        // Terms with FTS unicode61 separators (_, -, etc.) → LIKE so literals are required.
        if (fts is null && query.Terms.Count > 0)
            return SearchWithLike(query, maxResults, includeDirectories, options);

        using var cmd = Conn().CreateCommand();
        cmd.CommandTimeout = 60; // always finish; never hang the UI spinner forever
        var sql = new System.Text.StringBuilder();
        sql.Append("""
            SELECT f.id, f.name, f.path, f.directory, f.extension, f.size, f.modified_utc, f.is_directory, f.attributes
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
        AppendExcludeFilter(where, cmd, options.ExcludePathPrefixes, alias: "f.");
        AppendSizeDateFilters(where, cmd, query, alias: "f.");

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
            // Plain FTS: skip redundant per-row Matches when filters are fully in SQL.
            // Size/date/ext/path are in SQL; NOT terms leave FTS above.
            while (reader.Read())
            {
                var entry = ReadEntry(reader);
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
        cmd.CommandTimeout = 60; // always finish; never hang the UI spinner forever
        var sql = new System.Text.StringBuilder();
        sql.Append("""
            SELECT id, name, path, directory, extension, size, modified_utc, is_directory, attributes
            FROM files
            """);

        var where = new List<string>();
        var termClauses = new List<string>();

        // Regex: optional literal alphanumeric LIKE prefilter on name; refine in memory
        bool regexMode = query.UseRegex;
        bool regexPathOnly = regexMode && string.IsNullOrEmpty(query.RegexPattern)
                             && !string.IsNullOrEmpty(query.PathScope);
        string? regexLiteral = regexMode && !regexPathOnly
            ? QueryParser.TryExtractLongestLiteral(query.RegexPattern)
            : null;

        if (regexMode && regexLiteral is not null)
        {
            // Prefilter: name LIKE %literal% (never unbounded full-table when a literal exists)
            string nameExpr = options.MatchCase ? "name" : "LOWER(name)";
            string likeExpr = options.MatchCase ? "$regexLit" : "LOWER($regexLit)";
            termClauses.Add($"{nameExpr} LIKE {likeExpr} ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$regexLit", QueryParser.PlainTermToLike(regexLiteral));
        }

        if (!regexMode)
        {
        for (int i = 0; i < query.Terms.Count; i++)
        {
            var term = query.Terms[i];
            var pname = $"$like{i}";
            bool isWildcard = term.Contains('*') || term.Contains('?');
            bool isPathTerm = QueryParser.IsPathTerm(term);

            // Everything-like \72: filter on path only (segment-prefix refined in Matches)
            if (isPathTerm)
            {
                string pathExpr = options.MatchCase ? "path" : "LOWER(path)";
                string likeExpr = options.MatchCase ? pname : $"LOWER({pname})";
                string pattern = isWildcard
                    ? QueryParser.ShellWildcardToLike(term)
                    : QueryParser.PathTermToLike(term);
                termClauses.Add($"{pathExpr} LIKE {likeExpr} ESCAPE '\\'");
                cmd.Parameters.AddWithValue(pname, pattern);
                continue;
            }

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
        AppendExcludeFilter(where, cmd, options.ExcludePathPrefixes, alias: "");
        AppendSizeDateFilters(where, cmd, query, alias: "");

        if (where.Count == 0 && string.IsNullOrWhiteSpace(query.Raw))
            return results;

        // Need at least drive/path/ext/term/size/date filter
        if (where.Count == 0)
            return results;

        sql.Append(" WHERE ").Append(string.Join(" AND ", where));

        // Always stop at maxResults in the read loop.
        // With a regex literal prefilter: LIMIT in SQL (cushion for in-memory rejects).
        // Without a literal: no SQL LIMIT (may scan), but still break at maxResults —
        // never an unbounded "collect everything" intent when a literal exists.
        if (!(regexMode && !regexPathOnly && regexLiteral is null))
        {
            int sqlLimit = regexMode && !regexPathOnly ? maxResults * 4 : maxResults;
            if (sqlLimit < maxResults) sqlLimit = maxResults;
            sql.Append(" LIMIT $limit;");
            cmd.Parameters.AddWithValue("$limit", sqlLimit);
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
            if (query.Terms.Count > 0 || query.NotTerms.Count > 0 || query.Extensions.Count > 0
                || query.UseRegex || !string.IsNullOrEmpty(query.PathScope)
                || query.HasSizeFilter || query.HasDateFilter)
            {
                if (!QueryParser.Matches(query, entry.Name, entry.Path, entry.Extension,
                        options.MatchCase, options.WholeWord, entry.Size, entry.ModifiedUtc, entry.IsDirectory))
                    continue;
            }

            results.Add(entry);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }


    /// <summary>Hide paths under any configured exclude prefix (search-time).</summary>
    private static void AppendExcludeFilter(
        List<string> where,
        SqliteCommand cmd,
        IReadOnlyList<string> prefixes,
        string alias)
    {
        if (prefixes is null || prefixes.Count == 0)
            return;

        for (int i = 0; i < prefixes.Count; i++)
        {
            var pname = $"$ex{i}";
            var exact = $"$exExact{i}";
            var prefix = prefixes[i];
            if (!prefix.EndsWith('\\') && !prefix.EndsWith('/'))
                prefix += "\\";
            var trimmed = prefix.TrimEnd('\\', '/');
            where.Add($"NOT (LOWER({alias}path) = LOWER({exact}) OR LOWER({alias}path) LIKE LOWER({pname}) ESCAPE '\\')");
            cmd.Parameters.AddWithValue(exact, trimmed);
            cmd.Parameters.AddWithValue(pname, QueryParser.EscapeLikeLiteral(prefix) + "%");
        }
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
        var entry = new FileEntry
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
        if (reader.FieldCount > 8 && !reader.IsDBNull(8))
            entry.AttributesText = reader.GetString(8);
        return entry;
    }

    /// <summary>Add attributes column + size/modified indexes for DBs created before v1.0.13.</summary>
    private void EnsureSchemaMigrations()
    {
        try
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "PRAGMA table_info(files);";
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    cols.Add(reader.GetString(1));
            }
            if (!cols.Contains("attributes"))
            {
                using var alter = Conn().CreateCommand();
                alter.CommandText = "ALTER TABLE files ADD COLUMN attributes TEXT NOT NULL DEFAULT '';";
                alter.ExecuteNonQuery();
            }
            using var idx = Conn().CreateCommand();
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_files_size ON files(size);
                CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_utc);
                """;
            idx.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort migration
        }
    }

    private static void AppendSizeDateFilters(
        List<string> where,
        SqliteCommand cmd,
        ParsedQuery query,
        string alias)
    {
        if (query.SizeMin.HasValue || query.SizeMax.HasValue)
            where.Add($"{alias}is_directory = 0");
        if (query.SizeMin.HasValue)
        {
            where.Add($"{alias}size >= $sizeMin");
            cmd.Parameters.AddWithValue("$sizeMin", query.SizeMin.Value);
        }
        if (query.SizeMax.HasValue)
        {
            where.Add($"{alias}size <= $sizeMax");
            cmd.Parameters.AddWithValue("$sizeMax", query.SizeMax.Value);
        }
        if (query.ModifiedAfterUtc.HasValue)
        {
            where.Add($"{alias}modified_utc >= $dmAfter");
            cmd.Parameters.AddWithValue("$dmAfter", query.ModifiedAfterUtc.Value.ToString("o"));
        }
        if (query.ModifiedBeforeUtc.HasValue)
        {
            where.Add($"{alias}modified_utc < $dmBefore");
            cmd.Parameters.AddWithValue("$dmBefore", query.ModifiedBeforeUtc.Value.ToString("o"));
        }
    }

    private static void TryDeleteSqliteFiles(string dbPath)
    {
        TryDelete(dbPath);
        TryDeleteSidecars(dbPath);
    }

    private static void TryDeleteSidecars(string dbPath)
    {
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");
        TryDelete(dbPath + "-journal");
    }

    private static void TryDelete(string path)
    {
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(Math.Min(200, 25 * attempt));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(Math.Min(200, 25 * attempt));
            }
            catch
            {
                return; // Best-effort cleanup
            }
        }
    }

    private SqliteConnection Conn() =>
        _connection ?? throw new InvalidOperationException("Database not open.");

    public void Dispose()
    {
        Close();
    }
}
