using System.IO;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class ReparseCrawlGateTests
{
    [Theory]
    [InlineData(@"C:\Users\arthub\OneDrive - Diebold Nixdorf", true)]
    [InlineData(@"C:\Users\arthub\OneDrive - Diebold Nixdorf\Docs", true)]
    [InlineData(@"C:\Users\arthub\OneDrive\file.txt", true)]
    [InlineData(@"C:/Users/x/OneDrive", true)]
    [InlineData(@"C:\Users\arthub\Documents", false)]
    [InlineData(@"C:\Users\arthub\Google Drive\Docs", false)]
    [InlineData(@"D:\data\junction-target", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOneDriveNamedPath_detects_segments(string? path, bool expected)
    {
        Assert.Equal(expected, ReparseCrawlGate.IsOneDriveNamedPath(path));
    }

    [Theory]
    [InlineData(0x9000001Au, true)]   // IO_REPARSE_TAG_CLOUD
    [InlineData(0x9000101Au, true)]   // CLOUD_1
    [InlineData(0x9000F01Au, true)]   // CLOUD_F
    [InlineData(0xA0000003u, false)]  // MOUNT_POINT / junction
    [InlineData(0xA000000Cu, false)]  // SYMLINK
    [InlineData(0x80000020u, false)]  // CSV
    public void IsCloudFilesReparseTag_matches_cloud_family(uint tag, bool expected)
    {
        Assert.Equal(expected, ReparseCrawlGate.IsCloudFilesReparseTag(tag));
    }

    [Fact]
    public void ShouldEnter_when_RecallOnDataAccess_even_without_OneDrive_name()
    {
        var attrs = FileAttributes.Directory | FileAttributes.ReparsePoint | (FileAttributes)0x00400000;
        Assert.True(ReparseCrawlGate.ShouldEnterDirectoryReparsePoint(@"C:\Users\x\CloudSyncRoot", attrs));
    }

    [Fact]
    public void ShouldEnter_when_OneDrive_path_without_recall_flag()
    {
        // Tag probe is Windows-only; path naming alone must allow entry (work laptop case).
        var attrs = FileAttributes.Directory | FileAttributes.ReparsePoint;
        Assert.True(ReparseCrawlGate.ShouldEnterDirectoryReparsePoint(
            @"C:\Users\arthub\OneDrive - Diebold Nixdorf", attrs));
    }

    [Fact]
    public void ShouldSkip_plain_junction_reparse_dir()
    {
        var attrs = FileAttributes.Directory | FileAttributes.ReparsePoint;
        Assert.False(ReparseCrawlGate.ShouldEnterDirectoryReparsePoint(@"C:\Users\x\Documents\linked", attrs));
    }

    [Fact]
    public void ShouldEnter_non_reparse_directory()
    {
        var attrs = FileAttributes.Directory;
        Assert.True(ReparseCrawlGate.ShouldEnterDirectoryReparsePoint(@"C:\Users\x\Documents", attrs));
    }
}
