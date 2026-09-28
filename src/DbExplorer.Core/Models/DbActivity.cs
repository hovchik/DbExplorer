namespace DbExplorer.Core.Models;

/// <summary>A statement currently executing (or a session idle inside an open transaction).</summary>
public sealed record DbActiveRequest
{
    public int SessionId { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ProgramName { get; init; }
    public string? DatabaseName { get; init; }
    public string? Status { get; init; }
    public string? Command { get; init; }
    public DateTime? StartTime { get; init; }
    public long? ElapsedMs { get; init; }
    public long? CpuMs { get; init; }
    public long? LogicalReads { get; init; }
    public long? Writes { get; init; }
    public string? WaitType { get; init; }
    public long? WaitMs { get; init; }
    public int? BlockedBy { get; init; }
    public string? SqlText { get; init; }
}

public enum QueryStatOrder
{
    TotalCpu,
    TotalDuration,
    AverageDuration,
    LogicalReads,
    Executions
}

/// <summary>Cumulative statistics for one cached statement (SQL Server plan cache / pg_stat_statements).</summary>
public sealed record DbQueryStat
{
    public string? DatabaseName { get; init; }
    public string? SqlText { get; init; }
    public long ExecutionCount { get; init; }

    /// <summary>Not tracked by PostgreSQL.</summary>
    public double? TotalCpuMs { get; init; }
    public double? AvgCpuMs { get; init; }
    public double TotalElapsedMs { get; init; }
    public double AvgElapsedMs { get; init; }
    public long? TotalLogicalReads { get; init; }
    public double? AvgLogicalReads { get; init; }
    public long? TotalRows { get; init; }
    public DateTime? LastExecutionTime { get; init; }
}
