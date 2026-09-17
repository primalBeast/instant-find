using System.Text;
using System.Text.RegularExpressions;
using InstantFind.Models;

namespace InstantFind.Services;

/// <summary>
/// Parses Instant Find query syntax: substrings, * ? wildcards, optional ext:pdf filters,
/// optional directory path-scope (leading Windows path ending in \), and optional regex body.
/// </summary>
public sealed class ParsedQuery
{
    public List<string> Terms { get; } = new();
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
}

public static class QueryParser
{
    private static readonly Regex ExtFilter = new(
        @"ext:(?<ext>[A-Za-z0-9_+-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Leading Windows drive path whose last segment ends with '\'.
    /// Captures valid path segments only (* ? " &lt; &gt; | excluded) so
    /// "C:\Projects\*.pdf" → scope=C:\Projects\, rest=*.pdf.
    /// </summary>
    private static readonly Regex PathScopeRegex = new(
        @"^(?<scope>[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*)(?<rest>.*)$",
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

        if (useRegex)
        {
            result.UseRegex = true;
            result.RegexPattern = working;
            // Still allow ext: filters alongside regex body
            foreach (Match m in ExtFilter.Matches(working))
            {
                var ext = m.Groups["ext"].Value.TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrEmpty(ext) && !result.Extensions.Contains(ext))
                    result.Extensions.Add(ext);
            }
            // Regex body keeps ext: text as part of the pattern unless we strip it —
            // prefer leaving the full remainder as the regex (ext: is for non-regex mode).
            // Clear extensions when useRegex so ext: is literal in the pattern.
            result.Extensions.Clear();
            return result;
        }

        foreach (Match m in ExtFilter.Matches(working))
        {
            var ext = m.Groups["ext"].Value.TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && !result.Extensions.Contains(ext))
                result.Extensions.Add(ext);
        }

        working = ExtFilter.Replace(working, " ").Trim();

        if (mode == MatchMode.LiteralWhitespace)
        {
            // Do not split on spaces — the whole remainder is one term (spaces must appear in the name).
            if (!string.IsNullOrEmpty(working))
            {
                if (working.Contains('*') || working.Contains('?'))
                    result.HasWildcards = true;
                result.Terms.Add(working);
            }
            return result;
        }

        foreach (var token in Tokenize(working))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (token.Contains('*') || token.Contains('?'))
                result.HasWildcards = true;
            result.Terms.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Extracts a directory scope when the query starts with a Windows path ending in '\'.
    /// </summary>
    public static bool TryExtractPathScope(string input, out string scope, out string remainder)
    {
        scope = string.Empty;
        remainder = input;
        if (string.IsNullOrEmpty(input))
            return false;

        var m = PathScopeRegex.Match(input);
        if (!m.Success)
            return false;

        scope = m.Groups["scope"].Value;
        // Require a real backslash after the drive (C:\ at minimum)
        if (scope.Length < 3)
            return false;

        remainder = m.Groups["rest"].Value;
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
        bool wholeWord = false)
    {
        if (!string.IsNullOrEmpty(query.PathScope)
            && !IsUnderPathScope(fullPath, query.PathScope, matchCase))
            return false;

        if (query.UseRegex)
            return MatchesRegex(query, name, fullPath, extension, matchCase);

        if (query.Extensions.Count > 0)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (!query.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Path-scope only (no terms): everything under the directory matches
        if (query.Terms.Count == 0)
            return query.Extensions.Count > 0
                   || !string.IsNullOrEmpty(query.PathScope)
                   || string.IsNullOrWhiteSpace(query.Raw);

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (query.Mode == MatchMode.Or)
        {
            foreach (var term in query.Terms)
            {
                if (TermMatches(term, name, fullPath, comparison, wholeWord))
                    return true;
            }
            return false;
        }

        // And and LiteralWhitespace: every term must match
        foreach (var term in query.Terms)
        {
            if (!TermMatches(term, name, fullPath, comparison, wholeWord))
                return false;
        }

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

        Regex rx;
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (!matchCase)
                options |= RegexOptions.IgnoreCase;
            rx = new Regex(query.RegexPattern, options);
        }
        catch (ArgumentException)
        {
            return false; // invalid pattern → no match
        }

        // Prefer filename first (same spirit as shell wildcards)
        if (rx.IsMatch(name))
            return true;
        if (rx.IsMatch(System.IO.Path.GetFileName(fullPath)))
            return true;

        // Path patterns only when the regex itself contains a path separator
        if (query.RegexPattern.Contains('\\') || query.RegexPattern.Contains('/'))
            return rx.IsMatch(fullPath);

        return false;
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
    /// Builds an FTS5 MATCH expression for plain (non-wildcard) substring terms only.
    /// Returns null when the query has wildcards, regex, or no usable terms — callers should use LIKE instead.
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

    private static IEnumerable<string> Tokenize(string input)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in input)
        {
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
