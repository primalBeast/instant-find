using System.Collections.Concurrent;
using System.IO;
using System.Security;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// User-mode parallel indexer using Directory.EnumerateFileSystemEntries.
/// Skips inaccessible directories. No drivers, no MFT/USN.
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
            var exclude = new HashSet<string>(_settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
            var roots = _settings.IndexedRoots
                .Where(Directory.Exists)
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
                            EnumerateDirectory(dir, exclude, pending, localBatch, ref filesIndexed, progress, ct, rebuildDb);

                            if (localBatch.Count >= 200)
                            {
                                rebuildDb.UpsertBatch(localBatch, ct);
                                localBatch.Clear();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            localBatch.Clear();
                            break;
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
                        try { rebuildDb.UpsertBatch(localBatch, ct); }
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
                FilesIndexed = Interlocked.Read(ref filesIndexed)
            });

            var finalCount = Interlocked.Read(ref filesIndexed);
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

    private void EnumerateDirectory(
        string dir,
        HashSet<string> exclude,
        ConcurrentQueue<string> pending,
        List<FileEntry> localBatch,
        ref long filesIndexed,
        IProgress<IndexProgress>? progress,
        CancellationToken ct,
        IndexDatabase targetDb)
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

        // Flush mid-directory if batch is large so cancel isn't blocked long
        if (localBatch.Count >= 200)
        {
            targetDb.UpsertBatch(localBatch, ct);
            localBatch.Clear();
        }
    }

    private static void Report(ref long filesIndexed, string dir, IProgress<IndexProgress>? progress)
    {
        var count = Interlocked.Increment(ref filesIndexed);
        if (count == 1 || count % 500 == 0)
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
