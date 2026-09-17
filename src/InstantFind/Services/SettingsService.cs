using System.IO;
using System.Text.Json;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Loads/saves settings under %AppData%\InstantFind. No admin required.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
                    EnsureDefaults(settings);
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
        // Default: all fixed drives the current user can see (no admin)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                {
                    settings.IndexedRoots.Add(drive.RootDirectory.FullName);
                }
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

        return settings;
    }

    private static void EnsureDefaults(AppSettings settings)
    {
        if (settings.MaxResults <= 0) settings.MaxResults = 500;
        if (settings.ExcludedDirectoryNames is null)
            settings.ExcludedDirectoryNames = new List<string>();
        if (settings.IndexedRoots is null)
            settings.IndexedRoots = new List<string>();
    }
}
