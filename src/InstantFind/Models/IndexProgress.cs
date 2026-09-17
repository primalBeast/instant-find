namespace InstantFind.Models;

public sealed class IndexProgress
{
    public string CurrentPath { get; init; } = string.Empty;
    public long FilesIndexed { get; init; }
    public long DirectoriesScanned { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }

    /// <summary>Optional override (e.g. "Preparing rebuild…").</summary>
    public string? Message { get; init; }

    public string StatusText
    {
        get
        {
            if (Message is not null)
                return Message;
            if (IsComplete)
                return $"Indexed {FilesIndexed:N0} items";
            if (string.IsNullOrEmpty(CurrentPath))
                return $"Scanning… {FilesIndexed:N0} items";
            return $"Scanning… {FilesIndexed:N0} items — {CurrentPath}";
        }
    }
}
