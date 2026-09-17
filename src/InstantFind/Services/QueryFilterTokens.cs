using System.Text;
using System.Text.RegularExpressions;

namespace InstantFind.Services;

/// <summary>
/// Helpers for injecting / clearing structured filter tokens (size:, dm:, type macros)
/// in the search box without destroying free-text terms.
/// </summary>
public static class QueryFilterTokens
{
    private static readonly Regex SizeToken = new(@"\bsize:[^\s""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateToken = new(@"\bdm:[^\s""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> MacroNames = new(
        FileTypeMacros.Groups.SelectMany(g => g.Aliases.Append(g.Key)),
        StringComparer.OrdinalIgnoreCase);

    public static bool HasStructuredFilters(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        if (SizeToken.IsMatch(query) || DateToken.IsMatch(query)) return true;
        foreach (var token in SplitPreservingQuotes(query))
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && colon == token.Length - 1)
            {
                if (MacroNames.Contains(token[..colon]))
                    return true;
            }
        }
        return false;
    }

    public static string UpsertSize(string? query, string sizeSpec)
        => UpsertToken(query, SizeToken, "size:" + sizeSpec.Trim());

    public static string UpsertDate(string? query, string dateSpec)
        => UpsertToken(query, DateToken, "dm:" + dateSpec.Trim());

    public static string UpsertTypeMacro(string? query, string macroToken)
    {
        // Remove existing type macros, then append the new one
        var without = ClearTypeMacros(query);
        return AppendToken(without, macroToken.Trim().TrimEnd(':') + ":");
    }

    public static string ClearStructured(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var working = SizeToken.Replace(query, " ");
        working = DateToken.Replace(working, " ");
        working = ClearTypeMacros(working);
        return CollapseSpaces(working);
    }

    public static string ClearTypeMacros(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var token in SplitPreservingQuotes(query))
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && colon == token.Length - 1 && MacroNames.Contains(token[..colon]))
                continue;
            if (sb.Length > 0) sb.Append(' ');
            if (token.Contains(' '))
                sb.Append('"').Append(token).Append('"');
            else
                sb.Append(token);
        }
        return sb.ToString().Trim();
    }

    private static string UpsertToken(string? query, Regex existing, string replacement)
    {
        var working = string.IsNullOrWhiteSpace(query) ? string.Empty : existing.Replace(query, " ");
        working = CollapseSpaces(working);
        return AppendToken(working, replacement);
    }

    private static string AppendToken(string query, string token)
    {
        if (string.IsNullOrWhiteSpace(query))
            return token;
        return query.TrimEnd() + " " + token;
    }

    private static string CollapseSpaces(string s)
        => Regex.Replace(s.Trim(), @"\s+", " ");

    private static IEnumerable<string> SplitPreservingQuotes(string input)
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
