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


    [Fact]
    public void StripPathPrefixControlledNames_removes_Windows_and_recycle()
    {
        var names = new List<string> { "Windows", "node_modules", "$Recycle.Bin", ".git", "System Volume Information" };
        ExcludePaths.StripPathPrefixControlledNames(names);
        Assert.DoesNotContain("Windows", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("$Recycle.Bin", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("System Volume Information", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("node_modules", names);
        Assert.Contains(".git", names);
    }

    [Fact]
    public void ResolveActivePrefixes_windows_unchecked_does_not_exclude_hosts_path()
    {
        var settings = new AppSettings
        {
            CheckedCommonExcludeIds = new List<string>(), // windows unchecked
            CommonExcludesInitialized = true,
            ExcludedDirectoryNames = new List<string> { "Windows", "node_modules" } // legacy bare name
        };
        ExcludePaths.StripPathPrefixControlledNames(settings.ExcludedDirectoryNames);
        var prefixes = ExcludePaths.ResolveActivePrefixes(settings);
        Assert.DoesNotContain(prefixes, p => p.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase));
        Assert.False(ExcludePaths.IsUnderAny(@"C:\Windows\System32\drivers\etc\hosts", prefixes));
        Assert.DoesNotContain("Windows", settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveActivePrefixes_windows_checked_excludes_hosts_path()
    {
        var settings = new AppSettings
        {
            CheckedCommonExcludeIds = new List<string> { "windows" },
            CommonExcludesInitialized = true
        };
        var prefixes = ExcludePaths.ResolveActivePrefixes(settings);
        Assert.True(ExcludePaths.IsUnderAny(@"C:\Windows\System32\drivers\etc\hosts", prefixes));
    }

    [Fact]
    public void Default_ExcludedDirectoryNames_must_not_include_Windows()
    {
        var settings = new AppSettings();
        Assert.DoesNotContain("Windows", settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("$Recycle.Bin", settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("node_modules", settings.ExcludedDirectoryNames);
    }
}
