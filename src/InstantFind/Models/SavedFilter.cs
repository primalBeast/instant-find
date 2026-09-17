namespace InstantFind.Models;

/// <summary>Named reusable filter query persisted in AppSettings.</summary>
public sealed class SavedFilter
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
}
