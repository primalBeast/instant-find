using Xunit;
using InstantFind.Models;
using InstantFind.Services;

namespace InstantFind.Tests;

public class ExcludePathsTests
{
    [Fact]
    public void NormalizePrefix_adds_trailing_separator()
    {
        var p = ExcludePaths.NormalizePrefix(@"C:\Windows");
        Assert.EndsWith("\\", p);
        Assert.StartsWith(@"C:\Windows", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsUnderAny_matches_child_not_sibling()
    {
        var prefixes = new[] { ExcludePaths.NormalizePrefix(@"C:\Windows") };
        Assert.True(ExcludePaths.IsUnderAny(@"C:\Windows\System32\cmd.exe", prefixes));
        Assert.True(ExcludePaths.IsUnderAny(@"C:\Windows", prefixes));
        Assert.False(ExcludePaths.IsUnderAny(@"C:\WindowsOld\x", prefixes));
        Assert.False(ExcludePaths.IsUnderAny(@"C:\Users\bradley", prefixes));
    }

    [Fact]
    public void ResolveActivePrefixes_includes_checked_common_and_custom()
    {
        var settings = new AppSettings
        {
            CheckedCommonExcludeIds = new List<string> { "windows" },
            CustomExcludePaths = new List<string> { @"D:\Scratch\Cache" },
            CommonExcludesInitialized = true
        };
        var prefixes = ExcludePaths.ResolveActivePrefixes(settings);
        Assert.Contains(prefixes, p => p.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prefixes, p => p.StartsWith(@"D:\Scratch\Cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultCheckedIds_includes_windows()
    {
        Assert.Contains("windows", ExcludePaths.DefaultCheckedIds());
    }
}
