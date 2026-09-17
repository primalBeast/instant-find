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
    private const int DebounceMs = 350;

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
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, _) => { /* buffer overflow — next full reindex recovers */ };
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

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            _indexer.IndexSinglePath(e.FullPath);
            ScheduleIndexMutated();
        });

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
            // If a directory was removed, also drop children
            _db.DeleteUnderDirectory(e.FullPath);
            ScheduleIndexMutated();
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

    private static void Safe(Action action)
    {
        try { action(); }
        catch { /* never crash UI from watcher callbacks */ }
    }

    public void Dispose() => Stop();
}
