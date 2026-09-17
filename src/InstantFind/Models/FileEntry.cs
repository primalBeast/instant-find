namespace InstantFind.Models;

/// <summary>
/// Indexed file or directory metadata shown in search results.
/// </summary>
public sealed class FileEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public bool IsDirectory { get; set; }

    public string FullPath => Path;

    public string SizeDisplay => IsDirectory ? "<DIR>" : FormatSize(Size);

    public string ModifiedDisplay => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

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
