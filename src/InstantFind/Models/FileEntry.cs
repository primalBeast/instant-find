using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace InstantFind.Models;

/// <summary>
/// Indexed file or directory metadata shown in search results.
/// Size and ModifiedUtc come from the SQLite index (set at crawl /
/// FileWatcher IndexSinglePath), not live disk on every search.
/// </summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    private long _size;
    private DateTime _modifiedUtc;

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;

    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    public DateTime ModifiedUtc
    {
        get => _modifiedUtc;
        set
        {
            if (_modifiedUtc == value) return;
            _modifiedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModifiedDisplay));
        }
    }

    public bool IsDirectory { get; set; }

    public string FullPath => Path;

    public string SizeDisplay => IsDirectory ? "<DIR>" : FormatSize(Size);

    public string ModifiedDisplay => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }
}
