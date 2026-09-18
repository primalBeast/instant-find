using System.Text;
using System.Text.RegularExpressions;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Parses Instant Find query syntax: substrings, * ? wildcards, optional ext:pdf filters,
/// type macros (doc:/img:/…), size:/dm: filters, Everything-style !NOT terms,
/// double-quoted exact phrases, optional directory path-scope (leading Windows path ending in \),
/// Everything-like path terms (leading \), and optional regex body.
/// </summary>
public sealed class ParsedQuery
{
    public List<string> Terms { get; } = new();
    /// <summary>Exclusion terms (Everything-style <c>!term</c> / <c>!*.tmp</c>).</summary>
    public List<string> NotTerms { get; } = new();
    public List<string> Extensions { get; } = new();
    public bool HasWildcards { get; set; }
    public string Raw { get; init; } = string.Empty;
    public MatchMode Mode { get; init; } = MatchMode.And;

    /// <summary>
    /// Directory scope extracted from a leading Windows path that ends with '\'.
    /// Example: @"C:\Projects\" from "C:\Projects\*.pdf". Null when absent.
    /// </summary>
    public string? PathScope { get; set; }

    /// <summary>True when the UI regex toggle is on; <see cref="RegexPattern"/> holds the body.</summary>
    public bool UseRegex { get; set; }

    /// <summary>Regex body after path-scope stripping. May be empty (path-only listing).</summary>
    public string RegexPattern { get; set; } = string.Empty;

    /// <summary>Minimum size in bytes (inclusive), from <c>size:</c> filters.</summary>
    public long? SizeMin { get; set; }
    /// <summary>Maximum size in bytes (inclusive), from <c>size:</c> filters.</summary>
    public long? SizeMax { get; set; }

    /// <summary>Modified-time lower bound (UTC inclusive), from <c>dm:</c> filters.</summary>
    public DateTime? ModifiedAfterUtc { get; set; }
    /// <summary>Modified-time upper bound (UTC exclusive end-of-range), from <c>dm:</c> filters.</summary>
    public DateTime? ModifiedBeforeUtc { get; set; }

    public bool HasSizeFilter => SizeMin.HasValue || SizeMax.HasValue;
    public bool HasDateFilter => ModifiedAfterUtc.HasValue || ModifiedBeforeUtc.HasValue;
    public bool HasNotTerms => NotTerms.Count > 0;
}

public static class QueryParser
{
    private static readonly Regex ExtFilter = new(
        @"ext:(?<ext>[A-Za-z0-9_+-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>size:&gt;1mb / size:&lt;=100kb / size:1mb..10mb / size:500kb</summary>
    private static readonly Regex SizeFilter = new(
        @"\bsize:(?<spec>[^\s""]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>dm:today / dm:thisweek / dm:&gt;2024-01-01 / dm:2024-01-01..2024-12-31</summary>
    private static readonly Regex DateModifiedFilter = new(
        @"\bdm:(?<spec>[^\s""]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Leading Windows drive path whose last segment ends with '\'.
    /// Segments may contain internal spaces (Program Files) but must not
    /// end with whitespace before '\' — so "C:\Logs  \72" is not absorbed
    /// as scope "C:\Logs  \".
    /// </summary>
    private static readonly Regex PathScopeRegex = new(
        // Segment = non-ws tokens separated by spaces, then '\' (no trailing ws before '\').
        @"^(?<scope>[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n\s]+(?:\s+[^\\/:*?""<>|\r\n\s]+)*\\)*)(?<rest>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>X:\dir</c> + whitespace + path term <c>\rest</c> → scope <c>X:\dir\</c>
    /// even when the directory has no trailing '\'.
    /// Example: "C:\Logs  \72" → scope=C:\Logs\, rest=\72.
    /// </summary>
    private static readonly Regex ImplicitPathScopeWithPathTermRegex = new(
        @"^(?<dir>[A-Za-z]:(?:\\[^\\/:*?""<>|\r\n\s]+(?:\s+[^\\/:*?""<>|\r\n\s]+)*)+)(\s+)(?<rest>\\.+)$",
        RegexOptions.Compiled);

    public static ParsedQuery Parse(string? input, MatchMode mode = MatchMode.And, bool useRegex = false)
    {
        var result = new ParsedQuery { Raw = input ?? string.Empty, Mode = mode };
        if (string.IsNullOrWhiteSpace(input))
            return result;

        var working = input.Trim();

        if (TryExtractPathScope(working, out var scope, out var remainder))
        {
            result.PathScope = scope;
            working = remainder.TrimStart();
        }

        // size: / dm: always extracted (including regex mode) so Filter popup tokens work with .*
        working = ExtractSizeFilters(working, result);
        working = ExtractDateFilters(working, result);

        if (useRegex)
        {
            result.UseRegex = true;
            // Expand type macros out of the regex body so doc: still filters by extension
            working = ExpandMacros(working, result);
            result.RegexPattern = working.Trim();
            return result;
        }

        foreach (Match m in ExtFilter.Matches(working))
        {
            var ext = m.Groups["ext"].Value.TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && !result.Extensions.Contains(ext))
                result.Extensions.Add(ext);
        }

        working = ExtFilter.Replace(working, " ").Trim();
        working = ExpandMacros(working, result);

        if (mode == MatchMode.LiteralWhitespace)
        {
            // Do not split on spaces — the whole remainder is one term (spaces must appear in the name).
            // Leading ! still means NOT for the whole literal term.
            if (!string.IsNullOrEmpty(working))
            {
                if (working.StartsWith('!') && working.Length > 1)
                {
                    var notTerm = working[1..];
                    if (notTerm.Contains('*') || notTerm.Contains('?'))
                        result.HasWildcards = true;
                    result.NotTerms.Add(notTerm);
                }
                else
                {
                    if (working.Contains('*') || working.Contains('?'))
                        result.HasWildcards = true;
                    result.Terms.Add(working);
                }
            }
            return result;
        }

        foreach (var token in Tokenize(working))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (token.StartsWith('!') && token.Length > 1)
            {
                var notTerm = token[1..];
                if (notTerm.Contains('*') || notTerm.Contains('?'))
                    result.HasWildcards = true;
                result.NotTerms.Add(notTerm);
                continue;
            }

            if (token.Contains('*') || token.Contains('?'))
                result.HasWildcards = true;
            result.Terms.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Extracts a directory scope when the query starts with a Windows path whose
    /// scoped prefix ends with '\'. Bradley rule: only scope when that prefix ends with '\',
    /// except when a path without trailing '\' is followed by whitespace and an
    /// Everything-like path term (<c>\72</c>) — then scope the directory and keep the path term.
    /// <c>C:\Projects</c> → no scope (normal FTS term);
    /// <c>C:\Projects\</c> → scope; <c>C:\Projects\foo</c> → scope <c>C:\Projects\</c>, rest <c>foo</c>;
    /// <c>C:\Logs  \72</c> → scope <c>C:\Logs\</c>, rest <c>\72</c>;
    /// <c>C:\</c> alone → scope OK; <c>C:\*.pdf</c> → scope <c>C:\</c>.
    /// </summary>
    public static bool TryExtractPathScope(string input, out string scope, out string remainder)
    {
        scope = string.Empty;
        remainder = input;
        if (string.IsNullOrEmpty(input))
            return false;

        // Prefer: C:\Logs  \72 → scope C:\Logs\ + path term \72 (no trailing \ required on dir).
        var impl = ImplicitPathScopeWithPathTermRegex.Match(input);
        if (impl.Success)
        {
            scope = impl.Groups["dir"].Value + "\\";
            remainder = impl.Groups["rest"].Value;
            return true;
        }

        var m = PathScopeRegex.Match(input);
        if (!m.Success)
            return false;

        scope = m.Groups["scope"].Value;
        // Require a real backslash after the drive (C:\ at minimum)
        if (scope.Length < 3)
            return false;

        remainder = m.Groups["rest"].Value;

        // Reject false positive: "C:\Projects" → scope=C:\ + rest=Projects.
        // Drive-root scope with a bare path segment (no \, wildcards, or leading space)
        // means the user typed a path-like FTS term, not a directory scope.
        if (scope.Length == 3 && remainder.Length > 0)
        {
            bool intentional =
                remainder.Contains('\\')
                || remainder.Contains('/')
                || remainder.Contains('*')
                || remainder.Contains('?')
                || remainder[0] is ' ' or '\t';
            if (!intentional)
            {
                scope = string.Empty;
                remainder = input;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is the scoped directory or a descendant.
    /// </summary>
    public static bool IsUnderPathScope(string fullPath, string pathScope, bool matchCase)
    {
        if (string.IsNullOrEmpty(pathScope) || string.IsNullOrEmpty(fullPath))
            return false;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var trimmed = pathScope.TrimEnd('\\', '/');
        if (fullPath.Equals(trimmed, comparison))
            return true;

        var prefix = trimmed + "\\";
        return fullPath.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Returns true if the filename/path matches the parsed query (in-memory matching for tests / fallback).
    /// Regex mode: .NET regex against the filename first; also tries full path when the pattern
    /// contains a path separator (same idea as path-shaped wildcards).
    /// </summary>
    public static bool Matches(
        ParsedQuery query,
        string name,
        string fullPath,
        string extension,
        bool matchCase = false,
        bool wholeWord = false,
        long size = 0,
        DateTime? modifiedUtc = null,
        bool isDirectory = false)
    {
        if (!string.IsNullOrEmpty(query.PathScope)
            && !IsUnderPathScope(fullPath, query.PathScope, matchCase))
            return false;

        if (!PassesSizeFilter(query, size, isDirectory))
            return false;

        if (!PassesDateFilter(query, modifiedUtc))
            return false;

        if (query.UseRegex)
        {
            if (!MatchesRegex(query, name, fullPath, extension, matchCase))
                return false;
            return !IsExcludedByNotTerms(query, name, fullPath, matchCase, wholeWord);
        }

        if (query.Extensions.Count > 0)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (!query.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Path-scope / ext / size / date only (no positive terms): everything under filters matches
        if (query.Terms.Count == 0)
        {
            bool structural =
                query.Extensions.Count > 0
                || !string.IsNullOrEmpty(query.PathScope)
                || query.HasSizeFilter
                || query.HasDateFilter
                || query.HasNotTerms
                || string.IsNullOrWhiteSpace(query.Raw);
            if (!structural)
                return false;
            return !IsExcludedByNotTerms(query, name, fullPath, matchCase, wholeWord);
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        bool positivesOk;
        if (query.Mode == MatchMode.Or)
        {
            positivesOk = false;
            foreach (var term in query.Terms)
            {
                if (TermMatches(term, name, fullPath, comparison, wholeWord))
                {
                    positivesOk = true;
                    break;
                }
            }
        }
        else
        {
            // And and LiteralWhitespace: every term must match
            positivesOk = true;
            foreach (var term in query.Terms)
            {
                if (!TermMatches(term, name, fullPath, comparison, wholeWord))
                {
                    positivesOk = false;
                    break;
                }
            }
        }

        if (!positivesOk)
            return false;

        return !IsExcludedByNotTerms(query, name, fullPath, matchCase, wholeWord);
    }

    /// <summary>True when any <see cref="ParsedQuery.NotTerms"/> matches the file (should be excluded).</summary>
    public static bool IsExcludedByNotTerms(
        ParsedQuery query,
        string name,
        string fullPath,
        bool matchCase = false,
        bool wholeWord = false)
    {
        if (query.NotTerms.Count == 0)
            return false;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var term in query.NotTerms)
        {
            if (TermMatches(term, name, fullPath, comparison, wholeWord))
                return true;
        }
        return false;
    }

    public static bool PassesSizeFilter(ParsedQuery query, long size, bool isDirectory)
    {
        if (!query.HasSizeFilter)
            return true;
        // Directories have size 0 in the index — size filters apply to files only
        if (isDirectory)
            return false;
        if (query.SizeMin.HasValue && size < query.SizeMin.Value)
            return false;
        if (query.SizeMax.HasValue && size > query.SizeMax.Value)
            return false;
        return true;
    }

    public static bool PassesDateFilter(ParsedQuery query, DateTime? modifiedUtc)
    {
        if (!query.HasDateFilter)
            return true;
        if (modifiedUtc is null)
            return false;
        var m = modifiedUtc.Value;
        if (m.Kind == DateTimeKind.Unspecified)
            m = DateTime.SpecifyKind(m, DateTimeKind.Utc);
        else if (m.Kind == DateTimeKind.Local)
            m = m.ToUniversalTime();

        if (query.ModifiedAfterUtc.HasValue && m < query.ModifiedAfterUtc.Value)
            return false;
        if (query.ModifiedBeforeUtc.HasValue && m >= query.ModifiedBeforeUtc.Value)
            return false;
        return true;
    }

    private static bool MatchesRegex(
        ParsedQuery query,
        string name,
        string fullPath,
        string extension,
        bool matchCase)
    {
        if (query.Extensions.Count > 0)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (!query.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Empty regex body with path scope → list everything under scope
        if (string.IsNullOrEmpty(query.RegexPattern))
            return !string.IsNullOrEmpty(query.PathScope);

        if (!TryCompileRegex(query.RegexPattern, matchCase, out var rx) || rx is null)
            return false;

        // Prefer filename first (unanchored IsMatch; ^/$ in the pattern still anchors)
        if (rx.IsMatch(name))
            return true;
        if (rx.IsMatch(System.IO.Path.GetFileName(fullPath)))
            return true;

        // Path patterns only when the regex itself contains a path separator
        if (query.RegexPattern.Contains('\\') || query.RegexPattern.Contains('/'))
            return rx.IsMatch(fullPath);

        return false;
    }

    /// <summary>
    /// Compiles a .NET regex, or if that fails, converts shell-glob (* ?) to regex and retries.
    /// Returns false when neither form is valid. Empty pattern → true with <c>regex == null</c>.
    /// </summary>
    public static bool TryCompileRegex(string pattern, bool matchCase, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrEmpty(pattern))
            return true;

        var options = RegexOptions.CultureInvariant;
        if (!matchCase)
            options |= RegexOptions.IgnoreCase;

        try
        {
            regex = new Regex(pattern, options);
            return true;
        }
        catch (ArgumentException)
        {
            // fall through to shell-glob conversion when appropriate
        }

        // Only treat as shell-glob when * or ? appear and the pattern does not look like
        // intentional (broken) regex grouping — so "(?" stays Invalid regex, while "*.pdf" works.
        bool looksLikeGlob =
            (pattern.Contains('*') || pattern.Contains('?'))
            && pattern.IndexOfAny(new[] { '(', '[', '{' }) < 0;
        if (!looksLikeGlob)
        {
            regex = null;
            return false;
        }

        try
        {
            regex = new Regex(ShellGlobToRegex(pattern), options);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    /// <summary>True when the pattern compiles as regex or as shell-glob→regex.</summary>
    public static bool IsRegexPatternValid(string? pattern, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(pattern))
            return true;
        return TryCompileRegex(pattern, matchCase, out _);
    }

    /// <summary>
    /// Shell-glob → regex: * → .*, ? → ., escape other regex metacharacters.
    /// Intended for unanchored <see cref="Regex.IsMatch(string)"/> against the filename.
    /// </summary>
    public static string ShellGlobToRegex(string glob)
    {
        var sb = new StringBuilder(glob.Length * 2);
        foreach (var c in glob)
        {
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Longest contiguous alphanumeric run in a regex/glob pattern for SQL LIKE prefilter.
    /// Returns null when no alphanumeric run exists.
    /// </summary>
    public static string? TryExtractLongestLiteral(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        string? best = null;
        var startIdx = -1;
        for (int i = 0; i <= pattern.Length; i++)
        {
            bool alnum = i < pattern.Length && char.IsLetterOrDigit(pattern[i]);
            if (alnum)
            {
                if (startIdx < 0) startIdx = i;
            }
            else if (startIdx >= 0)
            {
                var len = i - startIdx;
                if (best is null || len > best.Length)
                    best = pattern.Substring(startIdx, len);
                startIdx = -1;
            }
        }
        return best;
    }

    public static bool TermMatches(string term, string name, string fullPath)
        => TermMatches(term, name, fullPath, StringComparison.OrdinalIgnoreCase, wholeWord: false);

    public static bool TermMatches(
        string term,
        string name,
        string fullPath,
        StringComparison comparison,
        bool wholeWord)
    {
        // Everything-like \72: path/directory segment starts with rest — never bare-name FTS.
        if (IsPathTerm(term))
            return PathTermMatches(term, fullPath, comparison, wholeWord);

        if (term.Contains('*') || term.Contains('?'))
        {
            var options = comparison == StringComparison.Ordinal
                ? RegexOptions.CultureInvariant
                : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var pattern = "^" + Regex.Escape(term)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            // Shell wildcards apply to the filename — do not let D*.pdf match via a folder like \\docs\\
            if (Regex.IsMatch(name, pattern, options))
                return true;
            if (Regex.IsMatch(System.IO.Path.GetFileName(fullPath), pattern, options))
                return true;
            // Path patterns only when the term itself contains a path separator
            if (term.Contains('\\') || term.Contains('/'))
            {
                var pathPattern = Regex.Escape(term)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".");
                return Regex.IsMatch(fullPath, pathPattern, options);
            }
            return false;
        }

        if (wholeWord)
            return WholeWordMatches(term, name, fullPath, comparison);

        return fullPath.Contains(term, comparison)
               || name.Contains(term, comparison);
    }

    /// <summary>
    /// Path term <c>\rest</c>: true when any path segment starts with <c>rest</c>
    /// (Whole Word → segment equals <c>rest</c>). Supports * ? in <c>rest</c>.
    /// </summary>
    private static bool PathTermMatches(
        string term,
        string fullPath,
        StringComparison comparison,
        bool wholeWord)
    {
        var rest = PathTermRest(term);
        if (rest.Length == 0 || string.IsNullOrEmpty(fullPath))
            return false;

        // Multi-segment fragment (\foo\bar): require the literal path piece in fullPath
        if ((rest.Contains('\\') || rest.Contains('/')) && !(rest.Contains('*') || rest.Contains('?')))
            return fullPath.Contains(term, comparison);

        bool hasWild = rest.Contains('*') || rest.Contains('?');
        Regex? wildRx = null;
        if (hasWild)
        {
            var options = comparison == StringComparison.Ordinal
                ? RegexOptions.CultureInvariant
                : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var body = Regex.Escape(rest).Replace("\\*", ".*").Replace("\\?", ".");
            // Segment prefix: ^rest*  (or exact when wholeWord and no trailing wildcard intent)
            wildRx = new Regex("^" + body, options);
        }

        foreach (var segment in fullPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (hasWild)
            {
                if (wildRx!.IsMatch(segment))
                    return true;
                continue;
            }

            if (wholeWord)
            {
                if (string.Equals(segment, rest, comparison))
                    return true;
            }
            else if (segment.StartsWith(rest, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WholeWordMatches(string term, string name, string fullPath, StringComparison comparison)
    {
        if (ContainsWholeWord(name, term, comparison))
            return true;
        if (ContainsWholeWord(fullPath, term, comparison))
            return true;

        // Filename stem (report.pdf → report)
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (!string.IsNullOrEmpty(stem) && string.Equals(stem, term, comparison))
            return true;

        // Path segment equality (e.g. term "docs" matches C:\docs\file.txt)
        foreach (var segment in fullPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, term, comparison))
                return true;
            var segStem = System.IO.Path.GetFileNameWithoutExtension(segment);
            if (!string.IsNullOrEmpty(segStem) && string.Equals(segStem, term, comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="term"/> appears as a whole word in <paramref name="haystack"/>
    /// (bounded by start/end or non-letter/digit characters).
    /// </summary>
    private static bool ContainsWholeWord(string haystack, string term, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(term))
            return false;

        int start = 0;
        while (start <= haystack.Length - term.Length)
        {
            var idx = haystack.IndexOf(term, start, comparison);
            if (idx < 0) return false;

            bool leftOk = idx == 0 || !IsWordChar(haystack[idx - 1]);
            int end = idx + term.Length;
            bool rightOk = end == haystack.Length || !IsWordChar(haystack[end]);
            if (leftOk && rightOk)
                return true;

            start = idx + 1;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Converts a shell wildcard term (* ?) to a SQL LIKE pattern.
    /// Escapes %, _, and \ in literal parts; maps * → %, ? → _.
    /// </summary>
    public static string ShellWildcardToLike(string term)
    {
        var sb = new StringBuilder(term.Length);
        foreach (var c in term)
        {
            switch (c)
            {
                case '*':
                    sb.Append('%');
                    break;
                case '?':
                    sb.Append('_');
                    break;
                case '%':
                case '_':
                case '\\':
                    sb.Append('\\');
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts a plain (non-wildcard) term to a substring LIKE pattern (%term%).
    /// </summary>
    public static string PlainTermToLike(string term)
    {
        return "%" + EscapeLikeLiteral(term) + "%";
    }

    /// <summary>
    /// Escapes SQL LIKE metacharacters in a literal string (no wildcard conversion).
    /// </summary>
    public static string EscapeLikeLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }


    /// <summary>
    /// Everything-like path term: starts with '\' (and is not empty after it).
    /// Matches a path/directory <em>segment</em> that starts with the rest after '\',
    /// never bare-name FTS that ignores '\'.
    /// Drive path-scope (<c>C:\foo\</c>) is separate — see <see cref="TryExtractPathScope"/>.
    /// </summary>
    public static bool IsPathTerm(string term)
        => term.Length >= 2 && term[0] == '\\';

    /// <summary>Text after the leading '\' of a path term; empty if not a path term.</summary>
    public static string PathTermRest(string term)
        => IsPathTerm(term) ? term.Substring(1) : string.Empty;

    /// <summary>
    /// SQL LIKE pattern for a path term: %\rest% with LIKE metacharacters escaped.
    /// Used against <c>path</c> only (not bare <c>name</c>).
    /// </summary>
    public static string PathTermToLike(string term)
    {
        if (!IsPathTerm(term))
            return PlainTermToLike(term);
        // Keep the leading '\' so the match is always after a separator / drive root.
        return PlainTermToLike(term);
    }

    /// <summary>
    /// True when a plain term contains characters that FTS5 unicode61 treats as token
    /// separators (e.g. '_' / '-'). Those queries must use LIKE so literals are preserved.
    /// Letters and digits are FTS-safe; '*'/'?' already leave via <see cref="ParsedQuery.HasWildcards"/>.
    /// </summary>
    public static bool TermHasFtsTokenSeparators(string term)
    {
        foreach (var c in term)
        {
            if (char.IsLetterOrDigit(c))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds an FTS5 MATCH expression for plain (non-wildcard) substring terms only.
    /// Returns null when the query has wildcards, regex, FTS-splitting punctuation
    /// (e.g. '_' / '-'), or no usable terms — callers should use LIKE instead.
    /// Match Case / Whole Word / Regex must leave FTS (caller responsibility).
    /// </summary>
    public static string? BuildFtsMatch(ParsedQuery query)
    {
        if (query.UseRegex || query.HasWildcards)
            return null;

        if (query.Terms.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var term in query.Terms)
        {
            if (term.Length == 0)
                continue;

            // Everything-like \72 → path segment match; never bare-name FTS that ignores '\'
            if (IsPathTerm(term))
                return null;

            // unicode61 splits on '_' / '-' / other punctuation → token too loose (e.g. "72_" → "72")
            if (TermHasFtsTokenSeparators(term))
                return null;

            parts.Add(EscapeFtsToken(term) + "*");
        }

        if (parts.Count == 0)
            return null;

        var joiner = query.Mode == MatchMode.Or ? " OR " : " AND ";
        return string.Join(joiner, parts);
    }

    private static string EscapeFtsToken(string token)
    {
        // FTS5: wrap in double quotes and escape internal quotes
        var cleaned = token.Replace("\"", "\"\"");
        // Remove characters that break FTS tokenization badly
        var sb = new StringBuilder(cleaned.Length);
        foreach (var c in cleaned)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        return $"\"{sb}\"";
    }

    /// <summary>Strip and apply <c>size:</c> tokens into <paramref name="query"/>.</summary>
    public static string ExtractSizeFilters(string working, ParsedQuery query)
    {
        foreach (Match m in SizeFilter.Matches(working))
        {
            if (TryParseSizeSpec(m.Groups["spec"].Value, out var min, out var max))
            {
                if (min.HasValue)
                    query.SizeMin = query.SizeMin.HasValue ? Math.Max(query.SizeMin.Value, min.Value) : min;
                if (max.HasValue)
                    query.SizeMax = query.SizeMax.HasValue ? Math.Min(query.SizeMax.Value, max.Value) : max;
            }
        }
        return SizeFilter.Replace(working, " ").Trim();
    }

    /// <summary>Strip and apply <c>dm:</c> tokens into <paramref name="query"/>.</summary>
    public static string ExtractDateFilters(string working, ParsedQuery query)
    {
        foreach (Match m in DateModifiedFilter.Matches(working))
        {
            if (TryParseDateSpec(m.Groups["spec"].Value, out var after, out var before))
            {
                if (after.HasValue)
                    query.ModifiedAfterUtc = query.ModifiedAfterUtc.HasValue
                        ? (after > query.ModifiedAfterUtc ? after : query.ModifiedAfterUtc)
                        : after;
                if (before.HasValue)
                    query.ModifiedBeforeUtc = query.ModifiedBeforeUtc.HasValue
                        ? (before < query.ModifiedBeforeUtc ? before : query.ModifiedBeforeUtc)
                        : before;
            }
        }
        return DateModifiedFilter.Replace(working, " ").Trim();
    }

    /// <summary>Expand <c>doc:</c>/<c>img:</c>/… macros into <see cref="ParsedQuery.Extensions"/>.</summary>
    public static string ExpandMacros(string working, ParsedQuery query)
    {
        if (string.IsNullOrWhiteSpace(working))
            return working ?? string.Empty;

        var sb = new StringBuilder();
        foreach (var token in Tokenize(working))
        {
            if (FileTypeMacros.TryResolveMacro(token, out var group) && group is not null)
            {
                foreach (var ext in group.Extensions)
                {
                    if (!query.Extensions.Contains(ext))
                        query.Extensions.Add(ext);
                }
                continue;
            }
            if (sb.Length > 0) sb.Append(' ');
            // Re-quote tokens that contain spaces
            if (token.Contains(' '))
            {
                sb.Append('"');
                sb.Append(token);
                sb.Append('"');
            }
            else
            {
                sb.Append(token);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Parses size specs: <c>&gt;1mb</c>, <c>&lt;=100kb</c>, <c>1mb..10mb</c>, <c>500kb</c>.
    /// Units: b, kb, mb, gb (case-insensitive). Binary (1024) multipliers.
    /// </summary>
    public static bool TryParseSizeSpec(string? spec, out long? minBytes, out long? maxBytes)
    {
        minBytes = null;
        maxBytes = null;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        var s = spec.Trim();
        // Range: a..b
        var dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var left = s[..dots].Trim().TrimStart('>', '=');
            var right = s[(dots + 2)..].Trim().TrimStart('<', '=');
            bool ok = false;
            if (left.Length > 0 && TryParseSizeValue(left, out var lo))
            {
                minBytes = lo;
                ok = true;
            }
            if (right.Length > 0 && TryParseSizeValue(right, out var hi))
            {
                maxBytes = hi;
                ok = true;
            }
            return ok;
        }

        // Comparison operators
        if (s.StartsWith(">=", StringComparison.Ordinal))
        {
            if (!TryParseSizeValue(s[2..], out var v)) return false;
            minBytes = v;
            return true;
        }
        if (s.StartsWith("<=", StringComparison.Ordinal))
        {
            if (!TryParseSizeValue(s[2..], out var v)) return false;
            maxBytes = v;
            return true;
        }
        if (s.StartsWith('>'))
        {
            if (!TryParseSizeValue(s[1..], out var v)) return false;
            minBytes = v + 1; // exclusive >
            return true;
        }
        if (s.StartsWith('<'))
        {
            if (!TryParseSizeValue(s[1..], out var v)) return false;
            maxBytes = Math.Max(0, v - 1); // exclusive <
            return true;
        }

        // Bare value → exact-ish: treat as min=max
        if (!TryParseSizeValue(s, out var exact))
            return false;
        minBytes = exact;
        maxBytes = exact;
        return true;
    }

    public static bool TryParseSizeValue(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim().Replace("_", "").Replace(",", "");
        // Split number + optional unit
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            i++;
        if (i == 0)
            return false;
        if (!double.TryParse(s[..i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
            return false;
        var unit = s[i..].Trim().ToLowerInvariant();
        double mult = unit switch
        {
            "" or "b" or "byte" or "bytes" => 1,
            "k" or "kb" or "kib" => 1024,
            "m" or "mb" or "mib" => 1024d * 1024,
            "g" or "gb" or "gib" => 1024d * 1024 * 1024,
            "t" or "tb" or "tib" => 1024d * 1024 * 1024 * 1024,
            _ => -1
        };
        if (mult < 0)
            return false;
        bytes = (long)Math.Round(num * mult);
        if (bytes < 0) bytes = 0;
        return true;
    }

    /// <summary>
    /// Parses dm: specs: today, yesterday, thisweek, thismonth, thisyear,
    /// &gt;YYYY-MM-DD, &lt;YYYY-MM-DD, YYYY-MM-DD..YYYY-MM-DD.
    /// Bounds are UTC based on local calendar days.
    /// </summary>
    public static bool TryParseDateSpec(string? spec, out DateTime? afterUtc, out DateTime? beforeUtc)
    {
        afterUtc = null;
        beforeUtc = null;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        var s = spec.Trim();
        var nowLocal = DateTime.Now;
        var todayLocal = nowLocal.Date;

        switch (s.ToLowerInvariant())
        {
            case "today":
                afterUtc = todayLocal.ToUniversalTime();
                beforeUtc = todayLocal.AddDays(1).ToUniversalTime();
                return true;
            case "yesterday":
                afterUtc = todayLocal.AddDays(-1).ToUniversalTime();
                beforeUtc = todayLocal.ToUniversalTime();
                return true;
            case "thisweek":
            {
                int diff = ((int)todayLocal.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                var monday = todayLocal.AddDays(-diff);
                afterUtc = monday.ToUniversalTime();
                beforeUtc = todayLocal.AddDays(1).ToUniversalTime();
                return true;
            }
            case "thismonth":
                afterUtc = new DateTime(todayLocal.Year, todayLocal.Month, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
                beforeUtc = todayLocal.AddDays(1).ToUniversalTime();
                return true;
            case "thisyear":
                afterUtc = new DateTime(todayLocal.Year, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
                beforeUtc = todayLocal.AddDays(1).ToUniversalTime();
                return true;
        }

        var dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var left = s[..dots].Trim().TrimStart('>', '=');
            var right = s[(dots + 2)..].Trim().TrimStart('<', '=');
            bool ok = false;
            if (left.Length > 0 && TryParseDateValue(left, out var lo))
            {
                afterUtc = lo;
                ok = true;
            }
            if (right.Length > 0 && TryParseDateValue(right, out var hi))
            {
                beforeUtc = hi.AddDays(1); // inclusive end date
                ok = true;
            }
            return ok;
        }

        if (s.StartsWith(">=", StringComparison.Ordinal))
        {
            if (!TryParseDateValue(s[2..], out var d)) return false;
            afterUtc = d;
            return true;
        }
        if (s.StartsWith("<=", StringComparison.Ordinal))
        {
            if (!TryParseDateValue(s[2..], out var d)) return false;
            beforeUtc = d.AddDays(1);
            return true;
        }
        if (s.StartsWith('>'))
        {
            if (!TryParseDateValue(s[1..], out var d)) return false;
            afterUtc = d.AddDays(1);
            return true;
        }
        if (s.StartsWith('<'))
        {
            if (!TryParseDateValue(s[1..], out var d)) return false;
            beforeUtc = d;
            return true;
        }

        // Bare date → that calendar day
        if (!TryParseDateValue(s, out var day))
            return false;
        afterUtc = day;
        beforeUtc = day.AddDays(1);
        return true;
    }

    public static bool TryParseDateValue(string? text, out DateTime utcMidnightFromLocalDate)
    {
        utcMidnightFromLocalDate = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var localDate))
        {
            utcMidnightFromLocalDate = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local).ToUniversalTime();
            return true;
        }
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            utcMidnightFromLocalDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Local).ToUniversalTime();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Formats Windows <see cref="System.IO.FileAttributes"/> as compact letters (R/H/S/A/D/C/E/L).
    /// </summary>
    public static string FormatAttributes(System.IO.FileAttributes attrs)
    {
        var sb = new StringBuilder(8);
        if ((attrs & System.IO.FileAttributes.ReadOnly) != 0) sb.Append('R');
        if ((attrs & System.IO.FileAttributes.Hidden) != 0) sb.Append('H');
        if ((attrs & System.IO.FileAttributes.System) != 0) sb.Append('S');
        if ((attrs & System.IO.FileAttributes.Archive) != 0) sb.Append('A');
        if ((attrs & System.IO.FileAttributes.Directory) != 0) sb.Append('D');
        if ((attrs & System.IO.FileAttributes.Compressed) != 0) sb.Append('C');
        if ((attrs & System.IO.FileAttributes.Encrypted) != 0) sb.Append('E');
        if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0) sb.Append('L');
        return sb.ToString();
    }

    /// <summary>
    /// Splits on whitespace outside double quotes. Quote characters are delimiters only
    /// (Everything-style exact phrase) — they are never kept as literal search characters.
    /// Use <c>\"</c> inside a quoted phrase to include a literal double-quote in the term.
    /// </summary>
    private static IEnumerable<string> Tokenize(string input)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            // Escaped quote → literal " (inside or outside phrases)
            if (c == '\\' && i + 1 < input.Length && input[i + 1] == '"')
            {
                sb.Append('"');
                i++;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }
}
