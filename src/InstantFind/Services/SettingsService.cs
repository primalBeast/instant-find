using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Loads/saves settings under %AppData%\InstantFind. No admin required.
/// </summary>
public sealed class SettingsService
{
    private const int DefaultMaxResults = 10000;
    private const int LegacyDefaultMaxResults = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string AppDataDirectory { get; }
    public string SettingsPath { get; }
    public string DatabasePath { get; }

    public SettingsService()
    {
        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InstantFind");
        Directory.CreateDirectory(AppDataDirectory);
        SettingsPath = Path.Combine(AppDataDirectory, "settings.json");
        DatabasePath = Path.Combine(AppDataDirectory, "index.db");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    var rootsBefore = settings.IndexedRoots?.Count ?? -1;
                    var drivesBefore = settings.EnabledDrives?.Count ?? -1;
                    EnsureDefaults(settings);
                    // Persist pruned Network/UNC roots so chips and rebuild stay local
                    var rootsAfter = settings.IndexedRoots?.Count ?? 0;
                    var drivesAfter = settings.EnabledDrives?.Count ?? 0;
                    if (rootsAfter != rootsBefore || drivesAfter != drivesBefore)
                    {
                        Save(settings);
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt settings — fall back to defaults
        }

        var defaults = CreateDefaults();
        Save(defaults);
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        // Default: local fixed/removable only — never Network mapped drives (v1.0.16)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (!DriveHelpers.IsIndexableDriveType(drive.DriveType))
                    continue;
                settings.IndexedRoots.Add(drive.RootDirectory.FullName);
            }
        }
        catch
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                settings.IndexedRoots.Add(userProfile);
        }

        if (settings.IndexedRoots.Count == 0)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                settings.IndexedRoots.Add(userProfile);
        }

        settings.EnabledDrives = DriveHelpers.GetDriveLetters(settings.IndexedRoots);
        return settings;
    }

    private static void EnsureDefaults(AppSettings settings)
    {
        // Invalid or legacy v1.0.0 default (500) → raise to 10000
        if (settings.MaxResults <= 0 || settings.MaxResults == LegacyDefaultMaxResults)
            settings.MaxResults = DefaultMaxResults;
        if (settings.ExcludedDirectoryNames is null)
            settings.ExcludedDirectoryNames = new List<string>();
        if (settings.IndexedRoots is null)
            settings.IndexedRoots = new List<string>();

        // Prune remote/mapped Network drives and UNC roots (v1.0.16)
        settings.IndexedRoots = DriveHelpers.FilterIndexableRoots(settings.IndexedRoots);

        // null EnabledDrives (pre-1.0.2) → enable all indexed drive letters
        if (settings.EnabledDrives is null)
            settings.EnabledDrives = DriveHelpers.GetDriveLetters(settings.IndexedRoots);
        else
            settings.EnabledDrives = DriveHelpers.NormalizeDriveLetters(settings.EnabledDrives);

        // Drop network mapped letters from chips / rebuild enable set
        settings.EnabledDrives = DriveHelpers.FilterIndexableDriveLetters(settings.EnabledDrives);

        if (settings.OpenWithFavorites is null)
            settings.OpenWithFavorites = new List<string>();
        else
        {
            // Cap + de-dupe while preserving order (most recent first)
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleaned = new List<string>();
            foreach (var p in settings.OpenWithFavorites)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!seen.Add(p.Trim())) continue;
                cleaned.Add(p.Trim());
                if (cleaned.Count >= 8) break;
            }
            settings.OpenWithFavorites = cleaned;
        }

        if (settings.CheckedCommonExcludeIds is null)
            settings.CheckedCommonExcludeIds = new List<string>();
        if (settings.CustomExcludePaths is null)
            settings.CustomExcludePaths = new List<string>();
        if (!settings.CommonExcludesInitialized)
        {
            settings.CheckedCommonExcludeIds = ExcludePaths.DefaultCheckedIds();
            settings.CommonExcludesInitialized = true;
        }
        settings.CustomExcludePaths = settings.CustomExcludePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.SavedFilters is null)
            settings.SavedFilters = new List<SavedFilter>();
        else
        {
            settings.SavedFilters = settings.SavedFilters
                .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name))
                .Select(f => new SavedFilter
                {
                    Name = f.Name.Trim(),
                    Query = f.Query?.Trim() ?? string.Empty
                })
                .Take(50)
                .ToList();
        }
    }
}

/// <summary>Helpers for drive-letter chips and path-prefix filters.</summary>
public static class DriveHelpers
{
    public static List<string> GetDriveLetters(IEnumerable<string> roots)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var letter = TryGetDriveLetter(root);
            if (letter is not null)
                set.Add(letter);
        }
        return set.ToList();
    }

    public static List<string> NormalizeDriveLetters(IEnumerable<string>? letters)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (letters is null) return set.ToList();
        foreach (var raw in letters)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim().TrimEnd('\\', '/');
            if (t.Length >= 2 && t[1] == ':')
                set.Add(char.ToUpperInvariant(t[0]) + ":");
            else if (t.Length == 1 && char.IsLetter(t[0]))
                set.Add(char.ToUpperInvariant(t[0]) + ":");
        }
        return set.ToList();
    }

    public static string? TryGetDriveLetter(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Windows drive root: C:\...
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            return char.ToUpperInvariant(path[0]) + ":";
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
                return char.ToUpperInvariant(root[0]) + ":";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Local volumes we may index by default: Fixed and Removable.
    /// Network (mapped letters) are never indexable.
    /// </summary>
    public static bool IsIndexableDriveType(DriveType type) =>
        type is DriveType.Fixed or DriveType.Removable;

    /// <summary>UNC share root/path (\\server\share\…).</summary>
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for Network mapped drive letters or UNC paths.
    /// Uses DriveInfo.DriveType when available; UNC always remote.
    /// </summary>
    public static bool IsRemoteRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsUncPath(path)) return true;

        try
        {
            var root = Path.GetPathRoot(path.Trim());
            if (string.IsNullOrEmpty(root)) return false;
            // UNC Path.GetPathRoot returns \\server\share\
            if (IsUncPath(root)) return true;
            var di = new DriveInfo(root);
            return di.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a drive letter like "G:" maps to DriveType.Network.</summary>
    public static bool IsRemoteDriveLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return false;
        var t = letter.Trim().TrimEnd('\\', '/');
        if (t.Length >= 1 && char.IsLetter(t[0]))
        {
            var root = char.ToUpperInvariant(t[0]) + @":\";
            return IsRemoteRoot(root);
        }
        return false;
    }

    /// <summary>Drop Network / UNC roots from an IndexedRoots list.</summary>
    public static List<string> FilterIndexableRoots(IEnumerable<string>? roots)
    {
        var result = new List<string>();
        if (roots is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var r = raw.Trim();
            if (IsRemoteRoot(r)) continue;
            if (!seen.Add(r)) continue;
            result.Add(r);
        }
        return result;
    }

    /// <summary>Drop Network mapped letters from EnabledDrives.</summary>
    public static List<string> FilterIndexableDriveLetters(IEnumerable<string>? letters)
    {
        return NormalizeDriveLetters(letters)
            .Where(l => !IsRemoteDriveLetter(l))
            .ToList();
    }

    public static bool IsRootEnabled(string root, IReadOnlyList<string>? enabledDrives)
    {
        // Never treat remote/UNC as enabled for rebuild even if listed
        if (IsRemoteRoot(root)) return false;
        var letter = TryGetDriveLetter(root);
        if (letter is null) return false; // non-drive non-UNC should not appear; refuse
        if (enabledDrives is null || enabledDrives.Count == 0)
            return false;
        return enabledDrives.Any(d => d.Equals(letter, StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> ToPathPrefixes(IEnumerable<string> driveLetters)
    {
        return driveLetters
            .Select(d =>
            {
                var letter = d.Trim().TrimEnd('\\', '/');
                if (letter.Length >= 2 && letter[1] == ':')
                    return char.ToUpperInvariant(letter[0]) + @":\";
                return letter;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
