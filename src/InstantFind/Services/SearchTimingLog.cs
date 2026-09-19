using System.IO;
using System.Text;
using System.Text.Json;
namespace InstantFind.Services;

/// <summary>
/// Silent fire-and-forget JSONL search timing log under %AppData%\InstantFind
/// (exe-dir soft fallback). No Settings UI.
/// </summary>
public static class SearchTimingLog
{
    public const string FileName = "InstantFind-search.log";

    private static readonly object Gate = new();
    private static string? _resolvedPath;

    public static string LogPath
    {
        get
        {
            if (_resolvedPath is not null)
                return _resolvedPath;
            lock (Gate)
            {
                if (_resolvedPath is not null)
                    return _resolvedPath;
                _resolvedPath = ResolvePath();
                return _resolvedPath;
            }
        }
    }

    private static string ResolvePath()
    {
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InstantFind");
            Directory.CreateDirectory(appData);
            var candidate = Path.Combine(appData, FileName);
            // Probe writability
            File.AppendAllText(candidate, "");
            return candidate;
        }
        catch
        {
            try
            {
                return Path.Combine(ErrorLog.ExeDirectory, FileName);
            }
            catch
            {
                return Path.Combine(".", FileName);
            }
        }
    }

    public static void Append(
        double ms,
        int hits,
        bool capped,
        bool cancelled,
        string route,
        string query,
        bool matchCase,
        bool wholeWord,
        bool regex,
        string mode,
        int driveCount,
        int excludeCount)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["ms"] = Math.Round(ms, 2),
                    ["hits"] = hits,
                    ["capped"] = capped,
                    ["cancelled"] = cancelled,
                    ["route"] = route,
                    ["query"] = query ?? "",
                    ["flags"] = new Dictionary<string, object?>
                    {
                        ["matchCase"] = matchCase,
                        ["wholeWord"] = wholeWord,
                        ["regex"] = regex,
                        ["mode"] = mode ?? "",
                        ["drives"] = driveCount,
                        ["excludes"] = excludeCount
                    }
                };
                var line = JsonSerializer.Serialize(payload) + "\n";
                lock (Gate)
                {
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // soft-fail if unwritable
            }
        });
    }
}
