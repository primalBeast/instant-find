using System.Text;
using System.Text.RegularExpressions;

namespace InstantFind.Services;

/// <summary>
/// Parses Instant Find query syntax: substrings, * ? wildcards, and optional ext:pdf filters.
/// </summary>
public sealed class ParsedQuery
{
    public List<string> Terms { get; } = new();
    public List<string> Extensions { get; } = new();
    public bool HasWildcards { get; set; }
    public string Raw { get; init; } = string.Empty;
}

public static class QueryParser
{
    private static readonly Regex ExtFilter = new(
        @"ext:(?<ext>[A-Za-z0-9_+-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedQuery Parse(string? input)
    {
        var result = new ParsedQuery { Raw = input ?? string.Empty };
        if (string.IsNullOrWhiteSpace(input))
            return result;

        var working = input.Trim();

        foreach (Match m in ExtFilter.Matches(working))
        {
            var ext = m.Groups["ext"].Value.TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && !result.Extensions.Contains(ext))
                result.Extensions.Add(ext);
        }

        working = ExtFilter.Replace(working, " ").Trim();

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
    /// Returns true if the filename/path matches the parsed query (in-memory matching for tests / fallback).
    /// </summary>
    public static bool Matches(ParsedQuery query, string name, string fullPath, string extension)
    {
        if (query.Extensions.Count > 0)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (!query.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (query.Terms.Count == 0)
            return query.Extensions.Count > 0 || string.IsNullOrWhiteSpace(query.Raw);

        var haystack = fullPath;
        foreach (var term in query.Terms)
        {
            if (!TermMatches(term, name, haystack))
                return false;
        }

        return true;
    }

    public static bool TermMatches(string term, string name, string fullPath)
    {
        if (term.Contains('*') || term.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(term)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            // Shell wildcards apply to the filename — do not let D*.pdf match via a folder like \\docs\\
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            if (Regex.IsMatch(System.IO.Path.GetFileName(fullPath), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            // Path patterns only when the term itself contains a path separator
            if (term.Contains('\\') || term.Contains('/'))
            {
                var pathPattern = Regex.Escape(term)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".");
                return Regex.IsMatch(fullPath, pathPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return false;
        }

        return fullPath.Contains(term, StringComparison.OrdinalIgnoreCase)
               || name.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

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
    /// Returns null when the query has wildcards or no usable terms — callers should use LIKE instead.
    /// </summary>
    public static string? BuildFtsMatch(ParsedQuery query)
    {
        if (query.HasWildcards)
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

        return parts.Count == 0 ? null : string.Join(" AND ", parts);
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
