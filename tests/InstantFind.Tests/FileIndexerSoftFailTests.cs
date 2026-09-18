using System.IO;
using System.Security;
using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class FileIndexerSoftFailTests
{
    [Fact]
    public void IsSkippableContentException_covers_sharing_violation_style_io()
    {
        // Windows: "The process cannot access the file because it is being used by another process."
        var io = new IOException("The process cannot access the file because it is being used by another process.");
        Assert.True(FileIndexer.IsSkippableContentException(io));
        Assert.True(FileIndexer.IsSkippableContentException(new UnauthorizedAccessException()));
        Assert.True(FileIndexer.IsSkippableContentException(new SecurityException()));
        Assert.True(FileIndexer.IsSkippableContentException(new DirectoryNotFoundException()));
        Assert.True(FileIndexer.IsSkippableContentException(new FileNotFoundException()));
        Assert.False(FileIndexer.IsSkippableContentException(new InvalidOperationException("boom")));
        Assert.False(FileIndexer.IsSkippableContentException(new OutOfMemoryException()));
    }

    [Fact]
    public void TryCreateEntry_missing_file_returns_null_without_throw()
    {
        var missing = Path.Combine(Path.GetTempPath(), "instant-find-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        var entry = FileIndexer.TryCreateEntry(missing, Path.GetFileName(missing), isDirectory: false);
        Assert.Null(entry);
    }

    [Fact]
    public void TryCreateEntry_readable_temp_file_succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), "instant-find-ok-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "hello");
            var entry = FileIndexer.TryCreateEntry(path, Path.GetFileName(path), isDirectory: false);
            Assert.NotNull(entry);
            Assert.Equal(path, entry!.Path);
            Assert.Equal("txt", entry.Extension);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
