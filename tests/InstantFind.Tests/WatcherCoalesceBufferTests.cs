using Xunit;
using InstantFind.Services;

namespace InstantFind.Tests;

public class WatcherCoalesceBufferTests
{
    [Fact]
    public void Repeated_changes_collapse_to_one_upsert_case_insensitive()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueUpsert(@"C:\Data\a.txt");
        b.EnqueueUpsert(@"C:\Data\a.txt");
        b.EnqueueUpsert(@"c:\data\A.TXT");

        var (deletes, upserts) = b.Drain();
        Assert.Empty(deletes);
        Assert.Single(upserts);
    }

    [Fact]
    public void Upsert_then_delete_is_delete_only()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueUpsert(@"C:\Data\a.txt");
        b.EnqueueDelete(@"C:\Data\a.txt");

        var (deletes, upserts) = b.Drain();
        Assert.Single(deletes);
        Assert.Empty(upserts);
    }

    [Fact]
    public void Delete_then_create_keeps_both_so_delete_applies_first()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueDelete(@"C:\Data\Folder");
        b.EnqueueUpsert(@"C:\Data\Folder");

        var (deletes, upserts) = b.Drain();
        Assert.Equal(new[] { @"C:\Data\Folder" }, deletes);
        Assert.Equal(new[] { @"C:\Data\Folder" }, upserts);
    }

    [Fact]
    public void Delete_create_delete_ends_as_delete_only()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueDelete(@"C:\Data\x");
        b.EnqueueUpsert(@"C:\Data\x");
        b.EnqueueDelete(@"C:\Data\x");

        var (deletes, upserts) = b.Drain();
        Assert.Single(deletes);
        Assert.Empty(upserts);
    }

    [Fact]
    public void Rename_enqueues_delete_old_and_upsert_new()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueRename(@"C:\Data\old.txt", @"C:\Data\new.txt");

        var (deletes, upserts) = b.Drain();
        Assert.Equal(new[] { @"C:\Data\old.txt" }, deletes);
        Assert.Equal(new[] { @"C:\Data\new.txt" }, upserts);
    }

    [Fact]
    public void Rename_back_and_forth_keeps_final_state_consistent()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueRename(@"C:\Data\a.txt", @"C:\Data\b.txt");
        b.EnqueueRename(@"C:\Data\b.txt", @"C:\Data\a.txt");

        var (deletes, upserts) = b.Drain();
        // b.txt: upsert then delete -> delete. a.txt: delete then upsert -> both (delete first).
        Assert.Contains(@"C:\Data\b.txt", deletes);
        Assert.Contains(@"C:\Data\a.txt", deletes);
        Assert.Contains(@"C:\Data\a.txt", upserts);
        Assert.DoesNotContain(@"C:\Data\b.txt", upserts);
    }

    [Fact]
    public void Drain_empties_the_buffer()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueUpsert(@"C:\Data\a.txt");
        b.EnqueueDelete(@"C:\Data\b.txt");
        Assert.Equal(2, b.Count);

        _ = b.Drain();
        Assert.Equal(0, b.Count);
        var (d, u) = b.Drain();
        Assert.Empty(d);
        Assert.Empty(u);
    }

    [Fact]
    public void Blank_paths_are_ignored()
    {
        var b = new WatcherCoalesceBuffer();
        b.EnqueueUpsert("");
        b.EnqueueDelete("  ");
        b.EnqueueRename("", "");
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Concurrent_enqueue_is_thread_safe()
    {
        var b = new WatcherCoalesceBuffer();
        Parallel.For(0, 2000, i => b.EnqueueUpsert($@"C:\Data\f{i % 100}.txt"));
        var (_, upserts) = b.Drain();
        Assert.Equal(100, upserts.Count);
    }
}
