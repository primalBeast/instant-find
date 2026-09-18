namespace InstantFind.Models;

/// <summary>
/// Persisted user settings stored under %AppData%\InstantFind.
/// MaxResults is editable in settings.json (no UI page).
/// </summary>
public sealed class AppSettings
{
    public List<string> IndexedRoots { get; set; } = new();

    /// <summary>
    /// Drive letters included in search results and full rebuilds, e.g. "C:", "D:".
    /// Missing/null on load is filled from IndexedRoots; empty means none enabled.
    /// </summary>
    public List<string>? EnabledDrives { get; set; }

    public int MaxResults { get; set; } = 10000;
    public bool IncludeDirectories { get; set; } = true;
    public bool StartIndexingOnLaunch { get; set; } = true;

    /// <summary>And | Or | LiteralWhitespace — default And.</summary>
    public MatchMode MatchMode { get; set; } = MatchMode.And;

    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }

    /// <summary>Treat search text as a .NET regex against the filename. Off by default.</summary>
    public bool UseRegex { get; set; }

    /// <summary>
    /// Recent/favorite .exe paths for "Open with…" (most recent first, capped at 8).
    /// </summary>
    public List<string> OpenWithFavorites { get; set; } = new();

    /// <summary>Named reusable filter queries (Filter popup).</summary>
    public List<SavedFilter> SavedFilters { get; set; } = new();

    /// <summary>Show Extension column in results grid.</summary>
    public bool ShowExtensionColumn { get; set; }

    /// <summary>Show Attributes column in results grid.</summary>
    public bool ShowAttributesColumn { get; set; }

    /// <summary>
    /// Basename-only crawl skips (any drive). Do NOT put "Windows" / "$Recycle.Bin" /
    /// "System Volume Information" here — those are controlled by Filter → Exclude path prefixes.
    /// Legacy settings that listed them are stripped on load (v1.0.19).
    /// </summary>
    public List<string> ExcludedDirectoryNames { get; set; } = new()
    {
        "node_modules",
        ".git",
        ".svn"
    };

    /// <summary>Ids from ExcludePaths.CommonCatalog that are checked in the Filter popup.</summary>
    public List<string> CheckedCommonExcludeIds { get; set; } = new();

    /// <summary>User-added folder paths to exclude (Filter popup +).</summary>
    public List<string> CustomExcludePaths { get; set; } = new();

    /// <summary>True once default common-exclude checkboxes have been applied.</summary>
    public bool CommonExcludesInitialized { get; set; }
}
