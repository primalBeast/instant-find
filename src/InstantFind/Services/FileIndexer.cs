using System.Collections.Concurrent;
using System.IO;
using System.Security;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// User-mode parallel indexer using Directory.EnumerateFileSystemEntries.
/// Skips inaccessible / locked files and directories. No drivers, no MFT/USN.
/// Full rebuilds write to a temporary DB and atomically swap on success so
/// cancel/crash never leaves a tiny partial live index.
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
        try { _cts?.Dispose(); } catch { /* prior CTS may already be disposed */ }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ct = _cts.Token;
        IsRunning = true;

        try
        {
            await Task.Run(() => IndexCore(progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new IndexProgress
            {
                IsComplete = true,
                Error = "Indexing cancelled — previous index kept."
            });
        }
        catch (Exception ex)
        {
            progress?.Report(new IndexProgress
            {
                IsComplete = true,
                Error = "Index failed: " + ex.Message
            });
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* ignore */ }
    }

    private void IndexCore(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new IndexProgress { Message = "Preparing rebuild…" });

        var livePath = _db.DatabasePath;
        var rebuildPath = IndexDatabase.GetRebuildPath(livePath);

        // Fresh temp DB for this rebuild (discard any leftover)
        IndexDatabase.DiscardStaleRebuildArtifacts(livePath);
        IndexDatabase? rebuildDb = null;
        var swapSucceeded = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            rebuildDb = new IndexDatabase(rebuildPath);
            rebuildDb.Open();

            progress?.Report(new IndexProgress
            {
                Message = "Scanning… 0 items",
                CurrentPath = string.Empty,
                FilesIndexed = 0
            });

            long filesIndexed = 0;
            long dirsScanned = 0;
            long skippedLocked = 0;
            var exclude = new HashSet<string>(_settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
            var excludePrefixes = ExcludePaths.ResolveActivePrefixes(_settings);
            var roots = _settings.IndexedRoots
                .Where(Directory.Exists)
                .Where(r => !DriveHelpers.IsRemoteRoot(r))
                .Where(r => DriveHelpers.IsRootEnabled(r, _settings.EnabledDrives ?? new List<string>()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pending = new ConcurrentQueue<string>();
            foreach (var root in roots)
                pending.Enqueue(root);

            var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
            using var done = new CountdownEvent(1);
            var activeWorkers = 0;
            Exception? workerFault = null;

            void Worker()
            {
                var localBatch = new List<FileEntry>(256);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (!pending.TryDequeue(out var dir))
                        {
                            if (Volatile.Read(ref activeWorkers) == 0 && pending.IsEmpty)
                                break;
                            Thread.Sleep(5);
                            continue;
                        }

                        Interlocked.Increment(ref activeWorkers);
                        try
                        {
                            Interlocked.Increment(ref dirsScanned);
                            EnumerateDirectory(dir, exclude, excludePrefixes, pending, localBatch,
                                ref filesIndexed, ref skippedLocked, progress, ct, rebuildDb);

                            if (localBatch.Count >= 200)
                            {
                                SoftUpsertBatch(rebuildDb, localBatch, ref skippedLocked, ct);
                                localBatch.Clear();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            localBatch.Clear();
                            break;
                        }
                        catch (Exception ex) when (IsSkippableContentException(ex))
                        {
                            // Locked / inaccessible content must not fail the whole rebuild
                            Interlocked.Increment(ref skippedLocked);
                            localBatch.Clear();
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref workerFault, ex, null);
                            break;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeWorkers);
                        }
                    }

                    if (localBatch.Count > 0 && !ct.IsCancellationRequested)
                    {
                        try { SoftUpsertBatch(rebuildDb, localBatch, ref skippedLocked, ct); }
                        catch (OperationCanceledException) { /* discard partial batch */ }
                    }
                }
                finally
                {
                    try { done.Signal(); }
                    catch (ObjectDisposedException) { }
                }
            }

            for (int w = 0; w < workers; w++)
            {
                done.AddCount();
                ThreadPool.QueueUserWorkItem(_ => Worker());
            }

            done.Signal(); // remove initial count

            // Cooperative wait: on cancel, wait briefly for workers without crashing
            try
            {
                done.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                done.Wait(TimeSpan.FromSeconds(15));
                throw;
            }

            if (workerFault is not null)
                throw workerFault;

            ct.ThrowIfCancellationRequested();

            // Flush complete — atomically swap rebuild → live
            progress?.Report(new IndexProgress
            {
                Message = "Finalizing index…",
                FilesIndexed = Interlocked.Read(ref filesIndexed),
                SkippedLocked = Interlocked.Read(ref skippedLocked)
            });

            var finalCount = Interlocked.Read(ref filesIndexed);
            var finalSkipped = Interlocked.Read(ref skippedLocked);
            rebuildDb.Close();
            rebuildDb.Dispose();
            rebuildDb = null;

            _db.Close();
            try
            {
                _db.ReplaceWithRebuildFile(rebuildPath);
                swapSucceeded = true;
            }
            finally
            {
                // Always reopen live DB so search/watchers keep working
                try { _db.Open(); }
                catch
                {
                    // Last resort: recreate empty schema
                    try { _db.Open(); } catch { }
                }
            }

            progress?.Report(new IndexProgress
            {
                FilesIndexed = finalCount,
                DirectoriesScanned = Interlocked.Read(ref dirsScanned),
                SkippedLocked = finalSkipped,
                IsComplete = true
            });
        }
        catch (OperationCanceledException)
        {
            CleanupFailedRebuild(rebuildDb, livePath, swapSucceeded);
            throw;
        }
        catch
        {
            CleanupFailedRebuild(rebuildDb, livePath, swapSucceeded);
            // Ensure live DB is open for continued use
            try
            {
                if (!swapSucceeded)
                    _db.Open();
            }
            catch { }
            throw;
        }
    }

    private static void CleanupFailedRebuild(IndexDatabase? rebuildDb, string livePath, bool swapSucceeded)
    {
        if (swapSucceeded) return;
        try { rebuildDb?.Close(); } catch { }
        try { rebuildDb?.Dispose(); } catch { }
        IndexDatabase.DiscardStaleRebuildArtifacts(livePath);
    }

    /// <summary>
    /// Sharing-violation / access-denied / missing content — skip and continue crawl.
    /// </summary>
    public static bool IsSkippableContentException(Exception ex) =>
        ex is UnauthorizedAccessException
            or SecurityException
            or IOException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException;

    private static void SoftUpsertBatch(
        IndexDatabase targetDb,
        List<FileEntry> batch,
        ref long skippedLocked,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;
        try
        {
            targetDb.UpsertBatch(batch, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsSkippableContentException(ex))
        {
            // Rare: IO during batch write of content metadata path — skip entries
            Interlocked.Add(ref skippedLocked, batch.Count);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Soft-fail individual rows rather than aborting the rebuild
            foreach (var entry in batch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    targetDb.UpsertBatch(new[] { entry }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref skippedLocked);
                }
            }
        }
    }

    private void EnumerateDirectory(
        string dir,
        HashSet<string> exclude,
        IReadOnlyList<string> excludePrefixes,
        ConcurrentQueue<string> pending,
        List<FileEntry> localBatch,
        ref long filesIndexed,
        ref long skippedLocked,
        IProgress<IndexProgress>? progress,
        CancellationToken ct,
        IndexDatabase targetDb)
    {
        if (ExcludePaths.IsUnderAny(dir, excludePrefixes))
            return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref skippedLocked); return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { Interlocked.Increment(ref skippedLocked); return; }
        catch (SecurityException) { Interlocked.Increment(ref skippedLocked); return; }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (IsSkippableContentException(ex))
        {
            Interlocked.Increment(ref skippedLocked);
            return;
        }

        using (enumerator)
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return;

                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex) when (IsSkippableContentException(ex))
                {
                    // Mid-enumeration sharing violation — skip rest of this directory
                    Interlocked.Increment(ref skippedLocked);
                    return;
                }

                if (!moved) break;

                string entryPath;
                try { entryPath = enumerator.Current; }
                catch (Exception ex) when (IsSkippableContentException(ex))
                {
                    Interlocked.Increment(ref skippedLocked);
                    continue;
                }

                string name;
                try { name = Path.GetFileName(entryPath); }
                catch
                {
                    Interlocked.Increment(ref skippedLocked);
                    continue;
                }

                bool isDir;
                try
                {
                    var attrs = File.GetAttributes(entryPath);
                    // Skip directory reparse points to avoid cycles / junction storms
                    if ((attrs & FileAttributes.ReparsePoint) != 0 && (attrs & FileAttributes.Directory) != 0)
                        continue;
                    isDir = (attrs & FileAttributes.Directory) != 0;
                }
                catch (Exception ex) when (IsSkippableContentException(ex))
                {
                    Interlocked.Increment(ref skippedLocked);
                    continue;
                }
                catch
                {
                    Interlocked.Increment(ref skippedLocked);
                    continue;
                }

                if (isDir)
                {
                    if (exclude.Contains(name))
                        continue;
                    if (ExcludePaths.IsUnderAny(entryPath, excludePrefixes))
                        continue;

                    pending.Enqueue(entryPath);

                    if (_settings.IncludeDirectories)
                    {
                        var fe = TryCreateEntry(entryPath, name, isDirectory: true);
                        if (fe is not null)
                        {
                            localBatch.Add(fe);
                            Report(ref filesIndexed, dir, progress, ref skippedLocked);
                        }
                        else
                        {
                            Interlocked.Increment(ref skippedLocked);
                        }
                    }
                }
                else
                {
                    var fe = TryCreateEntry(entryPath, name, isDirectory: false);
                    if (fe is not null)
                    {
                        localBatch.Add(fe);
                        Report(ref filesIndexed, dir, progress, ref skippedLocked);
                    }
                    else
                    {
                        Interlocked.Increment(ref skippedLocked);
                    }
                }

                // Flush mid-directory if batch is large so cancel isn't blocked long
                if (localBatch.Count >= 200)
                {
                    SoftUpsertBatch(targetDb, localBatch, ref skippedLocked, ct);
                    localBatch.Clear();
                }
            }
        }
    }

    private static void Report(ref long filesIndexed, string dir, IProgress<IndexProgress>? progress, ref long skippedLocked)
    {
        var count = Interlocked.Increment(ref filesIndexed);
        if (count == 1 || count % 500 == 0)
        {
            progress?.Report(new IndexProgress
            {
                CurrentPath = dir,
                FilesIndexed = count,
                SkippedLocked = Interlocked.Read(ref skippedLocked),
                IsComplete = false
            });
        }
    }

    public static FileEntry? TryCreateEntry(string fullPath, string name, bool isDirectory)
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
            string attrsText = string.Empty;
            try
            {
                attrsText = QueryParser.FormatAttributes(File.GetAttributes(fullPath));
            }
            catch { }

            return new FileEntry
            {
                Name = name,
                Path = fullPath,
                Directory = directory,
                Extension = ext,
                Size = size,
                ModifiedUtc = modified,
                IsDirectory = isDirectory,
                AttributesText = attrsText
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
            if (DriveHelpers.IsRemoteRoot(path))
                return;

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
            // ignore inaccessible / locked
        }
    }
}
