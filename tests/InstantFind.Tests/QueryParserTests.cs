using InstantFind.Services;
using Xunit;

namespace InstantFind.Tests;

public class QueryParserTests
{
    [Theory]
    [InlineData("report", "report.pdf", @"C:\docs\report.pdf", "pdf", true)]
    [InlineData("REPORT", "report.pdf", @"C:\docs\report.pdf", "pdf", true)]
    [InlineData("budget", "report.pdf", @"C:\docs\report.pdf", "pdf", false)]
    [InlineData("docs", "report.pdf", @"C:\docs\report.pdf", "pdf", true)]
    public void Substring_match(string query, string name, string path, string ext, bool expected)
    {
        var q = QueryParser.Parse(query);
        Assert.Equal(expected, QueryParser.Matches(q, name, path, ext));
    }

    [Theory]
    [InlineData("*.pdf", "report.pdf", @"C:\docs\report.pdf", "pdf", true)]
    [InlineData("*.pdf", "report.docx", @"C:\docs\report.docx", "docx", false)]
    [InlineData("rep?rt.pdf", "report.pdf", @"C:\docs\report.pdf", "pdf", true)]
    [InlineData("rep?rt.pdf", "reprt.pdf", @"C:\docs\reprt.pdf", "pdf", false)]
    [InlineData("test*.txt", "test_file.txt", @"C:\a\test_file.txt", "txt", true)]
    public void Wildcard_match(string query, string name, string path, string ext, bool expected)
    {
        var q = QueryParser.Parse(query);
        Assert.True(q.HasWildcards);
        Assert.Equal(expected, QueryParser.Matches(q, name, path, ext));
    }

    [Fact]
    public void Ext_filter_pdf()
    {
        var q = QueryParser.Parse("invoice ext:pdf");
        Assert.Contains("pdf", q.Extensions);
        Assert.Contains("invoice", q.Terms);
        Assert.True(QueryParser.Matches(q, "invoice.pdf", @"C:\invoice.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "invoice.docx", @"C:\invoice.docx", "docx"));
    }

    [Fact]
    public void Ext_filter_only()
    {
        var q = QueryParser.Parse("ext:png");
        Assert.Empty(q.Terms);
        Assert.Single(q.Extensions);
        Assert.True(QueryParser.Matches(q, "photo.png", @"C:\photo.png", "png"));
        Assert.False(QueryParser.Matches(q, "photo.jpg", @"C:\photo.jpg", "jpg"));
    }

    [Fact]
    public void Multiple_terms_require_all()
    {
        var q = QueryParser.Parse("annual report");
        Assert.Equal(2, q.Terms.Count);
        Assert.True(QueryParser.Matches(q, "annual_report.xlsx", @"C:\finance\annual_report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
    }

    [Fact]
    public void BuildFtsMatch_adds_prefix_stars()
    {
        var q = QueryParser.Parse("hello world");
        var fts = QueryParser.BuildFtsMatch(q);
        Assert.NotNull(fts);
        Assert.Contains("AND", fts);
        Assert.Contains("hello", fts, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_query_parses_cleanly()
    {
        var q = QueryParser.Parse("  ");
        Assert.Empty(q.Terms);
        Assert.Empty(q.Extensions);
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }
}
