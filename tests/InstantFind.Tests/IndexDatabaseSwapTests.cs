using System.IO;
using InstantFind.Models;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class IndexDatabaseSwapTests
{
    private static FileEntry Entry(string path, string name) => new()
    {
        Name = name,
        Path = path,
        Directory = Path.GetDirectoryName(path) ?? "",
        Extension = "txt",
        Size = 1,
        ModifiedUtc = DateTime.UtcNow,
        IsDirectory = false,
        AttributesText = "A"
    };

    [Fact]
    public void Close_nulls_connection_before_swap_and_rebuild_data_wins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var livePath = Path.Combine(dir, "index.db");
        var rebuildPath = Path.Combine(dir, IndexDatabase.RebuildFileName);

        try
        {
            using var live = new IndexDatabase(livePath);
            live.Open();
            live.UpsertBatch(new[] { Entry(Path.Combine(dir, "old.txt"), "old.txt") });
            Assert.Equal(1, live.Count());
            Assert.True(live.IsOpen);

            using (var rebuild = new IndexDatabase(rebuildPath))
            {
                rebuild.Open();
                rebuild.UpsertBatch(new[]
                {
                    Entry(Path.Combine(dir, "new-a.txt"), "new-a.txt"),
                    Entry(Path.Combine(dir, "new-b.txt"), "new-b.txt")
                });
                Assert.Equal(2, rebuild.Count());
                rebuild.Close();
                Assert.False(rebuild.IsOpen);
            }

            // Required ordering: close live → swap (+ WAL/SHM) → reopen
            live.Close();
            Assert.False(live.IsOpen);

            live.ReplaceWithRebuildFile(rebuildPath);
            Assert.False(live.IsOpen);

            live.Open();
            Assert.True(live.IsOpen);
            Assert.Equal(2, live.Count());

            // Sidecars / rebuild leftovers cleaned
            Assert.False(File.Exists(rebuildPath));
            Assert.False(File.Exists(rebuildPath + "-wal"));
            Assert.False(File.Exists(rebuildPath + "-shm"));
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    File.Delete(f);
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReplaceWithRebuildFile_closes_open_live_connection_defensively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-swap2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var livePath = Path.Combine(dir, "index.db");
        var rebuildPath = Path.Combine(dir, IndexDatabase.RebuildFileName);

        try
        {
            using var live = new IndexDatabase(livePath);
            live.Open();
            live.UpsertBatch(new[] { Entry(Path.Combine(dir, "live.txt"), "live.txt") });

            using (var rebuild = new IndexDatabase(rebuildPath))
            {
                rebuild.Open();
                rebuild.UpsertBatch(new[] { Entry(Path.Combine(dir, "rebuilt.txt"), "rebuilt.txt") });
                rebuild.Close();
            }

            Assert.True(live.IsOpen);
            // Defensive Close inside ReplaceWithRebuildFile — must not leave our handle open
            live.ReplaceWithRebuildFile(rebuildPath);
            Assert.False(live.IsOpen);

            live.Open();
            Assert.Equal(1, live.Count());
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    File.Delete(f);
                Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }
}
