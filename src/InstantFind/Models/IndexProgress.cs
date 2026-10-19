namespace InstantFind.Models;

public sealed class IndexProgress
{
    public string CurrentPath { get; init; } = string.Empty;
    public long FilesIndexed { get; init; }
    public long DirectoriesScanned { get; init; }
    public long SkippedLocked { get; init; }

    /// <summary>Directories whose listing failed. Quiet suffix on the rebuild status line.</summary>
    public long SkippedFolders { get; init; }

    public bool IsComplete { get; init; }
    public string? Error { get; init; }

    /// <summary>Optional override (e.g. "Preparing rebuild…").</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Final rebuild line. Existing wording, plus <c> · Skipped N folders</c> when
    /// <see cref="SkippedFolders"/> is greater than zero.
    /// </summary>
    public string RebuildCompleteStatus
    {
        get
        {
            var skip = SkippedLocked > 0 ? $" (skipped {SkippedLocked:N0} locked)" : "";
            var folders = SkippedFolders > 0 ? $" · Skipped {SkippedFolders:N0} folders" : "";
            return $"Indexed {FilesIndexed:N0} items{skip}{folders}";
        }
    }

    public string StatusText
    {
        get
        {
            if (Message is not null)
                return Message;
            if (IsComplete)
                return RebuildCompleteStatus;
            var skip = SkippedLocked > 0 ? $" (skipped {SkippedLocked:N0} locked)" : "";
            if (string.IsNullOrEmpty(CurrentPath))
                return $"Scanning… {FilesIndexed:N0} items{skip}";
            return $"Scanning… {FilesIndexed:N0} items{skip} — {CurrentPath}";
        }
    }
}
