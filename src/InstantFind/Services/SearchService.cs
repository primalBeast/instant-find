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

    public IReadOnlyList<FileEntry> Search(string queryText)
    {
        var parsed = QueryParser.Parse(queryText);
        if (string.IsNullOrWhiteSpace(queryText))
            return Array.Empty<FileEntry>();

        return _db.Search(parsed, _settings.MaxResults, _settings.IncludeDirectories);
    }
}
