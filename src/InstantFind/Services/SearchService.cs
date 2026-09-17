using InstantFind.Models;

namespace InstantFind.Services;

public sealed class SearchService
{
    private readonly IndexDatabase _db;
    private readonly AppSettings _settings;

    public SearchService(IndexDatabase db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    public IReadOnlyList<FileEntry> Search(string queryText, SearchOptions options)
        => Search(queryText, options, out _);

    /// <summary>
    /// Searches the index. When <paramref name="invalidRegex"/> is true (UseRegex on and
    /// the pattern is neither valid .NET regex nor shell-glob→regex), returns empty hits
    /// so the UI can show StatusText "Invalid regex".
    /// </summary>
    public IReadOnlyList<FileEntry> Search(string queryText, SearchOptions options, out bool invalidRegex)
    {
        invalidRegex = false;
        if (string.IsNullOrWhiteSpace(queryText))
            return Array.Empty<FileEntry>();

        var parsed = QueryParser.Parse(queryText, options.MatchMode, options.UseRegex);

        if (parsed.UseRegex
            && !string.IsNullOrEmpty(parsed.RegexPattern)
            && !QueryParser.IsRegexPatternValid(parsed.RegexPattern, options.MatchCase))
        {
            invalidRegex = true;
            return Array.Empty<FileEntry>();
        }

        return _db.Search(parsed, _settings.MaxResults, _settings.IncludeDirectories, options);
    }
}
