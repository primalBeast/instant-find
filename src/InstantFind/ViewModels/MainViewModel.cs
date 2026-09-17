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
    private bool _isAndChecked = true;
    private bool _isOrChecked;
    private bool _syncingMode;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
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

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };

        OpenCommand = new RelayCommand(_ => OpenSelected(), _ => SelectedItem is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenContainingFolder(), _ => SelectedItem is not null);
        EditWithNotepadPpCommand = new RelayCommand(
            _ => EditWithNotepadPp(),
            _ => SelectedItem is not null && !SelectedItem.IsDirectory);
        OpenInCmdCommand = new RelayCommand(
            _ => OpenInCommandPrompt(),
            _ => SelectedItem is not null);
        OpenInGitBashCommand = new RelayCommand(
            _ => OpenInGitBash(),
            _ => SelectedItem is not null);
        RebuildIndexCommand = new RelayCommand(async _ => await RebuildIndexAsync(), _ => !IsIndexing);
        CancelIndexCommand = new RelayCommand(_ => _indexer.Cancel(), _ => IsIndexing);
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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

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

    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand EditWithNotepadPpCommand { get; }
    public ICommand OpenInCmdCommand { get; }
    public ICommand OpenInGitBashCommand { get; }
    public ICommand RebuildIndexCommand { get; }
    public ICommand CancelIndexCommand { get; }
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

        _dispatcher.BeginInvoke(() =>
        {
            if (IsIndexing) return;
            if (string.IsNullOrWhiteSpace(Query)) return;
            ScheduleSearch();
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
            EnabledDrivePrefixes = DriveHelpers.ToPathPrefixes(_settings.EnabledDrives ?? new List<string>())
        };
    }

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
        try
        {
            hits = await Task.Run(() => _search.Search(querySnapshot, options)).ConfigureAwait(false);
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

        await _dispatcher.InvokeAsync(() =>
        {
            // Ignore stale results if the query changed
            if (generation != _searchGeneration) return;

            Results.Clear();
            foreach (var h in hits)
                Results.Add(h);

            IsSearching = false;

            if (string.IsNullOrWhiteSpace(Query))
            {
                StatusText = $"Ready — {_db.Count():N0} items indexed";
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
        });
    }

    public async Task RebuildIndexAsync()
    {
        if (IsIndexing) return;
        IsIndexing = true;
        StatusText = "Starting index…";
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
                {
                    StatusText = $"Indexed {p.FilesIndexed:N0} items";
                    // Watchers remain on IndexedRoots (not per enabled chip)
                    _watcher.Start(_settings.IndexedRoots);
                    _ = RunSearchAsync();
                }
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
        }
    }

    public void OpenSelected()
    {
        if (SelectedItem is null) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SelectedItem.FullPath,
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
        if (SelectedItem is null) return;
        try
        {
            if (SelectedItem.IsDirectory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SelectedItem.FullPath,
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{SelectedItem.FullPath}\"",
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
        if (SelectedItem is null || SelectedItem.IsDirectory) return;

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
                Arguments = $"\"{SelectedItem.FullPath}\"",
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
        if (SelectedItem is null) return null;
        return SelectedItem.IsDirectory
            ? SelectedItem.FullPath
            : SelectedItem.Directory;
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
