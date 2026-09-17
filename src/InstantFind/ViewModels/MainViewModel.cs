using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using InstantFind.Models;
using InstantFind.Services;

namespace InstantFind.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly IndexDatabase _db;
    private readonly FileIndexer _indexer;
    private readonly FileWatcherService _watcher;
    private readonly SearchService _search;
    private readonly DispatcherTimer _debounce;
    private readonly Dispatcher _dispatcher;
    private string _query = string.Empty;
    private string _statusText = "Ready";
    private bool _isIndexing;
    private bool _isSearching;
    private FileEntry? _selected;
    private int _searchGeneration;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private bool _isAndChecked = true;
    private bool _isOrChecked;
    private bool _syncingMode;
    private bool _isContextMenuOpen;
    private bool _silentRefreshPending;
    private FileEntry? _contextTarget;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
        // Crash mid-rebuild left index-rebuild.db — discard and keep previous live index
        IndexDatabase.DiscardStaleRebuildArtifacts(_settingsService.DatabasePath);
        _db = new IndexDatabase(_settingsService.DatabasePath);
        _db.Open();
        _indexer = new FileIndexer(_db, _settings);
        _watcher = new FileWatcherService(_db, _indexer);
        _search = new SearchService(_db, _settings);
        _watcher.IndexMutated += OnIndexMutated;

        Results = new ObservableCollection<FileEntry>();
        DriveChips = new ObservableCollection<DriveChip>();
        InitDriveChips();
        ApplyMatchModeFromSettings();
        _matchCase = _settings.MatchCase;
        _wholeWord = _settings.WholeWord;
        _useRegex = _settings.UseRegex;
        OpenWithFavorites = new ObservableCollection<OpenWithFavorite>(
            (_settings.OpenWithFavorites ?? new List<string>())
                .Select(p => new OpenWithFavorite(p)));

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };

        OpenCommand = new RelayCommand(_ => OpenSelected(), _ => ActionTarget is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenContainingFolder(), _ => ActionTarget is not null);
        EditWithNotepadPpCommand = new RelayCommand(
            _ => EditWithNotepadPp(),
            _ => ActionTarget is not null && !ActionTarget.IsDirectory);
        OpenInCmdCommand = new RelayCommand(
            _ => OpenInCommandPrompt(),
            _ => ActionTarget is not null);
        OpenInGitBashCommand = new RelayCommand(
            _ => OpenInGitBash(),
            _ => ActionTarget is not null);
        OpenWithFavoriteCommand = new RelayCommand(
            p => OpenWithExe(p as string),
            p => ActionTarget is not null && !ActionTarget.IsDirectory
                 && p is string exe && !string.IsNullOrWhiteSpace(exe));
        ChooseOpenWithAppCommand = new RelayCommand(
            _ => ChooseOpenWithApp(),
            _ => ActionTarget is not null && !ActionTarget.IsDirectory);
        IndexButtonCommand = new RelayCommand(_ => OnIndexButton());
        ClearQueryCommand = new RelayCommand(_ => ClearQuery());

        var count = _db.Count();
        if (count > 0)
        {
            StatusText = $"Ready — {count:N0} items indexed";
            // Watchers stay on IndexedRoots — no new per-drive watchers when chips toggle
            _watcher.Start(_settings.IndexedRoots);
        }
        else if (_settings.StartIndexingOnLaunch)
        {
            _ = RebuildIndexAsync();
        }
        else
        {
            StatusText = "No index yet. Click Rebuild Index.";
        }
    }

    public ObservableCollection<FileEntry> Results { get; }
    public ObservableCollection<DriveChip> DriveChips { get; }
    public ObservableCollection<OpenWithFavorite> OpenWithFavorites { get; }

    public event Action? FocusSearchRequested;

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
            {
                // Show spinner as soon as the query changes / debounce starts
                if (!string.IsNullOrWhiteSpace(value))
                {
                    IsSearching = true;
                    StatusText = "Searching…";
                }
                else
                {
                    // Invalidate in-flight searches
                    Interlocked.Increment(ref _searchGeneration);
                    IsSearching = false;
                    Results.Clear();
                    StatusText = $"Ready — {_db.Count():N0} items indexed";
                }

                _debounce.Stop();
                _debounce.Start();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        set
        {
            if (Set(ref _isIndexing, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexButtonText)));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Header button label: Rebuild Index when idle, Cancel while indexing.</summary>
    public string IndexButtonText => IsIndexing ? "Cancel" : "Rebuild Index";

    public bool IsSearching
    {
        get => _isSearching;
        set => Set(ref _isSearching, value);
    }

    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (Set(ref _matchCase, value))
            {
                _settings.MatchCase = value;
                PersistSettings();
                ScheduleSearch();
            }
        }
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set
        {
            if (Set(ref _wholeWord, value))
            {
                _settings.WholeWord = value;
                PersistSettings();
                ScheduleSearch();
            }
        }
    }

    /// <summary>Treat query as .NET regex against the filename. Persisted; off by default.</summary>
    public bool UseRegex
    {
        get => _useRegex;
        set
        {
            if (Set(ref _useRegex, value))
            {
                _settings.UseRegex = value;
                PersistSettings();
                ScheduleSearch();
            }
        }
    }

    /// <summary>And checkbox — mutual exclusive with Or; both off = LiteralWhitespace.</summary>
    public bool IsAndChecked
    {
        get => _isAndChecked;
        set
        {
            if (_syncingMode) { Set(ref _isAndChecked, value); return; }
            if (value)
            {
                _syncingMode = true;
                Set(ref _isAndChecked, true);
                Set(ref _isOrChecked, false);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOrChecked)));
                _syncingMode = false;
                SetMatchMode(MatchMode.And);
            }
            else
            {
                Set(ref _isAndChecked, false);
                if (!_isOrChecked)
                    SetMatchMode(MatchMode.LiteralWhitespace);
            }
        }
    }

    /// <summary>Or checkbox — mutual exclusive with And; both off = LiteralWhitespace.</summary>
    public bool IsOrChecked
    {
        get => _isOrChecked;
        set
        {
            if (_syncingMode) { Set(ref _isOrChecked, value); return; }
            if (value)
            {
                _syncingMode = true;
                Set(ref _isOrChecked, true);
                Set(ref _isAndChecked, false);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAndChecked)));
                _syncingMode = false;
                SetMatchMode(MatchMode.Or);
            }
            else
            {
                Set(ref _isOrChecked, false);
                if (!_isAndChecked)
                    SetMatchMode(MatchMode.LiteralWhitespace);
            }
        }
    }

    public FileEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Row under the pointer when the context menu opens. Commands prefer this over
    /// SelectedItem so a silent refresh that clears selection cannot grey out the menu.
    /// </summary>
    public FileEntry? ContextTarget
    {
        get => _contextTarget;
        set
        {
            if (Set(ref _contextTarget, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>True while the results context menu is open — silent refresh is deferred.</summary>
    public bool IsContextMenuOpen
    {
        get => _isContextMenuOpen;
        set
        {
            if (!Set(ref _isContextMenuOpen, value)) return;
            if (!value && _silentRefreshPending)
            {
                _silentRefreshPending = false;
                if (!IsIndexing && !string.IsNullOrWhiteSpace(Query))
                    _ = RunSilentRefreshAsync();
            }
        }
    }

    /// <summary>Target for Open / folder / editor / shell commands.</summary>
    private FileEntry? ActionTarget => ContextTarget ?? SelectedItem;

    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand EditWithNotepadPpCommand { get; }
    public ICommand OpenInCmdCommand { get; }
    public ICommand OpenInGitBashCommand { get; }
    public ICommand OpenWithFavoriteCommand { get; }
    public ICommand ChooseOpenWithAppCommand { get; }
    public ICommand IndexButtonCommand { get; }
    public ICommand ClearQueryCommand { get; }

    public string IndexedRootsDisplay =>
        _settings.IndexedRoots.Count == 0
            ? "(none)"
            : string.Join(", ", _settings.IndexedRoots);

    private void ApplyMatchModeFromSettings()
    {
        _syncingMode = true;
        switch (_settings.MatchMode)
        {
            case MatchMode.Or:
                _isAndChecked = false;
                _isOrChecked = true;
                break;
            case MatchMode.LiteralWhitespace:
                _isAndChecked = false;
                _isOrChecked = false;
                break;
            default:
                _isAndChecked = true;
                _isOrChecked = false;
                _settings.MatchMode = MatchMode.And;
                break;
        }
        _syncingMode = false;
    }

    private void SetMatchMode(MatchMode mode)
    {
        if (_settings.MatchMode == mode)
        {
            ScheduleSearch();
            return;
        }
        _settings.MatchMode = mode;
        PersistSettings();
        ScheduleSearch();
    }

    private void InitDriveChips()
    {
        var letters = DriveHelpers.GetDriveLetters(_settings.IndexedRoots);
        if (letters.Count == 0 && _settings.EnabledDrives is { Count: > 0 })
            letters = DriveHelpers.NormalizeDriveLetters(_settings.EnabledDrives);

        var enabledSet = new HashSet<string>(
            DriveHelpers.NormalizeDriveLetters(_settings.EnabledDrives ?? new List<string>()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var letter in letters)
        {
            var chip = new DriveChip(letter, enabledSet.Contains(letter));
            chip.PropertyChanged += DriveChip_PropertyChanged;
            DriveChips.Add(chip);
        }

        SyncEnabledDrivesFromChips();
    }

    private void DriveChip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DriveChip.IsEnabled)) return;
        SyncEnabledDrivesFromChips();
        PersistSettings();
        // Instant filter — no watcher changes
        ScheduleSearch();
    }

    private void SyncEnabledDrivesFromChips()
    {
        _settings.EnabledDrives = DriveChips
            .Where(c => c.IsEnabled)
            .Select(c => c.Letter)
            .ToList();
    }

    private void OnIndexMutated()
    {
        // Ignore while full rebuild is running — avoids thrashing mid-index.
        if (IsIndexing) return;
        if (string.IsNullOrWhiteSpace(Query)) return;

        // Silent path: never ScheduleSearch (that sets spinner / "Searching…").
        // Watcher already debounce-coalesces (~350ms); re-query off UI thread.
        // Defer while context menu is open so CanExecute / selection stay stable.
        _dispatcher.BeginInvoke(() =>
        {
            if (IsIndexing) return;
            if (string.IsNullOrWhiteSpace(Query)) return;
            if (IsContextMenuOpen)
            {
                _silentRefreshPending = true;
                return;
            }
            _ = RunSilentRefreshAsync();
        });
    }

    private void ScheduleSearch()
    {
        if (!string.IsNullOrWhiteSpace(Query))
        {
            IsSearching = true;
            StatusText = "Searching…";
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void PersistSettings() => _settingsService.Save(_settings);

    public void ClearQuery()
    {
        Query = string.Empty;
        FocusSearchRequested?.Invoke();
    }

    private SearchOptions BuildSearchOptions()
    {
        return new SearchOptions
        {
            MatchMode = _settings.MatchMode,
            MatchCase = MatchCase,
            WholeWord = WholeWord,
            UseRegex = UseRegex,
            EnabledDrivePrefixes = DriveHelpers.ToPathPrefixes(_settings.EnabledDrives ?? new List<string>())
        };
    }

    /// <summary>
    /// User-initiated search: shows spinner and "Searching…" status.
    /// </summary>
    private async Task RunSearchAsync()
    {
        var querySnapshot = Query;
        var options = BuildSearchOptions();
        var generation = Interlocked.Increment(ref _searchGeneration);

        if (string.IsNullOrWhiteSpace(querySnapshot))
        {
            IsSearching = false;
            Results.Clear();
            StatusText = $"Ready — {_db.Count():N0} items indexed";
            return;
        }

        IsSearching = true;
        StatusText = "Searching…";

        IReadOnlyList<FileEntry> hits;
        bool invalidRegex = false;
        try
        {
            hits = await Task.Run(() =>
            {
                var r = _search.Search(querySnapshot, options, out var inv);
                invalidRegex = inv;
                return r;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _searchGeneration) return;
                IsSearching = false;
                StatusText = "Search error: " + ex.Message;
            });
            return;
        }

        await _dispatcher.InvokeAsync(() => ApplySearchResults(hits, generation, invalidRegex));
    }

    /// <summary>
    /// Watcher-driven refresh: re-query off UI thread, swap Results, update final
    /// status only. Never sets IsSearching or StatusText to "Searching…".
    /// </summary>
    private async Task RunSilentRefreshAsync()
    {
        var querySnapshot = Query;
        if (string.IsNullOrWhiteSpace(querySnapshot)) return;

        var options = BuildSearchOptions();
        // Do not bump generation: that would orphan in-flight user searches and leave the spinner on.
        var generation = Volatile.Read(ref _searchGeneration);

        IReadOnlyList<FileEntry> hits;
        bool invalidRegex = false;
        try
        {
            hits = await Task.Run(() =>
            {
                var r = _search.Search(querySnapshot, options, out var inv);
                invalidRegex = inv;
                // Drop search hits that no longer exist (watcher may have missed deletes)
                var missing = _db.PruneMissingPaths(r.Select(x => x.FullPath));
                if (missing.Count > 0)
                {
                    var gone = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
                    r = r.Where(x => !gone.Contains(x.FullPath)).ToList();
                }
                return r;
            }).ConfigureAwait(false);
        }
        catch
        {
            // Keep prior results/status on silent failure — no flicker.
            return;
        }

        await _dispatcher.InvokeAsync(() => ApplySearchResults(hits, generation, invalidRegex));
    }

    private void ApplySearchResults(IReadOnlyList<FileEntry> hits, int generation, bool invalidRegex = false)
    {
        // Ignore stale results if a newer search/refresh superseded this one
        if (generation != _searchGeneration) return;

        // Same ordered paths: keep row instances (selection / context menu).
        // Unchanged only if Size + ModifiedUtc also match; otherwise update in place
        // so SizeDisplay / ModifiedDisplay repaint without Clear().
        if (SameOrderedPaths(Results, hits))
        {
            if (!SameOrderedMetadata(Results, hits))
                UpdateResultsMetadataInPlace(hits);

            IsSearching = false;
            UpdateResultsStatus(invalidRegex);
            return;
        }

        var selectedPath = SelectedItem?.FullPath;
        var contextPath = ContextTarget?.FullPath;

        Results.Clear();
        foreach (var h in hits)
            Results.Add(h);

        // Preserve selection / context target when those paths still exist.
        if (selectedPath is not null)
        {
            SelectedItem = Results.FirstOrDefault(r =>
                string.Equals(r.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        if (contextPath is not null)
        {
            var stillThere = Results.FirstOrDefault(r =>
                string.Equals(r.FullPath, contextPath, StringComparison.OrdinalIgnoreCase));
            if (stillThere is not null)
                ContextTarget = stillThere;
            // else keep the captured ContextTarget so an open menu stays usable
        }

        // User path turned the spinner on; silent path never did. Always clear here.
        IsSearching = false;
        UpdateResultsStatus(invalidRegex);
    }

    private void UpdateResultsStatus(bool invalidRegex = false)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            StatusText = $"Ready — {_db.Count():N0} items indexed";
        }
        else if (invalidRegex)
        {
            StatusText = "Invalid regex";
        }
        else if (Results.Count >= _settings.MaxResults)
        {
            StatusText =
                $"Showing {Results.Count:N0} results (limit reached — refine your search)";
        }
        else
        {
            StatusText = $"{Results.Count:N0} result(s)";
        }
    }

    private static bool SameOrderedPaths(IList<FileEntry> current, IReadOnlyList<FileEntry> hits)
    {
        if (current.Count != hits.Count) return false;
        for (var i = 0; i < hits.Count; i++)
        {
            if (!string.Equals(current[i].FullPath, hits[i].FullPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool SameOrderedMetadata(IList<FileEntry> current, IReadOnlyList<FileEntry> hits)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            if (current[i].Size != hits[i].Size) return false;
            if (current[i].ModifiedUtc != hits[i].ModifiedUtc) return false;
        }
        return true;
    }

    private void UpdateResultsMetadataInPlace(IReadOnlyList<FileEntry> hits)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            var row = Results[i];
            var h = hits[i];
            row.Id = h.Id;
            row.Size = h.Size;
            row.ModifiedUtc = h.ModifiedUtc;
        }
    }

    private void OnIndexButton()
    {
        if (IsIndexing)
        {
            _indexer.Cancel();
            return;
        }

        var confirm = MessageBox.Show(
            "Rebuild the entire index? This can take a while.",
            "Instant Find",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        _ = RebuildIndexAsync();
    }

    public async Task RebuildIndexAsync()
    {
        if (IsIndexing) return;
        IsIndexing = true;
        StatusText = "Preparing rebuild…";
        _watcher.Stop();

        var progress = new Progress<IndexProgress>(p =>
        {
            StatusText = p.StatusText;
            if (p.IsComplete)
            {
                IsIndexing = false;
                if (p.Error is not null)
                    StatusText = p.Error;
                else
                    StatusText = $"Indexed {p.FilesIndexed:N0} items";

                // Always restart watchers — including cancel (previous index kept)
                _watcher.Start(_settings.IndexedRoots);

                if (p.Error is null)
                    _ = RunSearchAsync();
                else if (!string.IsNullOrWhiteSpace(Query))
                    _ = RunSilentRefreshAsync();
            }
        });

        try
        {
            await _indexer.RunFullIndexAsync(progress);
        }
        catch (Exception ex)
        {
            IsIndexing = false;
            StatusText = "Index failed: " + ex.Message;
            _watcher.Start(_settings.IndexedRoots);
        }
    }

    public void OpenSelected()
    {
        var target = ActionTarget;
        if (target is null) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = target.FullPath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open: " + ex.Message, "Instant Find", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void OpenContainingFolder()
    {
        var target = ActionTarget;
        if (target is null) return;
        try
        {
            if (target.IsDirectory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.FullPath,
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target.FullPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open folder: " + ex.Message, "Instant Find", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void EditWithNotepadPp()
    {
        var target = ActionTarget;
        if (target is null || target.IsDirectory) return;

        var npp = FindNotepadPp();
        if (npp is null)
        {
            MessageBox.Show(
                "Notepad++ was not found. Install it or add notepad++.exe to PATH.",
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = npp,
                Arguments = $"\"{target.FullPath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start Notepad++: " + ex.Message,
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }


    private string? GetSelectedFolderPath()
    {
        var target = ActionTarget;
        if (target is null) return null;
        return target.IsDirectory
            ? target.FullPath
            : target.Directory;
    }

    public void OpenInCommandPrompt()
    {
        var folder = GetSelectedFolderPath();
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k cd /d \"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not open Command Prompt: " + ex.Message,
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void OpenInGitBash()
    {
        var folder = GetSelectedFolderPath();
        if (string.IsNullOrWhiteSpace(folder)) return;

        var bash = FindGitBash();
        if (bash is null)
        {
            MessageBox.Show(
                "Git Bash was not found. Install Git for Windows or add bash.exe to PATH.",
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = bash,
                Arguments = $"--cd=\"{folder}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start Git Bash: " + ex.Message,
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void OpenWithExe(string? exePath)
    {
        var target = ActionTarget;
        if (target is null || target.IsDirectory) return;
        if (string.IsNullOrWhiteSpace(exePath)) return;

        if (!File.Exists(exePath))
        {
            MessageBox.Show(
                "Application not found:\n" + exePath,
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "\"" + target.FullPath + "\"",
                UseShellExecute = false
            });
            RememberOpenWithFavorite(exePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not open with that app: " + ex.Message,
                "Instant Find",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void ChooseOpenWithApp()
    {
        var target = ActionTarget;
        if (target is null || target.IsDirectory) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an application",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            DefaultExt = ".exe",
            CheckFileExists = true
        };

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf) && Directory.Exists(pf))
            dlg.InitialDirectory = pf;

        if (dlg.ShowDialog() != true)
            return;

        OpenWithExe(dlg.FileName);
    }

    private void RememberOpenWithFavorite(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        exePath = exePath.Trim();

        // Move to front / insert
        for (var i = OpenWithFavorites.Count - 1; i >= 0; i--)
        {
            if (string.Equals(OpenWithFavorites[i].ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                OpenWithFavorites.RemoveAt(i);
        }
        OpenWithFavorites.Insert(0, new OpenWithFavorite(exePath));
        while (OpenWithFavorites.Count > 8)
            OpenWithFavorites.RemoveAt(OpenWithFavorites.Count - 1);

        SyncOpenWithFavoritesToSettings();
        PersistSettings();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SyncOpenWithFavoritesToSettings()
    {
        _settings.OpenWithFavorites = OpenWithFavorites.Select(f => f.ExePath).ToList();
    }

    private static string? FindGitBash()
    {
        var candidates = new List<string>();

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(pf))
            candidates.Add(Path.Combine(pf, "Git", "bin", "bash.exe"));
        if (!string.IsNullOrEmpty(pf86))
            candidates.Add(Path.Combine(pf86, "Git", "bin", "bash.exe"));
        if (!string.IsNullOrEmpty(local))
            candidates.Add(Path.Combine(local, "Programs", "Git", "bin", "bash.exe"));

        candidates.Add(@"C:\Program Files\Git\bin\bash.exe");
        candidates.Add(@"C:\Program Files (x86)\Git\bin\bash.exe");

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
                return c;
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), "bash.exe");
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static string? FindNotepadPp()
    {
        var candidates = new List<string>();

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(pf))
            candidates.Add(Path.Combine(pf, "Notepad++", "notepad++.exe"));
        if (!string.IsNullOrEmpty(pf86))
            candidates.Add(Path.Combine(pf86, "Notepad++", "notepad++.exe"));
        if (!string.IsNullOrEmpty(local))
            candidates.Add(Path.Combine(local, "Programs", "Notepad++", "notepad++.exe"));

        // Common alternate install roots
        candidates.Add(@"C:\Program Files\Notepad++\notepad++.exe");
        candidates.Add(@"C:\Program Files (x86)\Notepad++\notepad++.exe");

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
                return c;
        }

        // PATH lookup
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), "notepad++.exe");
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _searchGeneration);
        _debounce.Stop();
        _watcher.IndexMutated -= OnIndexMutated;
        _watcher.Dispose();
        _indexer.Cancel();
        _db.Dispose();
        SyncEnabledDrivesFromChips();
        _settings.MatchCase = MatchCase;
        _settings.WholeWord = WholeWord;
        _settings.UseRegex = UseRegex;
        SyncOpenWithFavoritesToSettings();
        _settingsService.Save(_settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
