using System.IO;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Incremental updates via FileSystemWatcher (user-mode). No USN journal.
/// Callbacks only enqueue; a timed flush applies one delete batch + one UpsertBatch.
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly FileIndexer _indexer;
    private readonly AppSettings _settings;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly WatcherCoalesceBuffer _buffer = new();
    private Timer? _flushTimer;
    private int _pruneScheduled;
    private int _paused;
    private int _uiPaused;
    private int _flushBusy;

    // Active window: ~1.5s; minimized / not foreground: ~6s (avoid restore catch-up storm).
    private const int ActiveFlushMs = 1500;
    private const int PausedFlushMs = 6000;
    // FileSystemWatcher max InternalBufferSize is 64 KiB on Windows.
    private const int WatcherBufferSize = 64 * 1024;

    /// <summary>
    /// Raised once after a successful flush that applied at least one path op (or prune).
    /// Subscribers should re-run the active search on the UI thread.
    /// </summary>
    public event Action? IndexMutated;

    public FileWatcherService(IndexDatabase db, FileIndexer indexer, AppSettings settings)
    {
        _db = db;
        _indexer = indexer;
        _settings = settings;
    }

    /// <summary>
    /// When true (window minimized / not active), flush runs on a slower cadence.
    /// UI silent-refresh gating is handled by MainViewModel.
    /// </summary>
    public void SetUiRefreshPaused(bool paused)
    {
        Interlocked.Exchange(ref _uiPaused, paused ? 1 : 0);
        RestartFlushTimer();
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

        RestartFlushTimer();
    }

    public void Stop()
    {
        lock (_flushGate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
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

        // Drop pending ops — rebuild / resume will resync.
        _ = _buffer.Drain();
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
        for (int i = 0; i < 50 && Volatile.Read(ref _flushBusy) != 0; i++)
            Thread.Sleep(20);
    }

    public void ResumeAfterRebuild(IEnumerable<string> roots)
    {
        Interlocked.Exchange(ref _paused, 0);
        Start(roots);
    }

    private bool IsPaused => Volatile.Read(ref _paused) != 0;
    private bool IsUiPaused => Volatile.Read(ref _uiPaused) != 0;

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            if (ShouldSkipLive(e.FullPath)) return;
            _buffer.EnqueueUpsert(e.FullPath);
        });

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            if (ShouldSkipLive(e.FullPath)) return;
            _buffer.EnqueueUpsert(e.FullPath);
        });

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Safe(() =>
        {
            if (ShouldSkipLive(e.FullPath)) return;
            _buffer.EnqueueDelete(e.FullPath);
        });

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        Safe(() =>
        {
            var skipOld = ShouldSkipLive(e.OldFullPath);
            var skipNew = ShouldSkipLive(e.FullPath);
            if (skipOld && skipNew) return;
            if (!skipOld)
                _buffer.EnqueueDelete(e.OldFullPath);
            if (!skipNew)
                _buffer.EnqueueUpsert(e.FullPath);
        });

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow / dropped events — schedule a background prune for that root.
        var root = (sender as FileSystemWatcher)?.Path;
        if (string.IsNullOrEmpty(root))
            root = null;
        SchedulePrune(root);
    }

    private bool ShouldSkipLive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        if (DriveHelpers.IsRemoteRoot(path))
            return true;
        return NoisyPathHeuristics.ShouldIgnoreLive(path, GetExcludePrefixes());
    }

    // Filter excludes can change at runtime; refresh the cached list at most every ~2s.
    private IReadOnlyList<string> _excludeCache = Array.Empty<string>();
    private long _excludeCacheTicks;
    private const long ExcludeCacheTtlMs = 2000;

    private IReadOnlyList<string> GetExcludePrefixes()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _excludeCacheTicks);
        if (last == 0 || now - last > ExcludeCacheTtlMs)
        {
            try
            {
                Volatile.Write(ref _excludeCache, ExcludePaths.ResolveActivePrefixes(_settings));
                Interlocked.Exchange(ref _excludeCacheTicks, now);
            }
            catch
            {
                // keep previous cache
            }
        }
        return Volatile.Read(ref _excludeCache);
    }

    private void RestartFlushTimer()
    {
        lock (_flushGate)
        {
            _flushTimer?.Dispose();
            if (IsPaused)
            {
                _flushTimer = null;
                return;
            }

            var ms = IsUiPaused ? PausedFlushMs : ActiveFlushMs;
            _flushTimer = new Timer(FlushTick, null, ms, ms);
        }
    }

    private void FlushTick(object? _)
    {
        if (IsPaused) return;
        if (Interlocked.CompareExchange(ref _flushBusy, 1, 0) != 0)
            return;

        try
        {
            var (deletes, upserts) = _buffer.Drain();
            if (deletes.Count == 0 && upserts.Count == 0)
                return;

            var any = false;

            foreach (var path in deletes)
            {
                if (IsPaused) return;
                try
                {
                    _db.DeleteByPath(path);
                    _db.DeleteUnderDirectory(path);
                    any = true;
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent))
                        SchedulePrune(parent);
                }
                catch
                {
                    // never crash from flush
                }
            }

            if (upserts.Count > 0 && !IsPaused)
            {
                try
                {
                    _indexer.IndexPathsBatch(upserts);
                    any = true;
                }
                catch
                {
                    // never crash from flush
                }
            }

            if (any && !IsPaused)
                FireIndexMutated();
        }
        finally
        {
            Interlocked.Exchange(ref _flushBusy, 0);
        }
    }

    private void FireIndexMutated()
    {
        try { IndexMutated?.Invoke(); }
        catch { /* never crash from mutation notify */ }
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
                FireIndexMutated();
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
