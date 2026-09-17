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
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return Array.Empty<FileEntry>();

        var parsed = QueryParser.Parse(queryText, options.MatchMode, options.UseRegex);
        return _db.Search(parsed, _settings.MaxResults, _settings.IncludeDirectories, options);
    }
}
