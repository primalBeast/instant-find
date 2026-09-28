namespace InstantFind.Services;

/// <summary>
/// Thread-safe coalesce buffer for FileSystemWatcher path ops (case-insensitive by path).
/// <list type="bullet">
/// <item>Repeated Created/Changed on a path collapse to one upsert.</item>
/// <item>Upsert → Delete collapses to a single delete (last wins).</item>
/// <item>Delete → Upsert keeps both: the delete is applied first, then the upsert, so a
/// recreated folder does not keep phantom child rows from before the delete.</item>
/// </list>
/// Drain returns deletes first, then upserts; the flush applies them in that order.
/// </summary>
public sealed class WatcherCoalesceBuffer
{
    private sealed class PathState
    {
        public bool Delete;
        public bool Upsert;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PathState> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get { lock (_gate) return _byPath.Count; }
    }

    public void EnqueueUpsert(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out var st))
                _byPath[path] = st = new PathState();
            st.Upsert = true;
        }
    }

    public void EnqueueDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out var st))
                _byPath[path] = st = new PathState();
            // Last wins: a later delete cancels any earlier pending upsert.
            st.Upsert = false;
            st.Delete = true;
        }
    }

    public void EnqueueRename(string oldPath, string newPath)
    {
        if (!string.IsNullOrWhiteSpace(oldPath))
            EnqueueDelete(oldPath);
        if (!string.IsNullOrWhiteSpace(newPath))
            EnqueueUpsert(newPath);
    }

    /// <summary>
    /// Atomically drain pending ops. Apply <c>Deletes</c> before <c>Upserts</c>.
    /// </summary>
    public (List<string> Deletes, List<string> Upserts) Drain()
    {
        lock (_gate)
        {
            var deletes = new List<string>();
            var upserts = new List<string>();
            foreach (var kv in _byPath)
            {
                if (kv.Value.Delete)
                    deletes.Add(kv.Key);
                if (kv.Value.Upsert)
                    upserts.Add(kv.Key);
            }
            _byPath.Clear();
            return (deletes, upserts);
        }
    }
}
