using System.IO;

namespace InstantFind.Services;

/// <summary>
/// Incremental updates via FileSystemWatcher (user-mode). No USN journal.
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly FileIndexer _indexer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly object _debounceGate = new();
    private Timer? _debounceTimer;
    private int _pruneScheduled;
    private int _paused;
    private const int DebounceMs = 350;
    // FileSystemWatcher max InternalBufferSize is 64 KiB on Windows.
    private const int WatcherBufferSize = 64 * 1024;

    /// <summary>
    /// Raised (debounced) after a successful create/change/delete/rename index update.
    /// Subscribers should re-run the active search on the UI thread.
    /// </summary>
    public event Action? IndexMutated;

    public FileWatcherService(IndexDatabase db, FileIndexer indexer)
    {
        _db = db;
        _indexer = indexer;
    }

    public void Start(IEnumerable<string> roots)
    {
        Stop();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                    InternalBufferSize = WatcherBufferSize
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                lock (_gate) { _watchers.Add(watcher); }
            }
            catch
            {
                // skip roots we cannot watch
            }
        }
    }

    public void Stop()
    {
        lock (_debounceGate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        lock (_gate)
        {
            foreach (var w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }
    }

    /// <summary>
    /// Stop watchers and wait briefly for in-flight prune so the live DB is not busy during swap.
    /// </summary>
    public void PauseForRebuild()
    {
        Interlocked.Exchange(ref _paused, 1);
        Stop();
        for (int i = 0; i < 50 && Volatile.Read(ref _pruneScheduled) != 0; i++)
            Thread.Sleep(20);
    }

    public void ResumeAfterRebuild(IEnumerable<string> roots)
    {
        Interlocked.Exchange(ref _paused, 0);
        Start(roots);
    }

    private bool IsPaused => Volatile.Read(ref _paused) != 0;

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            _indexer.IndexSinglePath(e.FullPath);
            ScheduleIndexMutated();
        });

    // Size/date writes: re-upsert via IndexSinglePath then notify UI to refresh.
    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            _indexer.IndexSinglePath(e.FullPath);
            ScheduleIndexMutated();
        });

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        Safe(() =>
        {
            _db.DeleteByPath(e.FullPath);
            // Folder delete: drop children by path AND by directory column
            _db.DeleteUnderDirectory(e.FullPath);
            ScheduleIndexMutated();
            // Light prune under parent in case watcher only fired for the top folder
            var parent = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(parent))
                SchedulePrune(parent);
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Safe(() =>
        {
            _db.DeleteByPath(e.OldFullPath);
            _db.DeleteUnderDirectory(e.OldFullPath);
            _indexer.IndexSinglePath(e.FullPath);
            ScheduleIndexMutated();
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow / dropped events — schedule a background prune for that root.
        var root = (sender as FileSystemWatcher)?.Path;
        if (string.IsNullOrEmpty(root))
            root = null;
        SchedulePrune(root);
    }

    /// <summary>
    /// Coalesce bursty watcher events (~350ms) so the UI refreshes within ~1s.
    /// </summary>
    private void ScheduleIndexMutated()
    {
        lock (_debounceGate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ =>
                {
                    try { IndexMutated?.Invoke(); }
                    catch { /* never crash from mutation notify */ }
                },
                null,
                DebounceMs,
                Timeout.Infinite);
        }
    }

    /// <summary>
    /// Background prune for a root (or entire index if null). Debounced / single-flight.
    /// </summary>
    public void SchedulePrune(string? prefix)
    {
        if (IsPaused) return;
        // Only one prune at a time
        if (Interlocked.CompareExchange(ref _pruneScheduled, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Small delay to coalesce overflow bursts
                Thread.Sleep(400);
                if (IsPaused) return;
                _db.PruneMissing(prefix);
                if (IsPaused) return;
                ScheduleIndexMutated();
            }
            catch
            {
                // never crash from prune
            }
            finally
            {
                Interlocked.Exchange(ref _pruneScheduled, 0);
            }
        });
    }

    private void Safe(Action action)
    {
        if (IsPaused) return;
        try { action(); }
        catch { /* never crash UI from watcher callbacks */ }
    }

    public void Dispose() => Stop();
}
