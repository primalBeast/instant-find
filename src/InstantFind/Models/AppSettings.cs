namespace InstantFind.Models;

/// <summary>
/// Persisted user settings stored under %AppData%\InstantFind.
/// </summary>
public sealed class AppSettings
{
    public List<string> IndexedRoots { get; set; } = new();
    public int MaxResults { get; set; } = 500;
    public bool IncludeDirectories { get; set; } = true;
    public bool StartIndexingOnLaunch { get; set; } = true;
    public List<string> ExcludedDirectoryNames { get; set; } = new()
    {
        "$Recycle.Bin",
        "System Volume Information",
        "Windows",
        "node_modules",
        ".git",
        ".svn"
    };
}
