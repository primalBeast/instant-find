using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using InstantFind.Services;

namespace InstantFind.Models;

/// <summary>
/// Indexed file or directory metadata shown in search results.
/// Size and ModifiedUtc come from the SQLite index (set at crawl /
/// FileWatcher IndexSinglePath), not live disk on every search.
/// Attributes are indexed when available; live File.GetAttributes is a fallback.
/// </summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    private long _size;
    private DateTime _modifiedUtc;
    private string _attributesText = string.Empty;

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

    /// <summary>Compact attribute letters (R/H/S/A/…), from index or live fallback.</summary>
    public string AttributesText
    {
        get
        {
            if (!string.IsNullOrEmpty(_attributesText))
                return _attributesText;
            try
            {
                if (!string.IsNullOrEmpty(Path))
                    return QueryParser.FormatAttributes(File.GetAttributes(Path));
            }
            catch { }
            return string.Empty;
        }
        set
        {
            if (_attributesText == value) return;
            _attributesText = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AttributesDisplay));
        }
    }

    public string AttributesDisplay => AttributesText;

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
