namespace InstantFind.Models;

/// <summary>Display model for an "Open with…" favorite exe.</summary>
public sealed class OpenWithFavorite
{
    public OpenWithFavorite(string exePath)
    {
        ExePath = exePath;
        DisplayName = System.IO.Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(DisplayName))
            DisplayName = System.IO.Path.GetFileName(exePath);
    }

    public string ExePath { get; }
    public string DisplayName { get; }
}
