using DbExplorer.Application.Design;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.TestData;

/// <summary>How the generator fills one column.</summary>
public enum TestValueKind
{
    /// <summary>Left out of the INSERT: the server fills it (identity, computed, default) or it stays NULL.</summary>
    ServerDefault,
    Null,
    /// <summary>The same text in every row.</summary>
    Fixed,
    /// <summary>Consecutive whole numbers, starting after the largest value in the table.</summary>
    Sequence,
    /// <summary>A value of a parent row, read from the table the foreign key points to.</summary>
    Reference,
    /// <summary>One of a known list: an enum's labels, a CHECK … IN list, or a list the name suggests (status, currency).</summary>
    OneOf,
    Integer,
    Decimal,
    Price,
    Quantity,
    Age,
    Year,
    Boolean,
    Date,
    BirthDate,
    DateTime,
    Time,
    Uuid,
    Word,
    ProductName,
    Sentence,
    Paragraph,
    Code,
    FirstName,
    LastName,
    FullName,
    Username,
    Email,
    Phone,
    Company,
    Street,
    City,
    Region,
    Country,
    PostalCode,
    Url,
    Color,
    Json,
    Binary
}

/// <summary>The rows of a parent table that a foreign key can point to, sampled before generating.</summary>
public sealed record TestReference
{
    public required DbForeignKey Key { get; init; }

    /// <summary>The child columns, in the key's order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<string> ParentColumns { get; init; }

    /// <summary>Distinct parent key tuples, one value per column; empty when the parent has no rows (yet).</summary>
    public IReadOnlyList<object?[]> Rows { get; init; } = [];

    public bool IsSelfReference { get; init; }

    public string ParentName => $"{Key.ReferencedSchema}.{Key.ReferencedTable}";
}

/// <summary>What the generator knows and decided about one column; the window lets the user change <see cref="Kind"/>
/// and <see cref="FixedValue"/>.</summary>
public sealed record TestColumnPlan
{
    public required DbColumn Column { get; init; }
    public TypeFamily Family { get; init; }
    public TestValueKind Kind { get; init; }

    /// <summary>The choices offered for this column, the detected one first.</summary>
    public IReadOnlyList<TestValueKind> Options { get; init; } = [];

    /// <summary>Characters the column takes; null when unlimited.</summary>
    public int? MaxLength { get; init; }

    public int? Precision { get; init; }
    public int? Scale { get; init; }

    /// <summary>Lowest and highest value, from the type and any CHECK the planner understood.</summary>
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }

    /// <summary>Strictly above <see cref="Min"/> / below <see cref="Max"/> (a CHECK with &gt; or &lt;).</summary>
    public bool MinExclusive { get; init; }
    public bool MaxExclusive { get; init; }

    /// <summary>Every value must differ: the primary key or a one-column unique index.</summary>
    public bool Unique { get; init; }

    public bool HasDefault { get; init; }

    /// <summary>Values for <see cref="TestValueKind.OneOf"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The first value after the table's largest, for <see cref="TestValueKind.Sequence"/>.</summary>
    public long SequenceStart { get; init; } = 1;

    public string FixedValue { get; init; } = "";

    /// <summary>Index into <see cref="TestDataPlan.References"/> and the column's position in that key.</summary>
    public int? ReferenceIndex { get; init; }
    public int ReferenceSlot { get; init; }

    /// <summary>Why this kind was picked, or what to watch out for.</summary>
    public string Note { get; init; } = "";

    public string Name => Column.Name;

    public bool IsInserted => Kind != TestValueKind.ServerDefault;
}

/// <summary>A CHECK that compares two columns of the row (end_date &gt;= start_date): the generator swaps the values
/// when a row would break it.</summary>
public sealed record TestColumnOrder(string Greater, string Lesser, bool Strict);

/// <summary>A unique index over several columns: generated rows may not repeat its tuple, nor repeat one already in
/// the table (<see cref="Existing"/>, sampled before generating).</summary>
public sealed record TestUniqueKey(string Name, IReadOnlyList<string> Columns)
{
    public IReadOnlyCollection<string> Existing { get; init; } = [];
}

/// <summary>Everything needed to generate rows for one table.</summary>
public sealed record TestDataPlan
{
    public required string ProviderKey { get; init; }
    public required DbObject Table { get; init; }
    public required IReadOnlyList<TestColumnPlan> Columns { get; init; }
    public IReadOnlyList<TestReference> References { get; init; } = [];
    public IReadOnlyList<TestUniqueKey> UniqueKeys { get; init; } = [];
    public IReadOnlyList<TestColumnOrder> Orders { get; init; } = [];

    /// <summary>Things the user should know before inserting: checks that weren't understood, empty parent tables…</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TestDataPlan WithColumn(TestColumnPlan column) =>
        this with { Columns = Columns.Select(c => c.Column.Name == column.Column.Name ? column : c).ToList() };
}

/// <summary>Generated rows for the columns that are inserted, in plan order.</summary>
public sealed record TestDataRows
{
    public required IReadOnlyList<TestColumnPlan> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>Rows asked for but not made: a unique key ran out of distinct values.</summary>
    public int Shortfall { get; init; }

    /// <summary>Why rows are missing or what was bent to fit; shown under the preview.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
