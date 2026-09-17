using System.Collections.Concurrent;
using System.IO;
using System.Security;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// User-mode parallel indexer using Directory.EnumerateFileSystemEntries.
/// Skips inaccessible directories. No drivers, no MFT/USN.
/// </summary>
public sealed class FileIndexer
{
    private readonly IndexDatabase _db;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public FileIndexer(IndexDatabase db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    public bool IsRunning { get; private set; }

    public async Task RunFullIndexAsync(IProgress<IndexProgress>? progress, CancellationToken externalToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ct = _cts.Token;
        IsRunning = true;

        try
        {
            await Task.Run(() => IndexCore(progress, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new IndexProgress { IsComplete = true, Error = "Indexing cancelled." });
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private void IndexCore(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        _db.ClearAll();

        long filesIndexed = 0;
        long dirsScanned = 0;
        var exclude = new HashSet<string>(_settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var roots = _settings.IndexedRoots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pending = new ConcurrentQueue<string>();
        foreach (var root in roots)
            pending.Enqueue(root);

        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        using var done = new CountdownEvent(1);
        var activeWorkers = 0;

        void Worker()
        {
            var localBatch = new List<FileEntry>(256);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!pending.TryDequeue(out var dir))
                    {
                        // Exit when no work remains and no worker is producing more
                        if (Volatile.Read(ref activeWorkers) == 0 && pending.IsEmpty)
                            break;
                        Thread.Sleep(10);
                        continue;
                    }

                    Interlocked.Increment(ref activeWorkers);
                    try
                    {
                        Interlocked.Increment(ref dirsScanned);
                        EnumerateDirectory(dir, exclude, pending, localBatch, ref filesIndexed, progress, ct);

                        if (localBatch.Count >= 200)
                        {
                            _db.UpsertBatch(localBatch);
                            localBatch.Clear();
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeWorkers);
                    }
                }

                if (localBatch.Count > 0)
                    _db.UpsertBatch(localBatch);
            }
            finally
            {
                done.Signal();
            }
        }

        for (int w = 0; w < workers; w++)
        {
            done.AddCount();
            ThreadPool.QueueUserWorkItem(_ => Worker());
        }

        done.Signal(); // remove initial count
        done.Wait(ct);

        progress?.Report(new IndexProgress
        {
            FilesIndexed = Interlocked.Read(ref filesIndexed),
            DirectoriesScanned = Interlocked.Read(ref dirsScanned),
            IsComplete = true
        });
    }

    private void EnumerateDirectory(
        string dir,
        HashSet<string> exclude,
        ConcurrentQueue<string> pending,
        List<FileEntry> localBatch,
        ref long filesIndexed,
        IProgress<IndexProgress>? progress,
        CancellationToken ct)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }
        catch (SecurityException) { return; }

        foreach (var entryPath in entries)
        {
            if (ct.IsCancellationRequested) return;

            string name;
            try { name = Path.GetFileName(entryPath); }
            catch { continue; }

            bool isDir;
            try
            {
                var attrs = File.GetAttributes(entryPath);
                // Skip directory reparse points to avoid cycles / junction storms
                if ((attrs & FileAttributes.ReparsePoint) != 0 && (attrs & FileAttributes.Directory) != 0)
                    continue;
                isDir = (attrs & FileAttributes.Directory) != 0;
            }
            catch
            {
                continue;
            }

            if (isDir)
            {
                if (exclude.Contains(name))
                    continue;

                pending.Enqueue(entryPath);

                if (_settings.IncludeDirectories)
                {
                    var fe = TryCreateEntry(entryPath, name, isDirectory: true);
                    if (fe is not null)
                    {
                        localBatch.Add(fe);
                        Report(ref filesIndexed, dir, progress);
                    }
                }
            }
            else
            {
                var fe = TryCreateEntry(entryPath, name, isDirectory: false);
                if (fe is not null)
                {
                    localBatch.Add(fe);
                    Report(ref filesIndexed, dir, progress);
                }
            }
        }
    }

    private static void Report(ref long filesIndexed, string dir, IProgress<IndexProgress>? progress)
    {
        var count = Interlocked.Increment(ref filesIndexed);
        if (count % 500 == 0)
        {
            progress?.Report(new IndexProgress
            {
                CurrentPath = dir,
                FilesIndexed = count,
                IsComplete = false
            });
        }
    }

    internal static FileEntry? TryCreateEntry(string fullPath, string name, bool isDirectory)
    {
        try
        {
            DateTime modified;
            long size = 0;
            if (isDirectory)
            {
                modified = Directory.GetLastWriteTimeUtc(fullPath);
            }
            else
            {
                var info = new FileInfo(fullPath);
                size = info.Exists ? info.Length : 0;
                modified = info.LastWriteTimeUtc;
            }

            var ext = isDirectory ? "" : Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var directory = Path.GetDirectoryName(fullPath) ?? fullPath;

            return new FileEntry
            {
                Name = name,
                Path = fullPath,
                Directory = directory,
                Extension = ext,
                Size = size,
                ModifiedUtc = modified,
                IsDirectory = isDirectory
            };
        }
        catch
        {
            return null;
        }
    }

    public void IndexSinglePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrEmpty(name)) name = path;
                var fe = TryCreateEntry(path, name, isDirectory: true);
                if (fe is not null) _db.UpsertBatch(new[] { fe });
            }
            else if (File.Exists(path))
            {
                var name = Path.GetFileName(path);
                var fe = TryCreateEntry(path, name, isDirectory: false);
                if (fe is not null) _db.UpsertBatch(new[] { fe });
            }
        }
        catch
        {
            // ignore inaccessible
        }
    }
}
