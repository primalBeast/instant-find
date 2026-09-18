using System.IO;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class ErrorLogTests
{
    [Fact]
    public void LogPath_is_beside_exe_directory()
    {
        Assert.Equal(ErrorLog.FileName, Path.GetFileName(ErrorLog.LogPath));
        Assert.Equal(ErrorLog.ExeDirectory, Path.GetDirectoryName(ErrorLog.LogPath));
    }

    [Fact]
    public void AppendFailure_writes_file_with_exception_details()
    {
        var path = ErrorLog.LogPath;
        var before = File.Exists(path) ? new FileInfo(path).Length : 0L;
        var ex = new IOException("The process cannot access the file because it is being used by another process.");
        ErrorLog.AppendFailure(
            "UnitTest",
            ex,
            failedPath: @"C:\Users\Test\AppData\Roaming\InstantFind\index.db",
            liveDbPath: @"C:\Users\Test\AppData\Roaming\InstantFind\index.db",
            rebuildDbPath: @"C:\Users\Test\AppData\Roaming\InstantFind\index-rebuild.db",
            skippedLocked: 3,
            extra: "test");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("UnitTest", text);
        Assert.Contains("being used by another process", text);
        Assert.Contains("index.db", text);
        Assert.True(new FileInfo(path).Length > before);
    }

    [Fact]
    public void IsSharingViolation_detects_classic_message()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process.");
        Assert.True(IndexDatabase.IsSharingViolation(ex));
        Assert.False(IndexDatabase.IsSharingViolation(new IOException("disk full")));
        Assert.False(IndexDatabase.IsSharingViolation(new InvalidOperationException("x")));
    }
}
