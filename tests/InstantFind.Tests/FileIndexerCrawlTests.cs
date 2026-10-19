using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using InstantFind.Models;
using InstantFind.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace InstantFind.Tests;

public class FileIndexerCrawlTests
{
    [Fact]
    public void ListingOptions_keep_hidden_system_and_do_not_recurse()
    {
        var options = CrawlListing.CreateOptions();
        Assert.Equal((FileAttributes)0, options.AttributesToSkip);
        Assert.False(options.IgnoreInaccessible);
        Assert.False(options.RecurseSubdirectories);
    }

    [Fact]
    public void CreateFromListing_uses_find_buffer_without_opening_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), "if-missing-" + Guid.NewGuid().ToString("N"), "placeholder.txt");
        var when = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var listed = new CrawlListedEntry(
            "placeholder.txt",
            missing,
            FileAttributes.Archive | FileAttributes.Hidden,
            length: 42,
            when);

        var entry = CrawlListing.CreateFromListing(listed);

        Assert.False(File.Exists(missing));
        Assert.Equal("placeholder.txt", entry.Name);
        Assert.Equal(missing, entry.Path);
        Assert.Equal("txt", entry.Extension);
        Assert.Equal(42, entry.Size);
        Assert.False(entry.IsDirectory);
        Assert.Equal(when.UtcDateTime, entry.ModifiedUtc);
        Assert.Contains("A", entry.AttributesText);
        Assert.Contains("H", entry.AttributesText);
    }

    [Fact]
    public void Reparse_gate_skips_junctions_and_enters_onedrive_directories()
    {
        var pending = new ConcurrentQueue<string>();
        var batch = new List<FileEntry>();
        var skips = new CrawlSkipLog();
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> prefixes = Array.Empty<string>();

        var junction = new CrawlListedEntry(
            "linked",
            @"C:\Users\x\Documents\linked",
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            0,
            DateTimeOffset.UtcNow);
        var cloudDir = new CrawlListedEntry(
            "Reports",
            @"C:\Users\x\OneDrive - Diebold Nixdorf\Reports",
            FileAttributes.Directory | FileAttributes.ReparsePoint | (FileAttributes)0x00400000,
            0,
            DateTimeOffset.UtcNow);

        Assert.Equal(
            CrawlConsumeResult.SkippedIntentional,
            CrawlListing.TryConsume(junction, true, exclude, prefixes, pending, batch, skips));
        Assert.Equal(
            CrawlConsumeResult.Indexed,
            CrawlListing.TryConsume(cloudDir, true, exclude, prefixes, pending, batch, skips));

        Assert.DoesNotContain(pending, p => p.Contains("linked", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(batch, e => e.Name == "linked");
        Assert.Contains(@"C:\Users\x\OneDrive - Diebold Nixdorf\Reports", pending);
        Assert.Contains(batch, e => e.Name == "Reports" && e.IsDirectory);
        Assert.Equal(0, skips.Total);
    }

    [Fact]
    public void Directory_row_failure_still_enqueues_and_keeps_a_zero_size_row()
    {
        var root = @"C:\Users\x\OneDrive - Diebold Nixdorf";
        var bad = new CrawlListedEntry(
            "cloud docs",
            root + @"\cloud docs",
            FileAttributes.Directory,
            0,
            DateTimeOffset.UtcNow);
        var sibling = new CrawlListedEntry(
            "local.txt",
            root + @"\local.txt",
            FileAttributes.Archive,
            11,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var pending = new ConcurrentQueue<string>();
        var batch = new List<FileEntry>();
        var skips = new CrawlSkipLog();
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FileEntry Build(CrawlListedEntry entry)
        {
            if (entry.Name == "cloud docs")
                throw new IOException("The cloud file provider is not running.");
            return CrawlListing.CreateFromListing(entry);
        }

        var logBefore = ReadLog();
        Assert.Equal(CrawlConsumeResult.Indexed, CrawlListing.TryConsume(
            bad, true, exclude, Array.Empty<string>(), pending, batch, skips, Build));
        Assert.Equal(CrawlConsumeResult.Indexed, CrawlListing.TryConsume(
            sibling, true, exclude, Array.Empty<string>(), pending, batch, skips, Build));

        Assert.Contains(bad.FullPath, pending);
        var dirRow = Assert.Single(batch, e => e.IsDirectory);
        Assert.Equal("cloud docs", dirRow.Name);
        Assert.Equal(bad.FullPath, dirRow.Path);
        Assert.Equal(0, dirRow.Size);
        Assert.Equal(default, dirRow.ModifiedUtc);
        var fileRow = Assert.Single(batch, e => !e.IsDirectory);
        Assert.Equal("local.txt", fileRow.Name);
        Assert.Equal(11, fileRow.Size);

        var added = ReadLog().Substring(logBefore.Length);
        Assert.Contains("Context: CrawlSkip", added);
        Assert.Contains("kind=directory-row", added);
        Assert.Contains("System.IO.IOException", added);
        Assert.Contains("The cloud file provider is not running.", added);
        Assert.Contains(bad.FullPath, added);
    }

    [Fact]
    public void Listing_entry_failure_keeps_later_siblings_and_logs()
    {
        var sibling = new CrawlListedEntry(
            "kept.txt",
            @"C:\data\kept.txt",
            FileAttributes.Archive,
            4,
            DateTimeOffset.UtcNow);
        var steps = new Queue<Action>();
        steps.Enqueue(() => throw new IOException("placeholder stat failed"));
        steps.Enqueue(() => { });

        var pending = new ConcurrentQueue<string>();
        var batch = new List<FileEntry>();
        var skips = new CrawlSkipLog();
        long skipped = 0;
        var logBefore = ReadLog();

        CrawlListing.Consume(
            new ScriptedListing(steps, sibling),
            @"C:\data",
            includeDirectories: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            pending,
            batch,
            skips,
            ref skipped,
            CancellationToken.None,
            onIndexed: () => { },
            flush: () => { });

        Assert.Equal(1, skipped);
        var kept = Assert.Single(batch);
        Assert.Equal("kept.txt", kept.Name);
        Assert.Equal(4, kept.Size);
        var added = ReadLog().Substring(logBefore.Length);
        Assert.Contains("kind=listing-entry", added);
        Assert.Contains("placeholder stat failed", added);
        Assert.Contains(@"C:\data", added);
    }

    [Fact]
    public void ContinueOnError_records_win32_and_returns_true()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var skips = new CrawlSkipLog();
            using var probe = new ContinueProbe(dir, skips);
            var win32 = new Win32Exception(5);
            var logBefore = ReadLog();
            Assert.True(probe.Call(5));
            Assert.Equal(1, skips.Total);
            Assert.Equal(1, skips.Folders);

            var added = ReadLog().Substring(logBefore.Length);
            Assert.Contains("Context: CrawlSkip", added);
            Assert.Contains("kind=listing", added);
            Assert.Contains("win32=5", added);
            Assert.Contains("System.ComponentModel.Win32Exception", added);
            Assert.Contains($"0x{win32.HResult:X8}", added);
            Assert.Contains(win32.Message, added);
            Assert.Contains(dir, added);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CrawlSkip_detail_is_capped_and_summarized()
    {
        var pathToken = "skip-path-" + Guid.NewGuid().ToString("N");
        var boom = "skip-boom-" + Guid.NewGuid().ToString("N");
        var log = new CrawlSkipLog();
        var logBefore = ReadLog();
        for (var i = 0; i < 250; i++)
            log.Record(pathToken, "cap-probe", new IOException(boom));
        log.WriteSummary(pathToken);

        Assert.Equal(250, log.Total);
        var added = ReadLog().Substring(logBefore.Length);
        Assert.Equal(CrawlSkipLog.DetailCap, CountOf(added, boom));
        Assert.Contains("Further crawl skips omitted after 200", added);
        Assert.Contains("Summary: 250 crawl skip(s)", added);
        // 200 detail lines + the omission line + the summary line.
        Assert.Equal(CrawlSkipLog.DetailCap + 2, CountOf(added, pathToken));
    }

    [Fact]
    public void Rebuild_status_appends_skipped_folders_only_when_nonzero()
    {
        var both = new IndexProgress
        {
            IsComplete = true,
            FilesIndexed = 31,
            SkippedLocked = 4,
            SkippedFolders = 2
        };
        Assert.Equal("Indexed 31 items (skipped 4 locked) · Skipped 2 folders", both.RebuildCompleteStatus);
        Assert.Equal(both.RebuildCompleteStatus, both.StatusText);

        var foldersOnly = new IndexProgress { IsComplete = true, FilesIndexed = 31, SkippedFolders = 2 };
        Assert.Equal("Indexed 31 items · Skipped 2 folders", foldersOnly.StatusText);

        var lockedOnly = new IndexProgress { IsComplete = true, FilesIndexed = 31, SkippedLocked = 4 };
        Assert.Equal("Indexed 31 items (skipped 4 locked)", lockedOnly.StatusText);

        var clean = new IndexProgress { IsComplete = true, FilesIndexed = 31 };
        Assert.Equal("Indexed 31 items", clean.StatusText);

        var scanning = new IndexProgress { FilesIndexed = 10, SkippedFolders = 3 };
        Assert.Equal("Scanning… 10 items", scanning.StatusText);
    }

    [Fact]
    public void Crawl_indexes_sibling_folders_including_hidden_and_system()
    {
        var parent = Path.Combine(Path.GetTempPath(), "if-crawl-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "OneDrive - Diebold Nixdorf");
        var dbPath = Path.Combine(parent, "index.db");
        Directory.CreateDirectory(root);

        var localReader = Path.Combine(root, "local HTML reader");
        var localTracker = Path.Combine(root, "local issue tracker");
        var hiddenDir = Path.Combine(root, "hidden-folder");
        var nested = Path.Combine(localTracker, "nested");
        Directory.CreateDirectory(localReader);
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(hiddenDir);

        var plain = Path.Combine(localReader, "plain.txt");
        var system = Path.Combine(localReader, "system.txt");
        var hidden = Path.Combine(localTracker, "hidden.txt");
        var deep = Path.Combine(nested, "deep.txt");
        var inside = Path.Combine(hiddenDir, "inside.txt");
        File.WriteAllText(plain, "plain-text");
        File.WriteAllText(system, "sys");
        File.WriteAllText(hidden, "hid");
        File.WriteAllText(deep, "deep");
        File.WriteAllText(inside, "inside");

        var systemMarked = TryAddAttribute(system, FileAttributes.System);
        var hiddenMarked = TryAddAttribute(hidden, FileAttributes.Hidden);
        var hiddenDirMarked = TryAddAttribute(hiddenDir, FileAttributes.Hidden);

        try
        {
            using var db = new IndexDatabase(dbPath);
            db.Open();
            var settings = new AppSettings
            {
                IncludeDirectories = true,
                IndexedRoots = new List<string> { root },
                ExcludedDirectoryNames = new List<string> { "node_modules", ".git", ".svn" }
            };
            var indexer = new FileIndexer(db, settings);
            indexer.CrawlIntoExisting(root);
            db.Close();

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(localReader),
                Path.GetFullPath(plain),
                Path.GetFullPath(system),
                Path.GetFullPath(localTracker),
                Path.GetFullPath(hidden),
                Path.GetFullPath(nested),
                Path.GetFullPath(deep),
                Path.GetFullPath(hiddenDir),
                Path.GetFullPath(inside)
            };

            var rows = ReadRows(dbPath);
            var indexed = new HashSet<string>(rows.Select(r => Path.GetFullPath(r.Path)), StringComparer.OrdinalIgnoreCase);
            Assert.True(expected.SetEquals(indexed),
                "missing=[" + string.Join(", ", expected.Except(indexed)) + "] extra=[" + string.Join(", ", indexed.Except(expected)) + "]");

            var plainRow = rows.Single(r => r.Path.EndsWith("plain.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new FileInfo(plain).Length, plainRow.Size);
            Assert.Equal("txt", plainRow.Extension);

            if (systemMarked)
                Assert.Contains("S", rows.Single(r => r.Path.EndsWith("system.txt", StringComparison.OrdinalIgnoreCase)).Attributes);
            if (hiddenMarked)
                Assert.Contains("H", rows.Single(r => r.Path.EndsWith("hidden.txt", StringComparison.OrdinalIgnoreCase)).Attributes);
            if (hiddenDirMarked)
            {
                Assert.Contains("H", rows.Single(r => r.Path.EndsWith("hidden-folder", StringComparison.OrdinalIgnoreCase)).Attributes);
                Assert.Contains(indexed, p => p.EndsWith("inside.txt", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    private static bool TryAddAttribute(string path, FileAttributes extra)
    {
        try
        {
            var combined = File.GetAttributes(path) | extra;
            File.SetAttributes(path, combined);
            return (File.GetAttributes(path) & extra) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<(string Path, long Size, string Extension, string Attributes)> ReadRows(string dbPath)
    {
        var rows = new List<(string, long, string, string)>();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, size, extension, attributes FROM files;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
        return rows;
    }

    private static string ReadLog()
    {
        var path = ErrorLog.LogPath;
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private sealed class ScriptedListing : IEnumerator<CrawlListedEntry>
    {
        private readonly Queue<Action> _steps;
        private readonly CrawlListedEntry _success;

        public ScriptedListing(Queue<Action> steps, CrawlListedEntry success)
        {
            _steps = steps;
            _success = success;
        }

        public CrawlListedEntry Current { get; private set; }

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_steps.Count == 0)
                return false;
            var step = _steps.Dequeue();
            step();
            Current = _success;
            return true;
        }

        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }

    private sealed class ContinueProbe : CrawlListingEnumerator
    {
        public ContinueProbe(string directory, CrawlSkipLog skips) : base(directory, skips) { }

        public bool Call(int error) => ContinueOnError(error);
    }
}
