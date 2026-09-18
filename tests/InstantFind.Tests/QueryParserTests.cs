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
    public void BuildFtsMatch_LiteralWhitespace_leaves_fts_for_spaces()
    {
        // Space is an FTS unicode61 separator — use LIKE for literal contiguous match
        var q = QueryParser.Parse("hello world", MatchMode.LiteralWhitespace);
        Assert.Null(QueryParser.BuildFtsMatch(q));
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


    // ----- v1.0.10: FTS unicode61 separators (_, -) leave FTS / LIKE literal -----

    [Fact]
    public void BuildFtsMatch_returns_null_for_underscore_term()
    {
        var q = QueryParser.Parse("72_");
        Assert.Contains("72_", q.Terms);
        Assert.True(QueryParser.TermHasFtsTokenSeparators("72_"));
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }

    [Fact]
    public void BuildFtsMatch_returns_null_for_hyphen_term()
    {
        var q = QueryParser.Parse("a-b");
        Assert.Contains("a-b", q.Terms);
        Assert.True(QueryParser.TermHasFtsTokenSeparators("a-b"));
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }

    [Fact]
    public void BuildFtsMatch_allows_alphanumeric_terms()
    {
        var q = QueryParser.Parse("report42");
        Assert.False(QueryParser.TermHasFtsTokenSeparators("report42"));
        Assert.NotNull(QueryParser.BuildFtsMatch(q));
    }

    [Fact]
    public void Underscore_term_does_not_match_digits_alone()
    {
        // Bug: FTS "72_"* tokenized as "72" matched "6728". LIKE + Matches require literal '_'.
        var q = QueryParser.Parse("72_");
        Assert.False(QueryParser.Matches(q, "6728", @"C:\data\6728", ""));
        Assert.False(QueryParser.Matches(q, "6728.txt", @"C:\data\6728.txt", "txt"));
    }

    [Fact]
    public void Underscore_term_matches_literal_underscore_in_name()
    {
        var q = QueryParser.Parse("72_");
        Assert.True(QueryParser.Matches(q, "file72_name.txt", @"C:\data\file72_name.txt", "txt"));
        Assert.True(QueryParser.Matches(q, "72_", @"C:\data\72_", ""));
        Assert.True(QueryParser.Matches(q, "x72_y", @"C:\data\x72_y", ""));
    }

    [Fact]
    public void Hyphen_term_is_literal_not_token_break()
    {
        var q = QueryParser.Parse("a-b");
        Assert.True(QueryParser.Matches(q, "a-b.txt", @"C:\docs\a-b.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "ab.txt", @"C:\docs\ab.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "a_b.txt", @"C:\docs\a_b.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "unrelated.txt", @"C:\docs\unrelated.txt", "txt"));
    }

    [Fact]
    public void PlainTermToLike_escapes_underscore_for_sql()
    {
        // LIKE path must use ESCAPE '\' with escaped '_' so 72_ ≠ 6728
        Assert.Equal(@"%72\_%", QueryParser.PlainTermToLike("72_"));
        Assert.Equal("%a-b%", QueryParser.PlainTermToLike("a-b"));
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

    // ----- v1.0.11: Everything-like path term \72 -----

    [Fact]
    public void PathTerm_backslash72_is_path_term_not_scope()
    {
        var q = QueryParser.Parse(@"\72");
        Assert.Null(q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.True(QueryParser.IsPathTerm(@"\72"));
        Assert.Equal("72", QueryParser.PathTermRest(@"\72"));
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }

    [Fact]
    public void PathTerm_backslash72_matches_segment_prefix()
    {
        var q = QueryParser.Parse(@"\72");
        // Segment starts with 72
        Assert.True(QueryParser.Matches(q, "72folder", @"C:\data\72folder", ""));
        Assert.True(QueryParser.Matches(q, "file.txt", @"C:\72reports\file.txt", "txt"));
        Assert.True(QueryParser.Matches(q, "72report.txt", @"C:\docs\72report.txt", "txt"));
        Assert.True(QueryParser.Matches(q, "72", @"D:\72", ""));
    }

    [Fact]
    public void PathTerm_backslash72_does_not_match_mid_segment_or_bare_fts()
    {
        var q = QueryParser.Parse(@"\72");
        // Segment does NOT start with 72 (would wrongly match bare FTS "72")
        Assert.False(QueryParser.Matches(q, "x72y.txt", @"C:\data\x72y.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "6728", @"C:\data\6728", ""));
        Assert.False(QueryParser.Matches(q, "file.txt", @"C:\data\file.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "a72", @"C:\other\a72\file.txt", "txt"));
    }

    [Fact]
    public void PathTerm_keeps_drive_path_scope()
    {
        var q = QueryParser.Parse(@"C:\foo\");
        Assert.Equal(@"C:\foo\", q.PathScope);
        Assert.Empty(q.Terms);
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\foo\a.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "a.txt", @"C:\other\a.txt", "txt"));
    }

    [Fact]
    public void PathTerm_with_path_scope_combined()
    {
        var q = QueryParser.Parse(@"C:\Projects\ \72");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.True(QueryParser.Matches(q, "72x.txt", @"C:\Projects\72x.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "72x.txt", @"C:\Other\72x.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "x72.txt", @"C:\Projects\x72.txt", "txt"));
    }

    [Fact]
    public void PathTermToLike_escapes_leading_backslash()
    {
        // LIKE ESCAPE '\': \\ = literal backslash → pattern matches paths containing \72
        Assert.Equal(@"%\\72%", QueryParser.PathTermToLike(@"\72"));
    }

    [Fact]
    public void PathTerm_whole_word_requires_exact_segment()
    {
        var q = QueryParser.Parse(@"\72");
        Assert.True(QueryParser.Matches(q, "72", @"C:\data\72", "", matchCase: false, wholeWord: true));
        Assert.False(QueryParser.Matches(q, "72folder", @"C:\data\72folder", "", matchCase: false, wholeWord: true));
    }



    // ----- v1.0.12: C:\Logs  \72 path-scope + path-term (spaces, no trailing \) -----

    [Theory]
    [InlineData(@"C:\Logs  \72")]
    [InlineData(@"C:\Logs \72")]
    public void PathScope_space_before_path_term_scopes_dir_without_trailing_slash(string input)
    {
        // Repro: spaces between C:\Logs and \72 must NOT invent scope "C:\Logs  \"
        Assert.True(QueryParser.TryExtractPathScope(input, out var scope, out var rest));
        Assert.Equal(@"C:\Logs\", scope);
        Assert.Equal(@"\72", rest.TrimStart());

        var q = QueryParser.Parse(input);
        Assert.Equal(@"C:\Logs\", q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.DoesNotContain("Logs", q.Terms);
        Assert.Null(QueryParser.BuildFtsMatch(q));

        Assert.True(QueryParser.Matches(q, "72folder", @"C:\Logs\72folder", ""));
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\Logs\72reports\a.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "72folder", @"C:\Other\72folder", ""));
        Assert.False(QueryParser.Matches(q, "x72.txt", @"C:\Logs\x72.txt", "txt"));
    }

    [Theory]
    [InlineData(@"C:\Logs\ \72")]
    [InlineData(@"C:\Logs\\72")]
    public void PathScope_trailing_slash_plus_path_term(string input)
    {
        var q = QueryParser.Parse(input);
        Assert.Equal(@"C:\Logs\", q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.True(QueryParser.Matches(q, "72x", @"C:\Logs\72x", ""));
        Assert.False(QueryParser.Matches(q, "72x", @"C:\Other\72x", ""));
    }

    [Fact]
    public void PathScope_Logs_alone_with_trailing_slash_lists_under_dir()
    {
        var q = QueryParser.Parse(@"C:\Logs\");
        Assert.Equal(@"C:\Logs\", q.PathScope);
        Assert.Empty(q.Terms);
        Assert.True(QueryParser.Matches(q, "Logs", @"C:\Logs", ""));
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\Logs\a.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "a.txt", @"C:\Other\a.txt", "txt"));
    }

    [Fact]
    public void PathScope_normal_name_under_scoped_path_with_trailing_slash()
    {
        var q = QueryParser.Parse(@"C:\Logs\ report");
        Assert.Equal(@"C:\Logs\", q.PathScope);
        Assert.Contains("report", q.Terms);
        Assert.True(QueryParser.Matches(q, "report.txt", @"C:\Logs\report.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "report.txt", @"C:\Other\report.txt", "txt"));
    }

    [Fact]
    public void PathScope_does_not_absorb_trailing_spaces_into_segment()
    {
        // Guard against regressing to scope="C:\Logs  \"
        Assert.True(QueryParser.TryExtractPathScope(@"C:\Logs  \72", out var scope, out _));
        Assert.Equal(@"C:\Logs\", scope);
        Assert.DoesNotContain("  ", scope);
    }

    [Fact]
    public void PathTerm_backslash72_alone_still_global_segment_prefix()
    {
        var q = QueryParser.Parse(@"\72");
        Assert.Null(q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.True(QueryParser.Matches(q, "72folder", @"C:\data\72folder", ""));
        Assert.False(QueryParser.Matches(q, "x72y.txt", @"C:\data\x72y.txt", "txt"));
    }

    // ----- v1.0.13: NOT / macros / size / date -----

    [Fact]
    public void Not_excludes_matching_term()
    {
        var q = QueryParser.Parse("report !temp");
        Assert.Contains("report", q.Terms);
        Assert.Contains("temp", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report_temp.pdf", @"C:\docs\report_temp.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "temp_report.pdf", @"C:\docs\temp_report.pdf", "pdf"));
    }

    [Fact]
    public void Not_wildcard_excludes_extension()
    {
        var q = QueryParser.Parse("report !*.tmp");
        Assert.Contains("report", q.Terms);
        Assert.Contains("*.tmp", q.NotTerms);
        Assert.True(q.HasWildcards);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report.tmp", @"C:\docs\report.tmp", "tmp"));
    }

    [Fact]
    public void Not_only_excludes_without_positive_terms()
    {
        var q = QueryParser.Parse("!temp");
        Assert.Empty(q.Terms);
        Assert.Contains("temp", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "temp.pdf", @"C:\docs\temp.pdf", "pdf"));
    }

    [Fact]
    public void Not_works_with_path_scope()
    {
        var q = QueryParser.Parse(@"C:\Projects\ report !draft");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.Contains("report", q.Terms);
        Assert.Contains("draft", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\Projects\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report_draft.pdf", @"C:\Projects\report_draft.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report.pdf", @"C:\Other\report.pdf", "pdf"));
    }

    [Fact]
    public void Not_works_with_Or_mode()
    {
        var q = QueryParser.Parse("annual report !budget", MatchMode.Or);
        Assert.True(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
        Assert.True(QueryParser.Matches(q, "report.xlsx", @"C:\finance\report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "budget_report.xlsx", @"C:\finance\budget_report.xlsx", "xlsx"));
    }

    [Fact]
    public void Macro_doc_expands_to_document_extensions()
    {
        var q = QueryParser.Parse("invoice doc:");
        Assert.Contains("pdf", q.Extensions);
        Assert.Contains("docx", q.Extensions);
        Assert.Contains("invoice", q.Terms);
        Assert.True(QueryParser.Matches(q, "invoice.pdf", @"C:\invoice.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "invoice.png", @"C:\invoice.png", "png"));
    }

    [Theory]
    [InlineData("img:", "png", true)]
    [InlineData("image:", "jpg", true)]
    [InlineData("vid:", "mp4", true)]
    [InlineData("video:", "mkv", true)]
    [InlineData("audio:", "mp3", true)]
    [InlineData("zip:", "rar", true)]
    [InlineData("archive:", "7z", true)]
    [InlineData("code:", "cs", true)]
    [InlineData("xls:", "xlsx", true)]
    [InlineData("ppt:", "pptx", true)]
    [InlineData("exe:", "exe", true)]
    [InlineData("font:", "ttf", true)]
    [InlineData("iso:", "iso", true)]
    public void Macro_aliases_expand(string macro, string ext, bool expected)
    {
        var q = QueryParser.Parse(macro);
        Assert.Equal(expected, q.Extensions.Contains(ext));
        Assert.True(QueryParser.Matches(q, $"file.{ext}", $@"C:\file.{ext}", ext));
    }

    [Fact]
    public void Macro_with_Not_and_path_scope()
    {
        var q = QueryParser.Parse(@"C:\docs\ doc: !draft");
        Assert.Equal(@"C:\docs\", q.PathScope);
        Assert.Contains("pdf", q.Extensions);
        Assert.Contains("draft", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "notes.pdf", @"C:\docs\notes.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "draft.pdf", @"C:\docs\draft.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "notes.pdf", @"C:\other\notes.pdf", "pdf"));
    }

    [Fact]
    public void Size_greater_than_1mb()
    {
        Assert.True(QueryParser.TryParseSizeSpec(">1mb", out var min, out var max));
        Assert.Equal(1024L * 1024 + 1, min);
        Assert.Null(max);

        var q = QueryParser.Parse("size:>1mb");
        Assert.True(q.HasSizeFilter);
        Assert.True(QueryParser.Matches(q, "big.bin", @"C:\big.bin", "bin", size: 2_000_000));
        Assert.False(QueryParser.Matches(q, "small.bin", @"C:\small.bin", "bin", size: 100));
        Assert.False(QueryParser.Matches(q, "folder", @"C:\folder", "", size: 0, isDirectory: true));
    }

    [Fact]
    public void Size_range_1mb_to_10mb()
    {
        var q = QueryParser.Parse("report size:1mb..10mb");
        Assert.Contains("report", q.Terms);
        Assert.Equal(1024L * 1024, q.SizeMin);
        Assert.Equal(10L * 1024 * 1024, q.SizeMax);
        Assert.True(QueryParser.Matches(q, "report.bin", @"C:\report.bin", "bin", size: 5_000_000));
        Assert.False(QueryParser.Matches(q, "report.bin", @"C:\report.bin", "bin", size: 100));
        Assert.False(QueryParser.Matches(q, "report.bin", @"C:\report.bin", "bin", size: 20_000_000));
    }

    [Fact]
    public void Date_today_preset()
    {
        var q = QueryParser.Parse("dm:today");
        Assert.True(q.HasDateFilter);
        Assert.NotNull(q.ModifiedAfterUtc);
        Assert.NotNull(q.ModifiedBeforeUtc);
        var now = DateTime.UtcNow;
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\a.txt", "txt", modifiedUtc: now));
        Assert.False(QueryParser.Matches(q, "old.txt", @"C:\old.txt", "txt", modifiedUtc: now.AddDays(-3)));
    }

    [Fact]
    public void Date_range_explicit()
    {
        var q = QueryParser.Parse("dm:2024-01-01..2024-12-31");
        Assert.True(q.HasDateFilter);
        Assert.True(QueryParser.Matches(q, "a.txt", @"C:\a.txt", "txt",
            modifiedUtc: new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(QueryParser.Matches(q, "a.txt", @"C:\a.txt", "txt",
            modifiedUtc: new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Size_and_date_combined_with_terms()
    {
        var q = QueryParser.Parse("report size:>100kb dm:thisyear");
        Assert.Contains("report", q.Terms);
        Assert.True(q.HasSizeFilter);
        Assert.True(q.HasDateFilter);
        Assert.DoesNotContain("size:", q.Terms);
        Assert.DoesNotContain("dm:", q.Terms);
    }

    [Fact]
    public void PathScope_v1012_still_works_with_Not()
    {
        var q = QueryParser.Parse(@"C:\Logs  \72 !temp");
        Assert.Equal(@"C:\Logs\", q.PathScope);
        Assert.Contains(@"\72", q.Terms);
        Assert.Contains("temp", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "72folder", @"C:\Logs\72folder", ""));
        Assert.False(QueryParser.Matches(q, "72temp", @"C:\Logs\72temp", ""));
    }

    [Fact]
    public void FormatAttributes_letters()
    {
        var attrs = System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.Hidden | System.IO.FileAttributes.Archive;
        var text = QueryParser.FormatAttributes(attrs);
        Assert.Contains("R", text);
        Assert.Contains("H", text);
        Assert.Contains("A", text);
    }

    [Fact]
    public void QueryFilterTokens_upsert_and_clear()
    {
        var q = "report";
        q = QueryFilterTokens.UpsertSize(q, ">1mb");
        Assert.Contains("size:>1mb", q);
        q = QueryFilterTokens.UpsertDate(q, "today");
        Assert.Contains("dm:today", q);
        q = QueryFilterTokens.UpsertTypeMacro(q, "doc");
        Assert.Contains("doc:", q);
        Assert.True(QueryFilterTokens.HasStructuredFilters(q));
        q = QueryFilterTokens.ClearStructured(q);
        Assert.Equal("report", q);
        Assert.False(QueryFilterTokens.HasStructuredFilters(q));
    }

    [Fact]
    public void BuildFtsMatch_ignores_NotTerms_leaves_when_wildcards_in_Not()
    {
        var q = QueryParser.Parse("hello !*.tmp");
        Assert.True(q.HasWildcards);
        Assert.Null(QueryParser.BuildFtsMatch(q));
    }


    // ----- v1.0.19: quoted exact phrases -----

    [Fact]
    public void Quoted_phrase_is_exact_contiguous_match()
    {
        var q = QueryParser.Parse("\"annual report\"");
        Assert.Single(q.Terms);
        Assert.Equal("annual report", q.Terms[0]);
        Assert.DoesNotContain("\"", q.Terms[0]);
        Assert.True(QueryParser.Matches(q, "annual report.pdf", @"C:\docs\annual report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "annual_report.pdf", @"C:\docs\annual_report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "report.xlsx", @"C:\finance\report.xlsx", "xlsx"));
    }

    [Fact]
    public void Quoted_phrase_does_not_require_literal_quote_chars_in_name()
    {
        var q = QueryParser.Parse("\"annual report\"");
        // Filename that happens to contain " must not be required / must not falsely help
        Assert.False(QueryParser.Matches(q, "ann\"ual report.txt", @"C:\docs\ann""ual report.txt", "txt"));
        Assert.True(QueryParser.Matches(q, "annual report.txt", @"C:\docs\annual report.txt", "txt"));
    }

    [Fact]
    public void Unquoted_terms_still_split_And()
    {
        var q = QueryParser.Parse("annual report", MatchMode.And);
        Assert.Equal(2, q.Terms.Count);
        Assert.True(QueryParser.Matches(q, "annual_report.xlsx", @"C:\finance\annual_report.xlsx", "xlsx"));
        Assert.False(QueryParser.Matches(q, "annual.xlsx", @"C:\finance\annual.xlsx", "xlsx"));
    }

    [Fact]
    public void Multiple_quoted_phrases_and_mix_with_unquoted_Not_macro()
    {
        var q = QueryParser.Parse("\"annual report\" budget !draft doc:");
        Assert.Contains("annual report", q.Terms);
        Assert.Contains("budget", q.Terms);
        Assert.Contains("draft", q.NotTerms);
        Assert.Contains("pdf", q.Extensions);
        Assert.True(QueryParser.Matches(q, "annual report budget.pdf", @"C:\docs\annual report budget.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "annual report budget draft.pdf", @"C:\docs\annual report budget draft.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "annual_report budget.pdf", @"C:\docs\annual_report budget.pdf", "pdf"));
    }

    [Fact]
    public void Quoted_phrase_with_path_scope()
    {
        var q = QueryParser.Parse(@"C:\Projects\ ""annual report""");
        Assert.Equal(@"C:\Projects\", q.PathScope);
        Assert.Contains("annual report", q.Terms);
        Assert.True(QueryParser.Matches(q, "annual report.pdf", @"C:\Projects\annual report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "annual report.pdf", @"C:\Other\annual report.pdf", "pdf"));
    }

    [Fact]
    public void Escaped_quote_inside_phrase()
    {
        var q = QueryParser.Parse("\"foo\\\"bar\"");
        Assert.Single(q.Terms);
        Assert.Equal("foo\"bar", q.Terms[0]);
        Assert.True(QueryParser.Matches(q, "foo\"bar.txt", @"C:\docs\foo""bar.txt", "txt"));
        Assert.False(QueryParser.Matches(q, "foobar.txt", @"C:\docs\foobar.txt", "txt"));
    }

    [Fact]
    public void Not_quoted_phrase()
    {
        var q = QueryParser.Parse("report !\"temp file\"");
        Assert.Contains("report", q.Terms);
        Assert.Contains("temp file", q.NotTerms);
        Assert.True(QueryParser.Matches(q, "report.pdf", @"C:\docs\report.pdf", "pdf"));
        Assert.False(QueryParser.Matches(q, "report temp file.pdf", @"C:\docs\report temp file.pdf", "pdf"));
    }

    [Fact]
    public void BuildFtsMatch_null_for_quoted_phrase_with_space()
    {
        var q = QueryParser.Parse("\"annual report\"");
        Assert.Null(QueryParser.BuildFtsMatch(q)); // space → LIKE contiguous phrase
    }

    [Fact]
    public void Extensionless_hosts_matches_hosts_query()
    {
        var q = QueryParser.Parse("hosts");
        Assert.True(QueryParser.Matches(q, "hosts", @"C:\Windows\System32\drivers\etc\hosts", ""));
        Assert.True(QueryParser.Matches(q, "hosts", @"C:\Windows\System32\drivers\etc\hosts", "", matchCase: false));
    }
}
