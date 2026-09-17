namespace InstantFind.Models;

/// <summary>
/// Per-search options passed from the UI into SearchService / IndexDatabase.
/// </summary>
public sealed class SearchOptions
{
    public MatchMode MatchMode { get; init; } = MatchMode.And;
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }

    /// <summary>Treat the query body (after path scope) as a .NET regex.</summary>
    public bool UseRegex { get; init; }

    /// <summary>Enabled drive roots as prefixes, e.g. @"C:\". Empty = no results.</summary>
    public IReadOnlyList<string> EnabledDrivePrefixes { get; init; } = Array.Empty<string>();

    /// <summary>Normalized path prefixes to hide from results (trailing separator).</summary>
    public IReadOnlyList<string> ExcludePathPrefixes { get; init; } = Array.Empty<string>();
}

