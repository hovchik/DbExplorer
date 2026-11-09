using System.Text.Json.Serialization;

namespace DbExplorer.Application.Query;

/// <summary>How the formatter writes keywords.</summary>
public enum SqlKeywordCase
{
    Upper,
    Lower,

    /// <summary>Keywords are left as typed.</summary>
    AsTyped
}

/// <summary>Where the comma between list items goes when each item has its own line.</summary>
public enum SqlCommaStyle
{
    EndOfLine,
    StartOfLine
}

/// <summary>
/// The SQL formatter's style (Preferences › SQL formatting). Every value has a default that matches the formatter's
/// layout before it had options: upper-case keywords, four spaces, commas at the end of the line, one column per line,
/// AND / OR on their own lines, and short CASE expressions and subqueries kept on one line.
/// </summary>
public sealed record SqlFormatOptions
{
    public const int MinIndentSize = 1;
    public const int MaxIndentSize = 8;

    /// <summary>A CASE expression or subquery whose one-line form is at most this long stays on one line (when
    /// <see cref="CompactShortExpressions"/> is on); so does a single-item SELECT, GROUP BY, ORDER BY or SET list.</summary>
    public const int CompactWidth = 60;

    public static SqlFormatOptions Default { get; } = new();

    public SqlKeywordCase KeywordCase { get; init; } = SqlKeywordCase.Upper;

    /// <summary>Spaces per indent level (ignored when <see cref="UseTabs"/> is on).</summary>
    public int IndentSize { get; init; } = 4;

    public bool UseTabs { get; init; }

    public SqlCommaStyle Commas { get; init; } = SqlCommaStyle.EndOfLine;

    /// <summary>Each SELECT column, GROUP BY / ORDER BY item, SET assignment and VALUES row on its own line.</summary>
    public bool OneItemPerLine { get; init; } = true;

    /// <summary>AND / OR in WHERE, HAVING and ON start a new, indented line.</summary>
    public bool BreakAndOr { get; init; } = true;

    /// <summary>Short CASE expressions (one WHEN) and short subqueries stay on one line.</summary>
    public bool CompactShortExpressions { get; init; } = true;

    /// <summary>One level of indentation.</summary>
    [JsonIgnore]
    public string IndentUnit => UseTabs ? "\t" : new string(' ', Math.Clamp(IndentSize, MinIndentSize, MaxIndentSize));

    /// <summary>The options with every value in range (a hand-edited settings file can hold anything).</summary>
    public SqlFormatOptions Normalized() => this with
    {
        KeywordCase = Enum.IsDefined(KeywordCase) ? KeywordCase : SqlKeywordCase.Upper,
        IndentSize = Math.Clamp(IndentSize, MinIndentSize, MaxIndentSize),
        Commas = Enum.IsDefined(Commas) ? Commas : SqlCommaStyle.EndOfLine
    };
}
