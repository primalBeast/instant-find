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
        // Default: physical local Fixed/Removable only — never Network, UNC, or cloud-mapped letters (v1.0.17)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!DriveHelpers.IsIndexableLocalDrive(drive))
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

        // Prune Network / UNC / cloud-mapped letters (Google Drive, OneDrive, Dropbox) (v1.0.17)
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
    private static readonly string[] CloudVolumeLabelTokens =
    {
        "google drive",
        "google drive file stream",
        "google drive for desktop",
        "onedrive",
        "dropbox",
        "box",
        "icloud",
        "mega",
        "pcloud",
        "sugarync",
        "nextcloud",
        "seafile"
    };

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
    /// Local volume types we may consider: Fixed and Removable.
    /// Network / CD / RAM / unknown are never indexable by type alone.
    /// </summary>
    public static bool IsIndexableDriveType(DriveType type) =>
        type is DriveType.Fixed or DriveType.Removable;

    /// <summary>
    /// True for a drive we will index by default: ready Fixed/Removable that is not a
    /// cloud / virtual provider volume (Google Drive, OneDrive, Dropbox, …).
    /// DriveType.Network alone is not enough — Google Drive for desktop often reports Fixed.
    /// </summary>
    public static bool IsIndexableLocalDrive(DriveInfo drive)
    {
        if (drive is null) return false;
        try
        {
            if (!drive.IsReady) return false;
            if (!IsIndexableDriveType(drive.DriveType)) return false;
            if (IsCloudVolume(drive)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detect Google Drive / OneDrive / Dropbox (and similar) virtual volumes that report as Fixed.
    /// Uses volume label tokens + root folder markers. Prefer excluding when uncertain cloud.
    /// </summary>
    public static bool IsCloudVolume(DriveInfo drive)
    {
        if (drive is null) return false;
        try
        {
            string label = "";
            try { label = drive.VolumeLabel ?? ""; } catch { label = ""; }
            if (LooksLikeCloudVolumeLabel(label))
                return true;

            string root;
            try { root = drive.RootDirectory.FullName; }
            catch { return false; }

            if (HasGoogleDriveRootMarkers(root))
                return true;
            if (HasDropboxRootMarkers(root))
                return true;
            if (HasOneDriveRootMarkers(root))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Public for tests — volume label heuristic.</summary>
    public static bool LooksLikeCloudVolumeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var lower = label.Trim().ToLowerInvariant();
        foreach (var token in CloudVolumeLabelTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Google Drive for desktop / File Stream root markers.</summary>
    public static bool HasGoogleDriveRootMarkers(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            // Classic File Stream / desktop: G:\My Drive + G:\.shortcut-targets-by-id
            var shortcut = Path.Combine(root, ".shortcut-targets-by-id");
            var myDrive = Path.Combine(root, "My Drive");
            var tmpDrive = Path.Combine(root, ".tmp.drivedownload");
            var tmpUpload = Path.Combine(root, ".tmp.driveupload");
            if (Directory.Exists(shortcut))
                return true;
            if (Directory.Exists(tmpDrive) || Directory.Exists(tmpUpload))
                return true;
            // "My Drive" alone is weak; require another GDrive marker or only a couple top entries
            if (Directory.Exists(myDrive) && (Directory.Exists(shortcut) || Directory.Exists(tmpDrive)))
                return true;
            if (Directory.Exists(myDrive))
            {
                // Heuristic: root looks like GDrive when My Drive exists and no Windows/Program Files
                var hasWindows = Directory.Exists(Path.Combine(root, "Windows"));
                var hasProgramFiles = Directory.Exists(Path.Combine(root, "Program Files"));
                if (!hasWindows && !hasProgramFiles)
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasDropboxRootMarkers(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            if (Directory.Exists(Path.Combine(root, ".dropbox")))
                return true;
            if (File.Exists(Path.Combine(root, ".dropbox")))
                return true;
            return false;
        }
        catch { return false; }
    }

    public static bool HasOneDriveRootMarkers(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            // Letter-mapped OneDrive is uncommon; marker folder + no Windows tree
            var personal = Path.Combine(root, "OneDrive");
            if (Directory.Exists(personal))
            {
                var hasWindows = Directory.Exists(Path.Combine(root, "Windows"));
                if (!hasWindows)
                    return true;
            }
            // Cloud Files placeholder directory sometimes present at root
            if (Directory.Exists(Path.Combine(root, ".onedrive")))
                return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>UNC share root/path (\\server\share\…).</summary>
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for Network mapped letters, UNC, non-indexable DriveTypes, or cloud virtual volumes.
    /// </summary>
    public static bool IsRemoteRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsUncPath(path)) return true;

        try
        {
            var root = Path.GetPathRoot(path.Trim());
            if (string.IsNullOrEmpty(root)) return false;
            if (IsUncPath(root)) return true;
            var di = new DriveInfo(root);
            if (di.DriveType == DriveType.Network) return true;
            if (!IsIndexableDriveType(di.DriveType)) return true;
            if (IsCloudVolume(di)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a drive letter like "G:" is Network or cloud-mapped.</summary>
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

    /// <summary>Drop Network / UNC / cloud roots from an IndexedRoots list.</summary>
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

    /// <summary>Drop Network / cloud mapped letters from EnabledDrives.</summary>
    public static List<string> FilterIndexableDriveLetters(IEnumerable<string>? letters)
    {
        return NormalizeDriveLetters(letters)
            .Where(l => !IsRemoteDriveLetter(l))
            .ToList();
    }

    public static bool IsRootEnabled(string root, IReadOnlyList<string>? enabledDrives)
    {
        // Never treat remote/UNC/cloud as enabled for rebuild even if listed
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
