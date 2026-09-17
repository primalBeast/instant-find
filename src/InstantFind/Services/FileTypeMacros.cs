namespace InstantFind.Services;

/// <summary>
/// Shared extension sets for query macros (doc:, img:, …) and the Filter popup Type section.
/// </summary>
public static class FileTypeMacros
{
    public sealed record TypeGroup(string Key, string DisplayName, string[] Extensions, string[] Aliases);

    public static readonly TypeGroup[] Groups =
    {
        new("doc", "Documents",
            new[] { "doc", "docx", "rtf", "odt", "txt", "md", "pdf", "epub", "pages" },
            new[] { "doc", "docs", "document", "documents" }),
        new("img", "Images",
            new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "ico", "svg", "heic", "avif", "raw", "cr2", "nef" },
            new[] { "img", "image", "images", "pic", "pics", "photo", "photos" }),
        new("vid", "Video",
            new[] { "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpeg", "mpg", "ts", "m2ts" },
            new[] { "vid", "video", "videos", "movie", "movies" }),
        new("audio", "Audio",
            new[] { "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma", "aiff", "opus", "mid", "midi" },
            new[] { "audio", "sound", "music" }),
        new("zip", "Archives",
            new[] { "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "cab", "iso", "img" },
            new[] { "zip", "archive", "archives", "compressed" }),
        new("code", "Code",
            new[] { "cs", "cpp", "c", "h", "hpp", "java", "js", "ts", "tsx", "jsx", "py", "rb", "go", "rs", "php", "swift", "kt", "scala", "sql", "html", "htm", "css", "scss", "json", "xml", "yaml", "yml", "toml", "sh", "ps1", "bat", "cmd", "vue", "svelte" },
            new[] { "code", "src", "source" }),
        new("xls", "Spreadsheets",
            new[] { "xls", "xlsx", "csv", "ods", "tsv", "xlsm" },
            new[] { "xls", "sheet", "sheets", "spreadsheet", "spreadsheets", "excel" }),
        new("ppt", "Presentations",
            new[] { "ppt", "pptx", "odp", "key" },
            new[] { "ppt", "presentation", "presentations", "slides" }),
        new("exe", "Executables",
            new[] { "exe", "msi", "com", "scr", "dll", "sys", "bat", "cmd", "ps1", "appx", "msix" },
            new[] { "exe", "executable", "executables", "bin", "binary" }),
        new("font", "Fonts",
            new[] { "ttf", "otf", "woff", "woff2", "eot", "fon" },
            new[] { "font", "fonts" }),
        new("iso", "Disk images",
            new[] { "iso", "img", "vhd", "vhdx", "vmdk", "dmg", "bin", "cue" },
            new[] { "iso", "disk", "diskimage", "diskimages" }),
    };

    private static readonly Dictionary<string, TypeGroup> AliasMap = BuildAliasMap();

    private static Dictionary<string, TypeGroup> BuildAliasMap()
    {
        var map = new Dictionary<string, TypeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups)
        {
            map[g.Key] = g;
            foreach (var a in g.Aliases)
                map[a] = g;
        }
        return map;
    }

    /// <summary>True when token looks like <c>doc:</c> / <c>image:</c> (macro name + colon, optional trailing junk ignored).</summary>
    public static bool TryResolveMacro(string token, out TypeGroup? group)
    {
        group = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var t = token.Trim();
        var colon = t.IndexOf(':');
        if (colon <= 0)
            return false;

        // Only treat as macro when nothing (or only whitespace) follows the colon
        var after = t[(colon + 1)..].Trim();
        if (after.Length > 0)
            return false;

        var name = t[..colon].Trim();
        if (name.Length == 0)
            return false;

        if (AliasMap.TryGetValue(name, out group))
            return true;
        return false;
    }

    public static IReadOnlyList<string> ExtensionsForMacro(string macroKeyOrAlias)
    {
        if (AliasMap.TryGetValue(macroKeyOrAlias.TrimEnd(':'), out var g))
            return g.Extensions;
        return Array.Empty<string>();
    }

    /// <summary>Canonical query token for a type group, e.g. <c>doc:</c>.</summary>
    public static string MacroToken(TypeGroup group) => group.Key + ":";
}
