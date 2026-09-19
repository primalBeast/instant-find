using System.IO;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class StartupLogTests
{
    [Fact]
    public void LogPath_is_beside_exe_directory()
    {
        Assert.Equal(StartupLog.FileName, Path.GetFileName(StartupLog.LogPath));
        Assert.Equal(ErrorLog.ExeDirectory, Path.GetDirectoryName(StartupLog.LogPath));
    }

    [Fact]
    public void Append_and_BootHeader_write_file()
    {
        var path = StartupLog.LogPath;
        var before = File.Exists(path) ? new FileInfo(path).Length : 0L;
        StartupLog.AppendBootHeader("UnitTest");
        StartupLog.Append("UnitTest.Milestone", "detail=ok");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("UnitTest", text);
        Assert.Contains("UnitTest.Milestone", text);
        Assert.Contains("Version:", text);
        Assert.Contains("Framework:", text);
        Assert.True(new FileInfo(path).Length > before);
    }
}

public class UnhandledErrorLogTests
{
    [Fact]
    public void AppendUnhandled_writes_exception_and_stack()
    {
        var path = ErrorLog.LogPath;
        var before = File.Exists(path) ? new FileInfo(path).Length : 0L;
        try
        {
            throw new InvalidOperationException("startup boom");
        }
        catch (Exception ex)
        {
            ErrorLog.AppendUnhandled("UnitTest.Unhandled", ex, isTerminating: true);
        }

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("UNHANDLED", text);
        Assert.Contains("UnitTest.Unhandled", text);
        Assert.Contains("startup boom", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("IsTerminating: True", text);
        Assert.True(new FileInfo(path).Length > before);
    }
}
