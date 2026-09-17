using InstantFind.Models;
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
    [InlineData("D*.pdf", "Document.pdf", @"C:\docs\Document.pdf", "pdf", true)]
    [InlineData("D*.pdf", "data.pdf", @"C:\docs\data.pdf", "pdf", true)]
    [InlineData("D*.pdf", "report.pdf", @"C:\docs\report.pdf", "pdf", false)]
    [InlineData("test?.txt", "test1.txt", @"C:\a\test1.txt", "txt", true)]
    [InlineData("test?.txt", "test12.txt", @"C:\a\test12.txt", "txt", false)]
    public void Wildcard_match(string query, string name, string path, string ext, bool expected)
    {
        var q = QueryParser.Parse(query);
        Assert.True(q.HasWildcards);
        Assert.Equal(expected, QueryParser.Matches(q, name, path, ext));
    }

    [Fact]
    public void ShellWildcardToLike_D_star_pdf()
    {
        Assert.Equal("D%.pdf", QueryParser.ShellWildcardToLike("D*.pdf"));
        Assert.Equal("%.pdf", QueryParser.ShellWildcardToLike("*.pdf"));
        Assert.Equal("test_.txt", QueryParser.ShellWildcardToLike("test?.txt"));
    }

    [Fact]
    public void ShellWildcardToLike_escapes_literals()
    {
        Assert.Equal("100\\%.txt", QueryParser.ShellWildcardToLike("100%.txt"));
        Assert.Equal("a\\_b", QueryParser.ShellWildcardToLike("a_b"));
        Assert.Equal("a\\\\b", QueryParser.ShellWildcardToLike("a\\b"));
    }

    [Fact]
    public void BuildFtsMatch_returns_null_for_wildcards()
    {
        var q = QueryParser.Parse("D*.pdf");
        Assert.True(q.HasWildcards);
        Assert.Null(QueryParser.BuildFtsMatch(q));
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
    public void Multiple_terms_require_all_And_mode()
    {
        var q = QueryParser.Parse("annual report", MatchMode.And);
        Assert.Equal(MatchMode.And, q.Mode);
        Assert.Equal(2, q.Terms.Count);
        Assert.True(QueryParser.Matches(q, "annual_report.xlsx", @"C:\finance\annual_report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
    }

    [Fact]
    public void Or_mode_any_term_matches()
    {
        var q = QueryParser.Parse("annual report", MatchMode.Or);
        Assert.Equal(MatchMode.Or, q.Mode);
        Assert.Equal(2, q.Terms.Count);
        Assert.True(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
        Assert.True(QueryParser.Matches(q, "report.xlsx", @"C:\finance\report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "budget.xlsx", @"C:\finance\budget.xlsx", "xlsx"));
    }

    [Fact]
    public void LiteralWhitespace_keeps_spaces_as_one_term()
    {
        var q = QueryParser.Parse("my file", MatchMode.LiteralWhitespace);
        Assert.Equal(MatchMode.LiteralWhitespace, q.Mode);
        Assert.Single(q.Terms);
        Assert.Equal("my file", q.Terms[0]);
        Assert.True(QueryParser.Matches(q, "my file.txt", @"C:\docs\my file.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "my_file.txt", @"C:\docs\my_file.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "file.txt", @"C:\docs\file.txt", "txt"));
    }

    [Fact]
    public void LiteralWhitespace_with_ext_filter()
    {
        var q = QueryParser.Parse("my file ext:txt", MatchMode.LiteralWhitespace);
        Assert.Single(q.Terms);
        Assert.Equal("my file", q.Terms[0]);
        Assert.Contains("txt", q.Extensions);
        Assert.True(QueryParser.Matches(q, "my file.txt", @"C:\my file.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "my file.pdf", @"C:\my file.pdf", "pdf"));
    }

    [Fact]
    public void BuildFtsMatch_And_joins_with_AND()
    {
        var q = QueryParser.Parse("hello world", MatchMode.And);
        var fts = QueryParser.BuildFtsMatch(q);
        Assert.NotNull(fts);
        Assert.Contains("AND", fts);
        Assert.Contains("hello", fts, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFtsMatch_Or_joins_with_OR()
    {
        var q = QueryParser.Parse("hello world", MatchMode.Or);
        var fts = QueryParser.BuildFtsMatch(q);
        Assert.NotNull(fts);
        Assert.Contains("OR", fts);
        Assert.DoesNotContain(" AND ", fts);
    }

    [Fact]
    public void BuildFtsMatch_LiteralWhitespace_single_token()
    {
        var q = QueryParser.Parse("hello world", MatchMode.LiteralWhitespace);
        var fts = QueryParser.BuildFtsMatch(q);
        Assert.NotNull(fts);
        Assert.DoesNotContain(" AND ", fts);
        Assert.DoesNotContain(" OR ", fts);
        Assert.Contains("hello world", fts, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchCase_sensitive()
    {
        var q = QueryParser.Parse("Report");
        Assert.True(QueryParser.Matches(q, "Report.pdf", @"C:\Report.pdf", "pdf", matchCase: true));
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\report.pdf", "pdf", matchCase: true));
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\report.pdf", "pdf", matchCase: false));
    }

    [Fact]
    public void WholeWord_requires_full_token()
    {
        var q = QueryParser.Parse("report");
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf", wholeWord: true));
        Assert.False(QueryParser.Matches(q, "myreport.pdf", @"C:\docs\myreport.pdf", "pdf", wholeWord: true));
        Assert.True(QueryParser.Matches(q, "file.txt", @"C:\report\file.txt", "txt", wholeWord: true));
    }

    [Fact]
    public void Empty_query_parses_cleanly()
    {
        var q = QueryParser.Parse("  ");
        Assert.Empty(q.Terms);
        Assert.Empty(q.Extensions);
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }

    [Fact]
    public void DriveHelpers_normalize_and_prefix()
    {
        var letters = DriveHelpers.NormalizeDriveLetters(new[] { "c", "D:\\", "e:" });
        Assert.Equal(new[] { "C:", "D:", "E:" }, letters);
        var prefixes = DriveHelpers.ToPathPrefixes(letters);
        Assert.Contains(@"C:\", prefixes);
        Assert.True(DriveHelpers.IsRootEnabled(@"C:\Users", letters));
        Assert.False(DriveHelpers.IsRootEnabled(@"Z:\Data", letters));
    }

    // ----- #4 Regex toggle -----

    [Fact]
    public void Regex_off_uses_substring_not_regex_metacharacters()
    {
        var q = QueryParser.Parse("report\\.pdf", useRegex: false);
        Assert.False(q.UseRegex);
        // Literal backslash-dot in substring mode
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
    }

    [Fact]
    public void Regex_on_matches_filename()
    {
        var q = QueryParser.Parse(@"^report.*\.pdf$", MatchMode.And, useRegex: true);
        Assert.True(q.UseRegex);
        Assert.Equal(@"^report.*\.pdf$", q.RegexPattern);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.True(QueryParser.Matches(q, "report_2024.pdf", @"C:\docs\report_2024.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "budget.pdf", @"C:\docs\budget.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report.docx", @"C:\docs\report.docx", "docx"));
    }

    [Fact]
    public void Regex_respects_MatchCase()
    {
        var q = QueryParser.Parse("Report", MatchMode.And, useRegex: true);
        Assert.True(QueryParser.Matches(q, "Report.pdf", @"C:\Report.pdf", "pdf", matchCase: true));
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\report.pdf", "pdf", matchCase: true));
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\report.pdf", "pdf", matchCase: false));
    }

    [Fact]
    public void Regex_prefers_filename_not_path_folder()
    {
        // Without path separators in the pattern, folder "docs" must not satisfy "doc.*"
        var q = QueryParser.Parse("doc.*", MatchMode.And, useRegex: true);
        Assert.True(QueryParser.Matches(q, "document.pdf", @"C:\other\document.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
    }

    [Fact]
    public void Regex_path_pattern_can_match_full_path()
    {
        var q = QueryParser.Parse(@"docs\\report", MatchMode.And, useRegex: true);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\other\report.pdf", "pdf"));
    }

    [Fact]
    public void Regex_invalid_pattern_matches_nothing()
    {
        var q = QueryParser.Parse("[unterminated", MatchMode.And, useRegex: true);
        Assert.False(QueryParser.Matches(q, "file.txt", @"C:\file.txt", "txt"));
        Assert.False(QueryParser.IsRegexPatternValid("[unterminated"));
    }

    [Fact]
    public void Regex_glob_star_pdf_finds_filename()
    {
        // Users type *.pdf with Regex on — invalid as .NET regex, valid as shell-glob fallback
        var q = QueryParser.Parse("*.pdf", MatchMode.And, useRegex: true);
        Assert.True(q.UseRegex);
        Assert.True(QueryParser.IsRegexPatternValid("*.pdf"));
        Assert.True(QueryParser.Matches(q, "a.pdf", @"C:\docs\a.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "a.docx", @"C:\docs\a.docx", "docx"));
    }

    [Fact]
    public void Regex_plain_report_finds_filename()
    {
        var q = QueryParser.Parse("report", MatchMode.And, useRegex: true);
        Assert.True(QueryParser.Matches(q, "report.txt", @"C:\docs\report.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "budget.txt", @"C:\docs\budget.txt", "txt"));
    }

    [Fact]
    public void Regex_incomplete_group_is_invalid_no_crash()
    {
        var q = QueryParser.Parse("(?", MatchMode.And, useRegex: true);
        Assert.False(QueryParser.IsRegexPatternValid("(?"));
        Assert.False(QueryParser.Matches(q, "file.txt", @"C:\file.txt", "txt"));
        Assert.True(QueryParser.TryCompileRegex("(?", matchCase: false, out var rx) == false);
        Assert.Null(rx);
    }

    [Fact]
    public void Regex_anchored_IMG_digits()
    {
        var q = QueryParser.Parse(@"^IMG_\d+", MatchMode.And, useRegex: true);
        Assert.True(QueryParser.Matches(q, "IMG_001.jpg", @"C:\photos\IMG_001.jpg", "jpg"));
        Assert.False(QueryParser.Matches(q, "photo_IMG_001.jpg", @"C:\photos\photo_IMG_001.jpg", "jpg"));
        Assert.Equal("IMG", QueryParser.TryExtractLongestLiteral(@"^IMG_\d+"));
    }

    [Fact]
    public void BuildFtsMatch_returns_null_for_regex()
    {
        var q = QueryParser.Parse("report", MatchMode.And, useRegex: true);
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }

    // ----- #8 Path scope -----

    [Theory]
    [InlineData(@"C:\Projects\", @"C:\Projects\", "")]
    [InlineData(@"C:\Projects\*.pdf", @"C:\Projects\", "*.pdf")]
    [InlineData(@"C:\Projects\ budget", @"C:\Projects\", "budget")]
    [InlineData(@"C:\Work\ report", @"C:\Work\", "report")]
    [InlineData(@"C:\", @"C:\", "")]
    public void PathScope_extracts_leading_backslash_path(string input, string expectedScope, string expectedRest)
    {
        Assert.True(QueryParser.TryExtractPathScope(input, out var scope, out var rest));
        Assert.Equal(expectedScope, scope);
        Assert.Equal(expectedRest, rest.TrimStart());
    }

    [Fact]
    public void PathScope_absent_for_plain_query()
    {
        Assert.False(QueryParser.TryExtractPathScope("report.pdf", out _, out _));
        var q = QueryParser.Parse("report.pdf");
        Assert.Null(q.PathScope);
    }


    [Fact]
    public void PathScope_absent_for_path_without_trailing_slash()
    {
        // Bradley: C:\Projects is a normal FTS term — do NOT scope to C:\
        Assert.False(QueryParser.TryExtractPathScope(@"C:\Projects", out var scope, out var rest));
        var q = QueryParser.Parse(@"C:\Projects");
        Assert.Null(q.PathScope);
        Assert.Contains(@"C:\Projects", q.Terms);
    }

    [Theory]
    [InlineData(@"C:\Projects\foo", @"C:\Projects\", "foo")]
    [InlineData(@"C:\*.pdf", @"C:\", "*.pdf")]
    public void PathScope_bradley_trailing_slash_rules(string input, string expectedScope, string expectedRest)
    {
        Assert.True(QueryParser.TryExtractPathScope(input, out var scope, out var rest));
        Assert.Equal(expectedScope, scope);
        Assert.Equal(expectedRest, rest.TrimStart());
    }

    [Fact]
    public void PathScope_only_lists_under_directory()
    {
        var q = QueryParser.Parse(@"C:\Projects\");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.Empty(q.Terms);
        Assert.True(QueryParser.Matches(q, "Projects", @"C:\Projects", ""));
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\Projects\a.txt", "txt"));
        Assert.True(QueryParser.Matches(q, "b.txt", @"C:\Projects\sub\b.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "c.txt", @"C:\Other\c.txt", "txt"));
    }

    [Fact]
    public void PathScope_with_wildcard_terms()
    {
        var q = QueryParser.Parse(@"C:\Projects\*.pdf");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.True(q.HasWildcards);
        Assert.Contains("*.pdf", q.Terms);
        Assert.True(QueryParser.Matches(q, "a.pdf", @"C:\Projects\a.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "a.docx", @"C:\Projects\a.docx", "docx"));
        Assert.False(QueryParser.Matches(q, "a.pdf", @"C:\Other\a.pdf", "pdf"));
    }

    [Fact]
    public void PathScope_with_substring_after_space()
    {
        var q = QueryParser.Parse(@"C:\Projects\ budget");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.Contains("budget", q.Terms);
        Assert.True(QueryParser.Matches(q, "budget.xlsx", @"C:\Projects\budget.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "budget.xlsx", @"C:\Other\budget.xlsx", "xlsx"));
    }

    [Fact]
    public void PathScope_respects_MatchCase()
    {
        var q = QueryParser.Parse(@"C:\Projects\");
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\Projects\a.txt", "txt", matchCase: true));
        Assert.False(QueryParser.Matches(q, "a.txt", @"C:\projects\a.txt", "txt", matchCase: true));
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\projects\a.txt", "txt", matchCase: false));
    }

    [Fact]
    public void PathScope_plus_regex()
    {
        var q = QueryParser.Parse(@"C:\Projects\^budget.*", MatchMode.And, useRegex: true);
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.True(q.UseRegex);
        Assert.Equal("^budget.*", q.RegexPattern);
        Assert.True(QueryParser.Matches(q, "budget.xlsx", @"C:\Projects\budget.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "report.xlsx", @"C:\Projects\report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "budget.xlsx", @"C:\Other\budget.xlsx", "xlsx"));
    }
}
