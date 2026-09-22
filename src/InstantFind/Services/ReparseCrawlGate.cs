using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace InstantFind.Services;

/// <summary>
/// Decides whether the indexer may enter a directory that has
/// <see cref="FileAttributes.ReparsePoint"/>. Junctions / mount points are
/// skipped to avoid cycles; OneDrive / Cloud Files directories are entered so
/// placeholder names can be indexed via Enumerate + GetAttributes only
/// (never open / hydrate / download content).
/// </summary>
public static class ReparseCrawlGate
{
    // winnt.h — Cloud Files family (IO_REPARSE_TAG_CLOUD … CLOUD_F)
    // Clears the Cloud Files flags nibble (bits 12–15); matches CLOUD…CLOUD_F.
    private const uint IoReparseTagCloudMask = 0xFFFF0FFF;
    private const uint IoReparseTagCloudBase = 0x9000001A;

    // Win32 FILE_ATTRIBUTE_* — not always present on net*-windows reference assemblies
    private const FileAttributes AttrRecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes AttrRecallOnOpen = (FileAttributes)0x00040000;

    private const uint FileReadAttributes = 0x0080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const int ReparseDataBufferBytes = 16 * 1024;

    /// <summary>
    /// True when a directory reparse point should be crawled (OneDrive / Cloud Files).
    /// False → skip (junctions, mount points, other reparse dirs).
    /// </summary>
    public static bool ShouldEnterDirectoryReparsePoint(string entryPath, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.Directory) == 0)
            return false;
        if ((attrs & FileAttributes.ReparsePoint) == 0)
            return true; // not a reparse dir

        // Cloud Files placeholders advertise RecallOnDataAccess (and sometimes RecallOnOpen).
        if ((attrs & AttrRecallOnDataAccess) != 0)
            return true;
        if ((attrs & AttrRecallOnOpen) != 0)
            return true;

        // Path naming: OneDrive, OneDrive - Contoso, etc. (profile sync root under C:).
        if (IsOneDriveNamedPath(entryPath))
            return true;

        // Reparse tag family IO_REPARSE_TAG_CLOUD* — metadata open only (OPEN_REPARSE_POINT).
        if (OperatingSystem.IsWindows() && TryGetReparseTag(entryPath, out var tag) && IsCloudFilesReparseTag(tag))
            return true;

        return false;
    }

    /// <summary>
    /// True when any path segment starts with "OneDrive" (case-insensitive),
    /// e.g. C:\Users\x\OneDrive - Diebold Nixdorf\Docs.
    /// </summary>
    public static bool IsOneDriveNamedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        ReadOnlySpan<char> span = path.AsSpan().Trim();
        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            bool sep = i == span.Length || span[i] == '\\' || span[i] == '/';
            if (!sep) continue;
            int len = i - start;
            if (len > 0)
            {
                var seg = span.Slice(start, len);
                if (StartsWithOneDrive(seg))
                    return true;
            }
            start = i + 1;
        }
        return false;
    }

    private static bool StartsWithOneDrive(ReadOnlySpan<char> seg) =>
        seg.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);

    /// <summary>IO_REPARSE_TAG_CLOUD through IO_REPARSE_TAG_CLOUD_F.</summary>
    public static bool IsCloudFilesReparseTag(uint tag) =>
        (tag & IoReparseTagCloudMask) == IoReparseTagCloudBase;

    [SupportedOSPlatform("windows")]
    private static bool TryGetReparseTag(string path, out uint tag)
    {
        tag = 0;
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(
                path,
                FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle is null || handle.IsInvalid)
                return false;

            byte[] buffer = new byte[ReparseDataBufferBytes];
            if (!DeviceIoControl(
                    handle,
                    FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    buffer.Length,
                    out _,
                    IntPtr.Zero))
            {
                return false;
            }

            tag = BitConverter.ToUInt32(buffer, 0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
