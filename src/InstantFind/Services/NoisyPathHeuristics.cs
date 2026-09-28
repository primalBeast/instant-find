using System.IO;

namespace InstantFind.Services;

/// <summary>
/// Live FileSystemWatcher ignore heuristics for high-chatter Temp/cache paths.
/// Rebuild / CrawlIntoExisting still cover these; only incremental live updates skip them.
/// </summary>
public static class NoisyPathHeuristics
{
    private static readonly string[] CacheSegments =
    {
        @"\Cache\",
        @"\Caches\",
        @"\INetCache\",
        @"\Code Cache\",
        @"\GPUCache\",
        @"\ShaderCache\",
        @"\cache2\",      // Firefox profile cache under Local AppData
    };

    private static readonly string[] CacheSegmentNames =
    {
        "Cache",
        "Caches",
        "INetCache",
        "Code Cache",
        "GPUCache",
        "ShaderCache",
        "cache2",
    };

    /// <summary>
    /// True for Temp, LocalAppData\Temp, known cache path segments, and common browser cache trees under Local AppData.
    /// </summary>
    public static bool IsNoisy(string? path)
        => IsNoisy(path, CachedTempRoots.Value, CachedLocalAppData.Value);

    // Temp / LocalAppData don't change at runtime — resolve once, not per watcher event.
    private static readonly Lazy<IReadOnlyList<string>> CachedTempRoots = new(DefaultTempRoots);
    private static readonly Lazy<string?> CachedLocalAppData = new(SafeLocalAppData);

    /// <summary>Testable overload: explicit Temp roots and Local AppData root.</summary>
    public static bool IsNoisy(string? path, IReadOnlyList<string> tempRoots, string? localAppData)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { full = path.Trim(); }

        foreach (var root in tempRoots)
        {
            if (IsUnderPrefix(full, root))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            if (IsUnderPrefix(full, Path.Combine(localAppData, "Temp")))
                return true;
        }

        return HasCacheSegment(full);
    }

    /// <summary>%TEMP%, %TMP%, and %LOCALAPPDATA%\Temp (expanded, de-duplicated).</summary>
    public static IReadOnlyList<string> DefaultTempRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }
        try
        {
            Add(Environment.GetEnvironmentVariable("TEMP"));
            Add(Environment.GetEnvironmentVariable("TMP"));
            var local = SafeLocalAppData();
            if (!string.IsNullOrEmpty(local))
                Add(Path.Combine(local, "Temp"));
        }
        catch
        {
            // ignore env failures
        }
        return list;
    }

    private static string? SafeLocalAppData()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch { return null; }
    }

    /// <summary>
    /// Live-watch gate: true when the path is under an active Filter exclude or a noisy Temp/cache path.
    /// </summary>
    public static bool ShouldIgnoreLive(string? path, IReadOnlyList<string> excludePrefixes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        if (ExcludePaths.IsUnderAny(path, excludePrefixes))
            return true;
        return IsNoisy(path);
    }

    private static bool HasCacheSegment(string fullPath)
    {
        foreach (var seg in CacheSegments)
        {
            if (fullPath.Contains(seg, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Match ...\Cache or ...\GPUCache as a final path segment (no trailing separator).
        foreach (var name in CacheSegmentNames)
        {
            if (fullPath.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsUnderPrefix(string fullPath, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        string root;
        try { root = Path.GetFullPath(prefix.Trim()); }
        catch { root = prefix.Trim(); }

        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        return fullPath.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase);
    }
}
