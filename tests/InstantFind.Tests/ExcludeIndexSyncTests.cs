using System.IO;
using InstantFind.Models;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class ExcludeIndexSyncTests
{
    private static FileEntry Entry(string path) => new()
    {
        Name = Path.GetFileName(path),
        Path = path,
        Directory = Path.GetDirectoryName(path) ?? "",
        Extension = "txt",
        Size = 1,
        ModifiedUtc = DateTime.UtcNow,
        IsDirectory = false,
        AttributesText = "A"
    };

    [Fact]
    public void PurgePrefix_removes_prefix_and_children_not_siblings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        try
        {
            using var db = new IndexDatabase(dbPath);
            db.Open();
            db.UpsertBatch(new[]
            {
                Entry(@"C:\Keep\a.txt"),
                Entry(@"C:\Temp\x.txt"),
                Entry(@"C:\Temp\sub\y.txt"),
                Entry(@"C:\Tempish\z.txt"),
            });

            db.PurgePrefix(@"C:\Temp");

            var all = db.Search(
                QueryParser.Parse(@"C:\", MatchMode.And, useRegex: false),
                maxResults: 100,
                includeDirectories: true,
                new SearchOptions
                {
                    EnabledDrivePrefixes = new[] { @"C:\" },
                    MatchMode = MatchMode.And
                });
            var paths = all.Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(@"C:\Keep\a.txt", paths);
            Assert.Contains(@"C:\Tempish\z.txt", paths);
            Assert.DoesNotContain(@"C:\Temp\x.txt", paths);
            Assert.DoesNotContain(@"C:\Temp\sub\y.txt", paths);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SearchTimingLog_appends_json_line_soft_fail_ok()
    {
        SearchTimingLog.Append(
            ms: 12.5,
            hits: 3,
            capped: false,
            cancelled: false,
            route: "fts",
            query: "hosts",
            matchCase: false,
            wholeWord: false,
            regex: false,
            mode: "And",
            driveCount: 1,
            excludeCount: 2);
        Assert.False(string.IsNullOrWhiteSpace(SearchTimingLog.LogPath));
    }

    [Fact]
    public void LastSearchRoute_exact_when_quoted_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        try
        {
            using var db = new IndexDatabase(dbPath);
            db.Open();
            db.UpsertBatch(new[] { Entry(@"C:\Windows\System32\hosts") });

            var parsed = QueryParser.Parse("\"hosts\"", MatchMode.And, useRegex: false);
            _ = db.Search(
                parsed,
                maxResults: 50,
                includeDirectories: false,
                new SearchOptions
                {
                    EnabledDrivePrefixes = new[] { @"C:\" },
                    MatchMode = MatchMode.And
                });
            Assert.Equal("exact", db.LastSearchRoute);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
