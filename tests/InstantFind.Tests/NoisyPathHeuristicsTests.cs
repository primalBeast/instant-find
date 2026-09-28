using Xunit;
using InstantFind.Services;

namespace InstantFind.Tests;

public class NoisyPathHeuristicsTests
{
    private const string Local = @"C:\Users\brad\AppData\Local";
    private static readonly string[] TempRoots = { @"C:\Users\brad\AppData\Local\Temp", @"C:\Temp" };

    [Theory]
    [InlineData(@"C:\Users\brad\AppData\Local\Temp\x.tmp")]
    [InlineData(@"C:\Users\brad\AppData\Local\Temp")]
    [InlineData(@"C:\Temp\sub\file.log")]
    public void Temp_paths_are_noisy(string path)
    {
        Assert.True(NoisyPathHeuristics.IsNoisy(path, TempRoots, Local));
    }

    [Fact]
    public void LocalAppData_Temp_is_noisy_even_without_explicit_temp_roots()
    {
        Assert.True(NoisyPathHeuristics.IsNoisy(
            @"C:\Users\brad\AppData\Local\Temp\abc\def.dat", Array.Empty<string>(), Local));
    }

    [Theory]
    [InlineData(@"C:\Users\brad\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001")]
    [InlineData(@"C:\Users\brad\AppData\Local\Microsoft\Edge\User Data\Default\Code Cache\js\index")]
    [InlineData(@"C:\Users\brad\AppData\Local\Microsoft\Edge\User Data\Default\GPUCache\data_0")]
    [InlineData(@"C:\Users\brad\AppData\Local\Microsoft\Windows\INetCache\IE\abc.dat")]
    [InlineData(@"C:\Users\brad\AppData\Local\NVIDIA\DXCache\ShaderCache\x.bin")]
    [InlineData(@"D:\Projects\app\Caches\blob")]
    [InlineData(@"C:\Users\brad\AppData\Local\Mozilla\Firefox\Profiles\x.default\cache2\entries\ABC123")]
    public void Cache_segments_are_noisy(string path)
    {
        Assert.True(NoisyPathHeuristics.IsNoisy(path, TempRoots, Local));
    }

    [Theory]
    [InlineData(@"C:\Users\brad\Documents\report.docx")]
    [InlineData(@"C:\Users\brad\Documents\Cached notes.txt")]
    [InlineData(@"C:\Users\brad\OneDrive\Desktop\plan.xlsx")]
    [InlineData(@"D:\Code\instant-find\src\CacheHelper.cs")]
    [InlineData(@"C:\Users\brad\AppData\Local\Templates\foo.dotx")]
    public void Normal_paths_are_not_noisy(string path)
    {
        Assert.False(NoisyPathHeuristics.IsNoisy(path, TempRoots, Local));
    }

    [Fact]
    public void Empty_path_is_not_noisy()
    {
        Assert.False(NoisyPathHeuristics.IsNoisy("", TempRoots, Local));
        Assert.False(NoisyPathHeuristics.IsNoisy(null, TempRoots, Local));
    }

    [Fact]
    public void ShouldIgnoreLive_respects_filter_excludes()
    {
        var prefixes = new[] { ExcludePaths.NormalizePrefix(@"D:\Scratch") };
        Assert.True(NoisyPathHeuristics.ShouldIgnoreLive(@"D:\Scratch\a\b.txt", prefixes));
        Assert.False(NoisyPathHeuristics.ShouldIgnoreLive(@"D:\Work\b.txt", prefixes));
    }

    [Fact]
    public void ShouldIgnoreLive_skips_cache_even_without_filter_excludes()
    {
        Assert.True(NoisyPathHeuristics.ShouldIgnoreLive(
            @"D:\Work\app\GPUCache\data_1", Array.Empty<string>()));
    }

    [Fact]
    public void ShouldIgnoreLive_empty_path_is_ignored()
    {
        Assert.True(NoisyPathHeuristics.ShouldIgnoreLive("", Array.Empty<string>()));
    }
}
