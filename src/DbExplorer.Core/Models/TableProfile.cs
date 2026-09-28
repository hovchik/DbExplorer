namespace DbExplorer.Core.Models;

/// <summary>What a provider can compute for a column type during profiling.</summary>
public enum ColumnProfileLevel
{
    /// <summary>Null count only (e.g. xml, image, spatial types).</summary>
    NullsOnly,

    /// <summary>Null and distinct counts, but no meaningful ordering for min/max.</summary>
    Distinct,

    /// <summary>Null and distinct counts plus min/max.</summary>
    Full
}

public sealed record ColumnProfile
{
    public string Column { get; init; } = "";
    public string DataType { get; init; } = "";
    public long NonNullCount { get; init; }
    public long NullCount { get; init; }
    public long? DistinctCount { get; init; }
    public string? MinValue { get; init; }
    public string? MaxValue { get; init; }

    public double NullPercent => NonNullCount + NullCount == 0 ? 0 : 100d * NullCount / (NonNullCount + NullCount);

    /// <summary>Every non-null value in the sample is distinct (a candidate key), when known.</summary>
    public bool? LooksUnique => DistinctCount is long d && NonNullCount > 0 ? d == NonNullCount : null;
}

/// <summary>Column statistics computed over the first <see cref="SampleLimit"/> rows of a table.</summary>
public sealed record TableProfile
{
    public long SampledRows { get; init; }
    public int SampleLimit { get; init; }
    public IReadOnlyList<ColumnProfile> Columns { get; init; } = [];
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when the table may have more rows than were sampled.</summary>
    public bool IsPartial => SampledRows >= SampleLimit;
}

public sealed record ValueFrequency
{
    public string? Value { get; init; }
    public long Count { get; init; }
}
