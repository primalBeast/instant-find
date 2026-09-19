using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace InstantFind.Services;

/// <summary>
/// Append-only diagnostic log next to InstantFind.exe (portable zip layout).
/// No extra packages — File.AppendAllText only.
/// </summary>
public static class ErrorLog
{
    public const string FileName = "InstantFind-error.log";

    /// <summary>Folder containing the running executable (portable unzip location).</summary>
    public static string ExeDirectory
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        return dir;
                }
            }
            catch { /* fall through */ }

            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                    return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }

            return ".";
        }
    }

    public static string LogPath => Path.Combine(ExeDirectory, FileName);

    public static string AppVersion
    {
        get
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// Best-effort: another Instant Find process may hold index.db.
    /// </summary>
    public static bool AnotherInstanceMayBeRunning()
    {
        if (SingleInstance.OtherInstanceObservedAtStartup)
            return true;

        try
        {
            var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "InstantFind";
            var count = 0;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id != Environment.ProcessId)
                        count++;
                }
                finally
                {
                    p.Dispose();
                }
            }
            return count > 0;
        }
        catch
        {
            return SingleInstance.OtherInstanceObservedAtStartup;
        }
    }

    public static void AppendFailure(
        string context,
        Exception? ex,
        string? failedPath = null,
        string? liveDbPath = null,
        string? rebuildDbPath = null,
        long skippedLocked = 0,
        string? extra = null)
    {
        try
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("==========");
            sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Version: {AppVersion}");
            sb.AppendLine($"Context: {context}");
            if (!string.IsNullOrEmpty(failedPath))
                sb.AppendLine($"FailedPath: {failedPath}");
            if (!string.IsNullOrEmpty(liveDbPath))
                sb.AppendLine($"LiveDb: {liveDbPath}");
            if (!string.IsNullOrEmpty(rebuildDbPath))
                sb.AppendLine($"RebuildDb: {rebuildDbPath}");
            sb.AppendLine($"SkippedLockedRecent: {skippedLocked}");
            sb.AppendLine($"AnotherInstanceMayBeRunning: {AnotherInstanceMayBeRunning()}");
            sb.AppendLine($"ProcessId: {Environment.ProcessId}");
            if (!string.IsNullOrEmpty(extra))
                sb.AppendLine($"Extra: {extra}");

            AppendExceptionDetails(sb, ex);

            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Never let logging crash the app
        }
    }

    /// <summary>
    /// Unhandled crash dump for AppDomain / Dispatcher / TaskScheduler hooks.
    /// Writes full exception graph + stack before the process exits.
    /// </summary>
    public static void AppendUnhandled(string source, Exception? ex, bool isTerminating = false)
    {
        try
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("########## UNHANDLED ##########");
            sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Version: {AppVersion}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"IsTerminating: {isTerminating}");
            sb.AppendLine($"ProcessId: {Environment.ProcessId}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NET: {Environment.Version}");
            AppendExceptionDetails(sb, ex);
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
            StartupLog.Append("UnhandledException", $"{source} terminating={isTerminating} type={ex?.GetType().FullName}");
        }
        catch
        {
            // Never let logging crash the app
        }
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception? ex)
    {
        if (ex is null)
        {
            sb.AppendLine("Exception: (null)");
            return;
        }

        var depth = 0;
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException, depth++)
        {
            var prefix = depth == 0 ? "Exception" : $"Inner[{depth}]";
            sb.AppendLine($"{prefix}Type: {cur.GetType().FullName}");
            sb.AppendLine($"{prefix}Message: {cur.Message}");
            sb.AppendLine($"{prefix}HResult: 0x{cur.HResult:X8}");
            sb.AppendLine($"{prefix}Stack:");
            sb.AppendLine(cur.StackTrace ?? "(none)");
        }
    }

    public static void AppendNote(string context, string message, string? path = null, long skippedLocked = 0)
    {
        try
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("----------");
            sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Version: {AppVersion}");
            sb.AppendLine($"Context: {context}");
            sb.AppendLine($"Message: {message}");
            if (!string.IsNullOrEmpty(path))
                sb.AppendLine($"Path: {path}");
            if (skippedLocked > 0)
                sb.AppendLine($"SkippedLocked: {skippedLocked}");
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch { }
    }
}

/// <summary>Named mutex for second-instance detection (not a hard single-instance lock).</summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\InstantFind.SingleInstance";

    private static Mutex? _held;

    /// <summary>True when another process already owned the mutex at our startup.</summary>
    public static bool OtherInstanceObservedAtStartup { get; private set; }

    /// <summary>Acquire (or observe) the process mutex; keep it for the process lifetime.</summary>
    public static void TryRegister()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            OtherInstanceObservedAtStartup = !createdNew;
            GC.KeepAlive(_held);
        }
        catch
        {
            // ignore
        }
    }
}
