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

        Results = new ObservableCollection<FileEntry>();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };

        OpenCommand = new RelayCommand(_ => OpenSelected(), _ => SelectedItem is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenContainingFolder(), _ => SelectedItem is not null);
        RebuildIndexCommand = new RelayCommand(async _ => await RebuildIndexAsync(), _ => !IsIndexing);
        CancelIndexCommand = new RelayCommand(_ => _indexer.Cancel(), _ => IsIndexing);

        var count = _db.Count();
        if (count > 0)
        {
            StatusText = $"Ready — {count:N0} items indexed";
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

    public FileEntry? SelectedItem
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RebuildIndexCommand { get; }
    public ICommand CancelIndexCommand { get; }

    public string IndexedRootsDisplay =>
        _settings.IndexedRoots.Count == 0
            ? "(none)"
            : string.Join(", ", _settings.IndexedRoots);

    private async Task RunSearchAsync()
    {
        var querySnapshot = Query;
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
            hits = await Task.Run(() => _search.Search(querySnapshot)).ConfigureAwait(false);
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

    public void Dispose()
    {
        Interlocked.Increment(ref _searchGeneration);
        _debounce.Stop();
        _watcher.Dispose();
        _indexer.Cancel();
        _db.Dispose();
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
