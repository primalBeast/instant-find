using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace InstantFind.Services;

/// <summary>
/// Append-only boot breadcrumbs next to InstantFind.exe (portable zip layout).
/// Safe to call from static ctors / earliest App entry — never throws to callers.
/// </summary>
public static class StartupLog
{
    public const string FileName = "InstantFind-startup.log";

    public static string LogPath => Path.Combine(ErrorLog.ExeDirectory, FileName);

    public static void Append(string milestone, string? detail = null)
    {
        try
        {
            var sb = new StringBuilder(256);
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append("Z  ");
            sb.Append(milestone);
            if (!string.IsNullOrEmpty(detail))
            {
                sb.Append("  ");
                sb.Append(detail);
            }
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Never let logging crash the app
        }
    }

    /// <summary>Write once at process start: OS, runtime, assembly version, paths.</summary>
    public static void AppendBootHeader(string phase)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version?.ToString() ?? "unknown";
            var sb = new StringBuilder(512);
            sb.AppendLine("==========");
            sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Phase: {phase}");
            sb.AppendLine($"Version: {ver}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OSArch: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"ProcessArch: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($".NET: {Environment.Version}");
            sb.AppendLine($"ProcessId: {Environment.ProcessId}");
            sb.AppendLine($"ProcessPath: {Environment.ProcessPath ?? "(null)"}");
            sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
            sb.AppendLine($"ExeDirectory: {ErrorLog.ExeDirectory}");
            sb.AppendLine($"CommandLine: {Environment.CommandLine}");
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // ignore
        }
    }
}
