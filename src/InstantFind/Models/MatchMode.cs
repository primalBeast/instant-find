namespace InstantFind.Models;

/// <summary>
/// How multi-term queries combine. LiteralWhitespace keeps spaces in a single term.
/// </summary>
public enum MatchMode
{
    And,
    Or,
    LiteralWhitespace
}
