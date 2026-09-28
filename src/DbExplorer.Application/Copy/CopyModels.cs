using DbExplorer.Core.Models;

namespace DbExplorer.Application.Copy;

/// <summary>What copying the left object to the right side does.</summary>
public enum CopyAction
{
    /// <summary>The object is missing on the right: create it (tables optionally with their rows).</summary>
    CreateObject,

    /// <summary>The table exists: insert missing rows, optionally update changed rows and delete extra ones.</summary>
    MergeData,

    /// <summary>The table exists: delete the right rows (or the filtered slice) and insert the left ones.</summary>
    ReplaceData,

    /// <summary>The table exists: drop it on the right, re-create it from the left structure and copy the rows.</summary>
    DropAndRecreate,

    /// <summary>The view/routine/trigger exists: replace its definition by the left one.</summary>
    ReplaceDefinition
}

public sealed record CopyOptions
{
    /// <summary>Copy rows as well as structure when creating or re-creating a table.</summary>
    public bool IncludeData { get; init; } = true;

    /// <summary>Merge: rows present on both sides with different values are updated from the left.</summary>
    public bool UpdateChanged { get; init; } = true;

    /// <summary>Merge: rows present only on the right are deleted (makes the right an exact copy).</summary>
    public bool DeleteExtra { get; init; }

    public bool CopyIndexes { get; init; } = true;
    public bool CopyForeignKeys { get; init; } = true;

    /// <summary>Copy column defaults and check constraints of created tables (defaults also for added columns).</summary>
    public bool CopyDefaultsAndChecks { get; init; } = true;

    /// <summary>Create/replace: read the left rows page by page while running, instead of loading them all when
    /// planning. Needs a primary key; removes the row limit and keeps memory flat.</summary>
    public bool StreamRows { get; init; } = true;

    /// <summary>Rows read per page when streaming.</summary>
    public int PageSize { get; init; } = 5_000;

    /// <summary>Merge/replace: columns that exist only on the left are added to the right table first.</summary>
    public bool AddMissingColumns { get; init; } = true;

    /// <summary>Create the tables the object references (foreign keys) that are missing on the right, parents first.</summary>
    public bool IncludeMissingParents { get; init; }

    /// <summary>Snapshot the right table into a timestamped copy before any change to its rows or structure.</summary>
    public bool BackupTarget { get; init; }

    /// <summary>Optional SQL condition (without WHERE) restricting the rows read, applied to both sides.</summary>
    public string? RowFilter { get; init; }

    public int BatchSize { get; init; } = 500;
    public int MaxRows { get; init; } = 100_000;

    /// <summary>Run the whole script in one transaction: all or nothing.</summary>
    public bool SingleTransaction { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 900;
}

/// <summary>What the left object looks like against the right database, computed from cached metadata.</summary>
public sealed record CopyAnalysis
{
    public required DbObject Source { get; init; }
    public required string TargetDatabase { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetName { get; init; }
    public string TargetFullName => $"{TargetSchema}.{TargetName}";

    /// <summary>The object found on the right under the target name, if any.</summary>
    public DbObject? Target { get; init; }

    public bool TargetExists => Target is not null;
    public bool IsTable => Source.Type is DbObjectType.Table;
    public bool CrossEngine { get; init; }
    public bool TargetSchemaExists { get; init; }

    public IReadOnlyList<CopyAction> AvailableActions { get; init; } = [];
    public CopyAction? RecommendedAction { get; init; }

    /// <summary>One sentence describing the situation and the suggestion.</summary>
    public string Summary { get; init; } = "";

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Primary key columns of the left table that also exist on the right; merging needs them.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public IReadOnlyList<string> ColumnsMissingOnTarget { get; init; } = [];
    public IReadOnlyList<string> ColumnsOnlyOnTarget { get; init; } = [];
    public IReadOnlyList<string> ColumnTypeDifferences { get; init; } = [];

    /// <summary>Tables the left table references (directly or indirectly) that do not exist on the right, parents first.</summary>
    public IReadOnlyList<DbObject> MissingParents { get; init; } = [];

    /// <summary>Foreign keys on the right that reference the target table (they can block deletes and drops).</summary>
    public IReadOnlyList<string> IncomingReferences { get; init; } = [];
}

public enum CopyStepKind
{
    Schema,
    Backup,
    Structure,
    Delete,
    Insert,
    Update,
    Constraint,
    Maintenance
}

/// <summary>A step of the script. A streamed step has no data in <see cref="Sql"/> (only a description): its rows
/// are read from the left page by page and inserted when the plan runs.</summary>
public sealed record CopyStep(string Title, CopyStepKind Kind, string Sql, StreamedCopy? Stream = null);

/// <summary>What a streamed step copies: left rows (in key order, optionally filtered) into the right table.</summary>
public sealed record StreamedCopy(
    DbObject SourceTable,
    IReadOnlyList<DbColumn> SourceColumns,
    string TargetSchema,
    string TargetName,
    IReadOnlyList<DbColumn> TargetColumns,
    IReadOnlyList<string> KeyColumns,
    string? RowFilter,
    long EstimatedRows);

/// <summary>A reviewed, ready-to-run script plus what it will do.</summary>
public sealed record CopyPlan
{
    public required CopyAnalysis Analysis { get; init; }
    public required CopyAction Action { get; init; }
    public required CopyOptions Options { get; init; }
    public required string TargetProviderKey { get; init; }
    public IReadOnlyList<CopyStep> Steps { get; init; } = [];

    public int RowsToInsert { get; init; }
    public int RowsToUpdate { get; init; }
    public int RowsToDelete { get; init; }
    public int RowsUnchanged { get; init; }

    /// <summary>For replace/drop: rows removed from the right before inserting (null when unknown).</summary>
    public long? TargetRowsRemoved { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>"schema.name" of every table this plan creates (used to link foreign keys within a batch).</summary>
    public IReadOnlyList<string> CreatedTables { get; init; } = [];

    /// <summary>Everything the plan runs, wrapped in a transaction when <see cref="CopyOptions.SingleTransaction"/> is set.</summary>
    public string Script
    {
        get
        {
            var dialect = SqlDialect.For(TargetProviderKey);
            var body = string.Concat(Steps.Select(s => $"-- {s.Title}\n{s.Sql}{dialect.EndOfStep}"));
            return Options.SingleTransaction ? dialect.BeginTransaction + body + dialect.CommitTransaction : body;
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var structural = Steps.Count(s => s.Kind is CopyStepKind.Structure or CopyStepKind.Schema or CopyStepKind.Constraint);
            if (structural > 0) parts.Add($"{structural:N0} schema statement(s)");
            if (TargetRowsRemoved is long removed) parts.Add($"{removed:N0} right row(s) removed");
            if (RowsToInsert > 0) parts.Add($"{RowsToInsert:N0} row(s) inserted");
            if (RowsToUpdate > 0) parts.Add($"{RowsToUpdate:N0} updated");
            if (RowsToDelete > 0) parts.Add($"{RowsToDelete:N0} deleted");
            if (RowsUnchanged > 0) parts.Add($"{RowsUnchanged:N0} already identical");
            if (Steps.Any(s => s.Kind == CopyStepKind.Backup)) parts.Add("backup first");
            return parts.Count == 0 ? "Nothing to do: the right side already matches." : string.Join(" · ", parts);
        }
    }

    public bool IsEmpty => Steps.Count == 0;

    /// <summary>Streamed rows are not in <see cref="Script"/>; a saved script then only has the structure.</summary>
    public bool HasStreamedSteps => Steps.Any(s => s.Stream is not null);
}

public sealed record CopyRunResult(int RowsAffected, TimeSpan Elapsed, IReadOnlyList<string> Messages);

public sealed record CopyVerification(long SourceRows, long TargetRows, bool Matches, string Summary);
