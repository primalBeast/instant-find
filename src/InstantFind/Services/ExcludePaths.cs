using System.IO;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Common + custom folder excludes for search and crawl (user-mode path prefixes only).
/// </summary>
public static class ExcludePaths
{
    public sealed record CommonFolder(string Id, string Label, string PathTemplate, bool DefaultChecked);

    /// <summary>Static catalog shown in the Filter popup. PathTemplate may use env vars.</summary>
    public static IReadOnlyList<CommonFolder> CommonCatalog { get; } = new[]
    {
        new CommonFolder("windows", @"C:\Windows", @"C:\Windows", true),
        new CommonFolder("programfiles", @"C:\Program Files", @"C:\Program Files", true),
        new CommonFolder("programfilesx86", @"C:\Program Files (x86)", @"C:\Program Files (x86)", true),
        new CommonFolder("programdata", @"C:\ProgramData", @"C:\ProgramData", false),
        new CommonFolder("recovery", @"C:\Recovery", @"C:\Recovery", true),
        new CommonFolder("sysvolinfo", @"C:\System Volume Information", @"C:\System Volume Information", true),
        new CommonFolder("recycle", @"C:\$Recycle.Bin", @"C:\$Recycle.Bin", true),
        new CommonFolder("temp", @"%TEMP%", "%TEMP%", false),
        new CommonFolder("localtemp", @"%LOCALAPPDATA%\Temp", @"%LOCALAPPDATA%\Temp", false),
        new CommonFolder("inetcache", @"%LOCALAPPDATA%\Microsoft\Windows\INetCache",
            @"%LOCALAPPDATA%\Microsoft\Windows\INetCache", false),
    };

    public static string Expand(string pathOrTemplate)
    {
        if (string.IsNullOrWhiteSpace(pathOrTemplate))
            return string.Empty;
        var expanded = Environment.ExpandEnvironmentVariables(pathOrTemplate.Trim());
        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch
        {
            // keep expanded string
        }
        return TrimTrailingSeparators(expanded);
    }

    public static string NormalizePrefix(string path)
    {
        var p = Expand(path);
        if (string.IsNullOrEmpty(p))
            return string.Empty;
        if (!p.EndsWith(Path.DirectorySeparatorChar) && !p.EndsWith(Path.AltDirectorySeparatorChar))
            p += Path.DirectorySeparatorChar;
        return p;
    }

    public static IReadOnlyList<string> ResolveActivePrefixes(AppSettings settings)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var checkedIds = new HashSet<string>(
            settings.CheckedCommonExcludeIds ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in CommonCatalog)
        {
            if (!checkedIds.Contains(c.Id))
                continue;
            var prefix = NormalizePrefix(c.PathTemplate);
            if (!string.IsNullOrEmpty(prefix))
                set.Add(prefix);
        }

        foreach (var custom in settings.CustomExcludePaths ?? new List<string>())
        {
            var prefix = NormalizePrefix(custom);
            if (!string.IsNullOrEmpty(prefix))
                set.Add(prefix);
        }

        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsUnderAny(string fullPath, IReadOnlyList<string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || prefixes.Count == 0)
            return false;
        string path;
        try { path = Path.GetFullPath(fullPath.Trim()); }
        catch { path = fullPath.Trim(); }

        var pathWithSep = path;
        if (!pathWithSep.EndsWith(Path.DirectorySeparatorChar) && !pathWithSep.EndsWith(Path.AltDirectorySeparatorChar))
            pathWithSep += Path.DirectorySeparatorChar;

        foreach (var prefix in prefixes)
        {
            if (pathWithSep.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            var root = TrimTrailingSeparators(prefix);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static List<string> DefaultCheckedIds()
        => CommonCatalog.Where(c => c.DefaultChecked).Select(c => c.Id).ToList();

    private static string TrimTrailingSeparators(string p)
        => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
