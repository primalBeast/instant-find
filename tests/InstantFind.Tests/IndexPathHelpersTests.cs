using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class IndexPathHelpersTests
{
    [Theory]
    [InlineData(@"C:\Work\Project\", @"C:\Work\Project")]
    [InlineData(@"C:\Work\Project", @"C:\Work\Project")]
    [InlineData(@"C:/Work/Project/", @"C:\Work\Project")]
    [InlineData(@"D:\", @"D:")]
    public void NormalizePrefix_trims_separators(string input, string expected)
    {
        Assert.Equal(expected, IndexPathHelpers.NormalizePrefix(input));
    }

    [Fact]
    public void BuildDeletePatterns_covers_path_and_directory()
    {
        var p = IndexPathHelpers.BuildDeletePatterns(@"C:\Docs\OldFolder\");
        Assert.Equal(@"C:\Docs\OldFolder", p.Exact);
        Assert.Equal(@"C:\\Docs\\OldFolder\\%", p.PathLike);
        Assert.Equal(@"C:\\Docs\\OldFolder\\%", p.DirectoryLike);
    }

    [Fact]
    public void BuildDeletePatterns_escapes_like_wildcards()
    {
        var p = IndexPathHelpers.BuildDeletePatterns(@"C:\100%_done");
        Assert.Equal(@"C:\100%_done", p.Exact);
        // \ → \\, % → \%, _ → \_
        Assert.Contains(@"100\%\_done", p.PathLike);
        Assert.EndsWith(@"\\%", p.PathLike);
    }

    [Fact]
    public void EscapeLike_escapes_percent_underscore_backslash()
    {
        Assert.Equal(@"a\\b\%c\_d", IndexPathHelpers.EscapeLike(@"a\b%c_d"));
    }
}
