namespace DbExplorer.Application.QueryBuilder;

public enum JoinKind
{
    Inner,
    Left,
    Right,
    Full
}

public enum ColumnAggregate
{
    None,
    Count,
    CountDistinct,
    Sum,
    Avg,
    Min,
    Max
}

public enum SortDirection
{
    None,
    Ascending,
    Descending
}

/// <summary>A table placed in the builder, under its alias (the same table can be placed twice for a self join).</summary>
public sealed record QueryTable(string Alias, string Schema, string Name);

/// <summary>One <c>left.column = right.column</c> pair. Conditions between the same two tables become one ON clause.
/// <see cref="Kind"/> reads from the left side: Left keeps every row of the left table.</summary>
public sealed record JoinCondition(string LeftAlias, string LeftColumn, string RightAlias, string RightColumn, JoinKind Kind = JoinKind.Inner);

/// <summary>A row of the column grid: a column of a placed table (or <c>*</c>), and what to do with it.</summary>
public sealed record QueryColumn
{
    public required string TableAlias { get; init; }

    /// <summary>A column name, or <c>*</c> for every column (or COUNT(*) with the Count aggregate).</summary>
    public required string Column { get; init; }

    /// <summary>In the SELECT list. A column can also be used only to filter or sort by.</summary>
    public bool Output { get; init; } = true;

    /// <summary>The name the result column gets (AS ...); empty keeps the column's own.</summary>
    public string OutputAlias { get; init; } = "";

    public ColumnAggregate Aggregate { get; init; }
    public SortDirection Sort { get; init; }

    /// <summary>A condition as typed: <c>&gt; 100</c>, <c>= 'AM'</c>, <c>LIKE 'A%'</c>, <c>IS NULL</c>, <c>IN (1, 2)</c>, or a bare
    /// value (<c>Smith</c> means = 'Smith', <c>A%</c> means LIKE 'A%'). On an aggregated column it goes to HAVING.</summary>
    public string Filter { get; init; } = "";
}

/// <summary>Everything the builder knows about the query; <see cref="QuerySqlBuilder"/> turns it into SQL.</summary>
public sealed record QueryDesign
{
    public IReadOnlyList<QueryTable> Tables { get; init; } = [];
    public IReadOnlyList<JoinCondition> Joins { get; init; } = [];
    public IReadOnlyList<QueryColumn> Columns { get; init; } = [];
    public bool Distinct { get; init; }

    /// <summary>TOP / LIMIT; null or 0 for every row.</summary>
    public int? Limit { get; init; }
}

/// <summary>The SQL, and what the user should know about it (cross joins, invalid sorts).</summary>
public sealed record BuiltQuery(string Sql, IReadOnlyList<string> Warnings);
