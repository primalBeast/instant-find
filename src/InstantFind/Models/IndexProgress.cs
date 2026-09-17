namespace InstantFind.Models;

public sealed class IndexProgress
{
    public string CurrentPath { get; init; } = string.Empty;
    public long FilesIndexed { get; init; }
    public long DirectoriesScanned { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }

    public string StatusText => IsComplete
        ? $"Indexed {FilesIndexed:N0} items"
        : $"Indexing… {FilesIndexed:N0} items — {CurrentPath}";
}
