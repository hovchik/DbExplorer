using System.Globalization;
using System.Text.RegularExpressions;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Lab;

/// <summary>An <c>-- expect: …</c> comment of a script and the result set it is about.</summary>
/// <param name="Line">1-based line of the comment.</param>
/// <param name="ResultIndex">Which result set of the run it checks (statements that return rows, counted in order).</param>
public sealed record QueryExpectation(int Line, string Text, int ResultIndex);

/// <summary>Whether an expectation held after a run.</summary>
public sealed record ExpectationOutcome(QueryExpectation Expectation, bool Passed, string Detail)
{
    public string Icon => Passed ? "✓" : "✗";
    public override string ToString() => $"{Icon} line {Expectation.Line}: expect {Expectation.Text} — {Detail}";
}

/// <summary>
/// Expectations written as comments right above a statement and checked against its result after every run, which turns
/// a query into a small data test (and, with auto refresh, a watchdog):
/// <code>
/// -- expect: rows = 0
/// SELECT * FROM orders WHERE total &lt; 0;
/// </code>
/// Supported: <c>rows = | != | &lt; | &lt;= | &gt; | &gt;= N</c>, <c>empty</c>, <c>not empty</c>,
/// <c>&lt;column&gt; not null</c>, <c>&lt;column&gt; unique</c> and <c>value = | != | &lt; | &gt; … literal</c>
/// (the first column of the first row). Several can be joined with <c>and</c>. Checking only reads the rows already shown.
/// </summary>
public static class QueryExpectations
{
    private static readonly Regex Marker = new(@"^--\s*expect\b\s*:?\s*(?<body>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Rows = new(@"^rows?\s*(?<op>=|==|!=|<>|<=|>=|<|>)\s*(?<n>\d[\d_,]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Value = new(@"^value\s*(?<op>=|==|!=|<>|<=|>=|<|>)\s*(?<v>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotNull = new(@"^(?<col>.+?)\s+(is\s+)?not\s+null$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Unique = new(@"^(?<col>.+?)\s+(is\s+)?unique$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex And = new(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Statements that return a result set (and so take the next result index).</summary>
    private static readonly HashSet<string> RowReturning = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "VALUES", "TABLE", "SHOW" };

    /// <summary>The script's expectations, each tied to the result set of the statement right below it.</summary>
    public static IReadOnlyList<QueryExpectation> Parse(string script)
    {
        if (string.IsNullOrEmpty(script) || script.IndexOf("expect", StringComparison.OrdinalIgnoreCase) < 0) return [];
        var tokens = SqlLexer.Tokenize(script);
        var statements = SqlScriptTools.SplitStatements(script);

        // Where each statement's first real word is, and which result set it produces.
        var starts = new List<(int Offset, int ResultIndex)>();
        var results = 0;
        foreach (var range in statements)
        {
            var first = tokens.FirstOrDefault(t => t.Start >= range.Start && t.End <= range.End && !t.IsTrivia);
            if (first.Text is null || first.Kind == SqlTokenKind.Semicolon) continue;
            var returnsRows = first.Kind == SqlTokenKind.Word && RowReturning.Contains(first.Text) && !SelectsInto(script, range);
            starts.Add((first.Start, returnsRows ? results : -1));
            if (returnsRows) results++;
        }

        var found = new List<QueryExpectation>();
        foreach (var comment in tokens.Where(t => t.Kind == SqlTokenKind.Comment))
        {
            var m = Marker.Match(comment.Text.Trim());
            if (!m.Success) continue;
            var target = starts.FirstOrDefault(s => s.Offset > comment.Start);
            if (target == default || target.ResultIndex < 0) continue;
            var line = LineOf(script, comment.Start);
            foreach (var part in And.Split(m.Groups["body"].Value))
                if (part.Trim().TrimEnd(';') is { Length: > 0 } text)
                    found.Add(new QueryExpectation(line, text, target.ResultIndex));
        }
        return found;
    }

    /// <summary>Checks each expectation against the result set it is about.</summary>
    public static IReadOnlyList<ExpectationOutcome> Check(IReadOnlyList<QueryExpectation> expectations, IReadOnlyList<QueryResultSet> results)
    {
        var outcomes = new List<ExpectationOutcome>();
        foreach (var e in expectations)
        {
            if (e.ResultIndex >= results.Count)
            {
                outcomes.Add(new ExpectationOutcome(e, false, "the statement returned no result set"));
                continue;
            }
            outcomes.Add(CheckOne(e, results[e.ResultIndex]));
        }
        return outcomes;
    }

    private static ExpectationOutcome CheckOne(QueryExpectation e, QueryResultSet rs)
    {
        var text = e.Text.Trim();
        var count = rs.IsTruncated ? rs.TotalRowCount : rs.Rows.Count;
        var countText = (rs.IsTruncated && !rs.TotalRowCountIsExact ? "at least " : "") + $"{count:N0} row(s)";

        if (text.Equals("empty", StringComparison.OrdinalIgnoreCase) || text.Equals("no rows", StringComparison.OrdinalIgnoreCase))
            return new(e, count == 0, "got " + countText);
        if (text.Equals("not empty", StringComparison.OrdinalIgnoreCase) || text.Equals("rows", StringComparison.OrdinalIgnoreCase))
            return new(e, count > 0, "got " + countText);

        if (Rows.Match(text) is { Success: true } rows)
        {
            var n = long.Parse(rows.Groups["n"].Value.Replace("_", "").Replace(",", ""), CultureInfo.InvariantCulture);
            var passed = Compare(count.CompareTo(n), rows.Groups["op"].Value);
            return new(e, passed, "got " + countText +
                (rs.IsTruncated && !rs.TotalRowCountIsExact ? " (reading stopped at Max rows; raise it for an exact count)" : ""));
        }

        if (Value.Match(text) is { Success: true } value)
        {
            if (rs.Rows.Count == 0 || rs.Columns.Count == 0) return new(e, false, "no rows, so no value");
            var actual = rs.Rows[0][0];
            var expected = Unquote(value.Groups["v"].Value.Trim());
            var cmp = CompareValues(actual, expected);
            var shown = ResultDiff.Display(actual);
            return cmp is null
                ? new(e, false, $"got {shown}, which cannot be compared with {expected}")
                : new(e, Compare(cmp.Value, value.Groups["op"].Value), "got " + shown);
        }

        if (NotNull.Match(text) is { Success: true } notNull)
        {
            if (ColumnIndex(rs, notNull.Groups["col"].Value) is not int c) return Missing(e, notNull.Groups["col"].Value, rs);
            var nulls = rs.Rows.Count(r => r[c] is null or DBNull);
            return new(e, nulls == 0, nulls == 0 ? "no NULLs" : $"{nulls:N0} NULL(s)");
        }

        if (Unique.Match(text) is { Success: true } unique)
        {
            if (ColumnIndex(rs, unique.Groups["col"].Value) is not int c) return Missing(e, unique.Groups["col"].Value, rs);
            var duplicates = rs.Rows.GroupBy(r => ResultDiff.Text(r[c]), StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
            return new(e, duplicates.Count == 0, duplicates.Count == 0
                ? "all values unique"
                : $"{duplicates.Count:N0} duplicated value(s), e.g. {ResultDiff.Display(duplicates[0].First()[c])} ×{duplicates[0].Count()}");
        }

        return new(e, false, "not understood (try rows = 0, empty, not empty, <column> not null, <column> unique, value > 0)");
    }

    private static ExpectationOutcome Missing(QueryExpectation e, string column, QueryResultSet rs) =>
        new(e, false, $"no column \"{column.Trim()}\" in the result ({string.Join(", ", rs.Columns.Take(8))})");

    private static int? ColumnIndex(QueryResultSet rs, string name)
    {
        name = name.Trim().Trim('[', ']', '"', '`');
        for (var i = 0; i < rs.Columns.Count; i++)
            if (string.Equals(rs.Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    private static bool Compare(int cmp, string op) => op switch
    {
        "=" or "==" => cmp == 0,
        "!=" or "<>" => cmp != 0,
        "<" => cmp < 0,
        "<=" => cmp <= 0,
        ">" => cmp > 0,
        ">=" => cmp >= 0,
        _ => false
    };

    /// <summary>Numbers compare as numbers, everything else as text; NULL only equals NULL.</summary>
    private static int? CompareValues(object? actual, string expected)
    {
        var isNull = expected.Equals("null", StringComparison.OrdinalIgnoreCase);
        if (actual is null or DBNull) return isNull ? 0 : null;
        if (isNull) return 1;
        if (actual is IConvertible && actual is not string and not DateTime and not bool &&
            double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            try { return Convert.ToDouble(actual, CultureInfo.InvariantCulture).CompareTo(number); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
        }
        if (actual is bool b && bool.TryParse(expected, out var expectedBool)) return b.CompareTo(expectedBool);
        return string.Compare(ResultDiff.Display(actual), expected, StringComparison.Ordinal);
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '\'' && s[^1] == '\'' || s[0] == '"' && s[^1] == '"') ? s[1..^1].Replace("''", "'") : s;

    /// <summary>SELECT … INTO creates a table and returns no rows.</summary>
    private static bool SelectsInto(string script, SqlRange range) =>
        SqlLexer.Tokenize(range.Of(script)).Any(t => t.Is("INTO")) &&
        SqlLexer.Tokenize(range.Of(script)).FirstOrDefault(t => !t.IsTrivia).Is("SELECT");

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
