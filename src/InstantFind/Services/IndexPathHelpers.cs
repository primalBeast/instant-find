namespace InstantFind.Services;

/// <summary>
/// Path normalization and SQL LIKE patterns for directory delete / prune.
/// Pure helpers — unit-testable without SQLite.
/// </summary>
public static class IndexPathHelpers
{
    /// <summary>Trim trailing separators; keep drive roots like C:\ as C:</summary>
    public static string NormalizePrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim().Replace('/', '\\').TrimEnd('\\');
        // Drive root "C:" — callers add separator for LIKE
        return trimmed;
    }

    /// <summary>
    /// Builds exact match + LIKE patterns for deleting a folder and everything under it
    /// (path column and directory column). LIKE wildcards in the path are escaped.
    /// </summary>
    public static DeletePatterns BuildDeletePatterns(string directoryPrefix)
    {
        var exact = NormalizePrefix(directoryPrefix);
        var escaped = EscapeLike(exact);
        // With ESCAPE '\', a literal '\' must appear as '\\' in the pattern.
        // Appending a single '\' before '%' would make '\%' (literal percent).
        return new DeletePatterns(
            Exact: exact,
            PathLike: escaped + "\\\\" + "%",
            DirectoryLike: escaped + "\\\\" + "%");
    }

    public static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public readonly record struct DeletePatterns(string Exact, string PathLike, string DirectoryLike);
}
