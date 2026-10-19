using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// One directory listing entry copied off <see cref="FileSystemEntry"/>.
/// Metadata comes from the find buffer only — never open the file (no OneDrive hydration).
/// </summary>
internal readonly struct CrawlListedEntry
{
    public CrawlListedEntry(
        string name,
        string fullPath,
        FileAttributes attributes,
        long length,
        DateTimeOffset lastWriteTimeUtc)
    {
        Name = name;
        FullPath = fullPath;
        Attributes = attributes;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public string Name { get; }
    public string FullPath { get; }
    public FileAttributes Attributes { get; }
    public long Length { get; }
    public DateTimeOffset LastWriteTimeUtc { get; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public static CrawlListedEntry From(ref FileSystemEntry entry) =>
        new(
            entry.FileName.ToString(),
            entry.ToFullPath(),
            entry.Attributes,
            entry.Length,
            entry.LastWriteTimeUtc);
}

internal enum CrawlConsumeResult
{
    Indexed,
    SkippedIntentional,
    SkippedError
}

/// <summary>
/// Directory listing for the indexer. <see cref="EnumerationOptions.AttributesToSkip"/> is 0 so
/// Hidden / System / Offline / Recall placeholders are returned. Reparse directories are still
/// filtered by <see cref="ReparseCrawlGate"/> after the listing, not by the enumerator.
/// </summary>
internal static class CrawlListing
{
    /// <summary>
    /// Consecutive MoveNext failures (without a successful entry) before this directory is abandoned.
    /// A single bad entry must not stop the rest; a stuck enumerator must not spin forever.
    /// </summary>
    internal const int MaxConsecutiveListingErrors = 64;

    public static EnumerationOptions CreateOptions() => new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false
    };

    public static FileEntry CreateFromListing(CrawlListedEntry entry)
    {
        var isDirectory = entry.IsDirectory;
        var directory = Path.GetDirectoryName(entry.FullPath) ?? string.Empty;
        var ext = isDirectory
            ? ""
            : Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
        var length = entry.Length < 0 ? 0 : entry.Length;

        return new FileEntry
        {
            Name = entry.Name,
            Path = entry.FullPath,
            Directory = directory,
            Extension = ext,
            Size = isDirectory ? 0 : length,
            ModifiedUtc = entry.LastWriteTimeUtc.UtcDateTime,
            IsDirectory = isDirectory,
            AttributesText = QueryParser.FormatAttributes(entry.Attributes)
        };
    }

    /// <summary>Directory row when listing metadata could not be turned into a normal entry.</summary>
    public static FileEntry CreateFallbackDirectory(CrawlListedEntry entry)
    {
        string directory;
        try { directory = Path.GetDirectoryName(entry.FullPath) ?? string.Empty; }
        catch { directory = string.Empty; }

        string attrs;
        try { attrs = QueryParser.FormatAttributes(entry.Attributes); }
        catch { attrs = "D"; }

        return new FileEntry
        {
            Name = entry.Name ?? string.Empty,
            Path = entry.FullPath ?? string.Empty,
            Directory = directory,
            Extension = "",
            Size = 0,
            ModifiedUtc = default,
            IsDirectory = true,
            AttributesText = attrs
        };
    }

    public static CrawlConsumeResult TryConsume(
        CrawlListedEntry entry,
        bool includeDirectories,
        HashSet<string> excludeNames,
        IReadOnlyList<string> excludePrefixes,
        ConcurrentQueue<string> pending,
        List<FileEntry> batch,
        CrawlSkipLog skips,
        Func<CrawlListedEntry, FileEntry>? build = null)
    {
        try
        {
            return ConsumeCore(entry, includeDirectories, excludeNames, excludePrefixes, pending, batch, skips, build);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var isDir = entry.IsDirectory;
            skips.Record(entry.FullPath, isDir ? "directory-entry" : "file-entry", ex);
            if (!isDir)
                return CrawlConsumeResult.SkippedError;

            try { pending.Enqueue(entry.FullPath); }
            catch { /* queue failure still tries to keep a row */ }

            try
            {
                batch.Add(CreateFallbackDirectory(entry));
                return CrawlConsumeResult.Indexed;
            }
            catch (Exception fallbackEx)
            {
                skips.Record(entry.FullPath, "directory-row-fallback", fallbackEx, folder: true);
                return CrawlConsumeResult.SkippedError;
            }
        }
    }

    /// <summary>
    /// Walk <paramref name="enumerator"/>. A failure on one entry is logged and skipped;
    /// siblings after it are still consumed. Directory rows that fail to build are still enqueued.
    /// </summary>
    public static void Consume(
        IEnumerator<CrawlListedEntry> enumerator,
        string directoryPath,
        bool includeDirectories,
        HashSet<string> excludeNames,
        IReadOnlyList<string> excludePrefixes,
        ConcurrentQueue<string> pending,
        List<FileEntry> batch,
        CrawlSkipLog skips,
        ref long skippedLocked,
        CancellationToken ct,
        Action onIndexed,
        Action flush,
        Func<CrawlListedEntry, FileEntry>? build = null)
    {
        var consecutiveErrors = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            CrawlListedEntry entry;
            try
            {
                if (!enumerator.MoveNext())
                    break;
                entry = enumerator.Current;
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Interlocked.Increment(ref skippedLocked);
                skips.Record(directoryPath, "listing-entry", ex);
                if (consecutiveErrors >= MaxConsecutiveListingErrors)
                {
                    skips.Record(directoryPath, "listing-aborted", ex, folder: true);
                    break;
                }
                continue;
            }

            var result = TryConsume(
                entry,
                includeDirectories,
                excludeNames,
                excludePrefixes,
                pending,
                batch,
                skips,
                build);

            if (result == CrawlConsumeResult.Indexed)
                onIndexed();
            else if (result == CrawlConsumeResult.SkippedError)
                Interlocked.Increment(ref skippedLocked);

            if (batch.Count >= 200)
                flush();
        }
    }

    private static CrawlConsumeResult ConsumeCore(
        CrawlListedEntry entry,
        bool includeDirectories,
        HashSet<string> excludeNames,
        IReadOnlyList<string> excludePrefixes,
        ConcurrentQueue<string> pending,
        List<FileEntry> batch,
        CrawlSkipLog skips,
        Func<CrawlListedEntry, FileEntry>? build)
    {
        if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.FullPath))
        {
            skips.Record(
                entry.FullPath,
                "entry-name",
                new IOException("Listing entry had an empty name or path."));
            return CrawlConsumeResult.SkippedError;
        }

        var isDir = entry.IsDirectory;
        if (isDir)
        {
            // Junctions / mount points stay skipped. OneDrive* and Recall flags still enter.
            if (!ReparseCrawlGate.ShouldEnterDirectoryReparsePoint(entry.FullPath, entry.Attributes))
                return CrawlConsumeResult.SkippedIntentional;

            if (excludeNames.Contains(entry.Name))
                return CrawlConsumeResult.SkippedIntentional;
            if (ExcludePaths.IsUnderAny(entry.FullPath, excludePrefixes))
                return CrawlConsumeResult.SkippedIntentional;

            pending.Enqueue(entry.FullPath);

            if (!includeDirectories)
                return CrawlConsumeResult.SkippedIntentional;
        }

        try
        {
            var built = build is null ? CreateFromListing(entry) : build(entry);
            batch.Add(built);
            return CrawlConsumeResult.Indexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            skips.Record(entry.FullPath, isDir ? "directory-row" : "file-row", ex);
            if (!isDir)
                return CrawlConsumeResult.SkippedError;

            batch.Add(CreateFallbackDirectory(entry));
            return CrawlConsumeResult.Indexed;
        }
    }
}

/// <summary>
/// Find-buffer enumerator. <see cref="ContinueOnError"/> records the Win32 error and returns true
/// so a query failure does not throw out of MoveNext (which used to abandon every remaining sibling).
/// The runtime then ends that directory's query; entries already accepted are kept.
/// </summary>
internal class CrawlListingEnumerator : FileSystemEnumerator<CrawlListedEntry>
{
    private readonly string _directory;
    private readonly CrawlSkipLog _skips;

    public CrawlListingEnumerator(string directory, CrawlSkipLog skips)
        : base(directory, CrawlListing.CreateOptions())
    {
        _directory = directory;
        _skips = skips;
    }

    protected override CrawlListedEntry TransformEntry(ref FileSystemEntry entry) =>
        CrawlListedEntry.From(ref entry);

    protected override bool ContinueOnError(int error)
    {
        _skips.Record(_directory, "listing", new Win32Exception(error), win32: error, folder: true);
        return true;
    }
}

/// <summary>
/// CrawlSkip notes in the existing Instant Find error log. Detail lines are capped; a summary follows.
/// </summary>
internal sealed class CrawlSkipLog
{
    public const int DetailCap = 200;

    private readonly object _gate = new();
    private int _detailSlots;
    private int _summarized;
    private long _total;
    private long _folders;

    public long Total => Interlocked.Read(ref _total);
    public long Folders => Interlocked.Read(ref _folders);

    public void Record(
        string? path,
        string kind,
        Exception? ex = null,
        int win32 = 0,
        bool folder = false,
        string? detail = null)
    {
        Interlocked.Increment(ref _total);
        if (folder)
            Interlocked.Increment(ref _folders);

        // Serialize lines so parallel workers don't interleave a single note.
        lock (_gate)
        {
            var slot = ++_detailSlots;
            if (slot <= DetailCap)
                ErrorLog.AppendNote("CrawlSkip", Format(kind, ex, win32, detail), path);
            else if (slot == DetailCap + 1)
            {
                ErrorLog.AppendNote(
                    "CrawlSkip",
                    $"Further crawl skips omitted after {DetailCap} entries. A summary is written at the end of the crawl.",
                    path);
            }
        }
    }

    public void WriteSummary(string? path)
    {
        if (Interlocked.Exchange(ref _summarized, 1) != 0)
            return;

        var total = Total;
        if (total <= 0)
            return;

        ErrorLog.AppendNote(
            "CrawlSkip",
            $"Summary: {total} crawl skip(s), {Folders} folder(s).",
            path,
            skippedLocked: total);
    }

    internal static string Format(string kind, Exception? ex, int win32, string? detail)
    {
        if (win32 == 0 && ex is Win32Exception w32)
            win32 = w32.NativeErrorCode;
        else if (win32 == 0 && ex is not null)
        {
            var hr = ex.HResult;
            if ((hr & 0xFFFF0000) == 0x80070000)
                win32 = hr & 0xFFFF;
        }

        var type = ex?.GetType().FullName ?? "";
        var hrText = ex is null ? "" : $"0x{ex.HResult:X8}";
        var message = ex?.Message ?? "";
        var extra = string.IsNullOrEmpty(detail) ? "" : $" {detail};";
        return $"kind={kind};{extra} win32={win32}; hresult={hrText}; type={type}; message={message}";
    }
}
