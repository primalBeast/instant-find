using System.IO;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class DriveHelpersTests
{
    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(DriveType.Unknown, false)]
    public void IsIndexableDriveType_allows_fixed_and_removable_only(DriveType type, bool expected)
    {
        Assert.Equal(expected, DriveHelpers.IsIndexableDriveType(type));
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    [InlineData(@"//server/share", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"", false)]
    [InlineData(null, false)]
    public void IsUncPath_detects_unc_roots(string? path, bool expected)
    {
        Assert.Equal(expected, DriveHelpers.IsUncPath(path));
    }

    [Fact]
    public void FilterIndexableRoots_drops_unc()
    {
        var roots = new[]
        {
            @"C:\",
            @"\\fileserver\docs",
            @"D:\Data",
            @"\\another\share\path"
        };
        var filtered = DriveHelpers.FilterIndexableRoots(roots);
        Assert.Equal(2, filtered.Count);
        Assert.Contains(@"C:\", filtered);
        Assert.Contains(@"D:\Data", filtered);
        Assert.DoesNotContain(filtered, r => DriveHelpers.IsUncPath(r));
    }

    [Fact]
    public void IsRemoteRoot_unc_is_remote()
    {
        Assert.True(DriveHelpers.IsRemoteRoot(@"\\corp\home\user"));
        Assert.True(DriveHelpers.IsRemoteRoot(@"\\corp\home"));
    }

    [Fact]
    public void IsRootEnabled_rejects_unc_even_if_listed()
    {
        Assert.False(DriveHelpers.IsRootEnabled(@"\\server\share", new[] { "C:", "G:" }));
    }
}
