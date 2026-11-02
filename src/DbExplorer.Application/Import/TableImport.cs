using System.Diagnostics;
using System.Globalization;
using System.Text;
using DbExplorer.Application.Design;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Import;

/// <summary>One file column going into one table column.</summary>
/// <param name="Source">The file column's position (0-based).</param>
/// <param name="Column">The table column's name.</param>
/// <param name="DataType">The column's type as declared (varchar(50), int, timestamptz): how the text is read and checked.</param>
public sealed record ImportColumn(int Source, string Column, string DataType, bool IsIdentity = false)
{
    public TypeFamily Family => ColumnTypes.Family(DataType);
    public int? MaxLength => Family == TypeFamily.Text ? ImportValues.MaxLength(DataType) : null;

    /// <summary>The type name without its size, lowercased (datetime, date), for literals that must match it exactly.</summary>
    public string BaseType
    {
        get
        {
            var open = DataType.IndexOf('(');
            return (open < 0 ? DataType : DataType[..open]).Trim().ToLowerInvariant();
        }
    }
}

/// <summary>What to import where: the target table, the column mapping, and how values are read.</summary>
public sealed record ImportPlan
{
    /// <summary>The table (Database empty for the connection's own database).</summary>
    public required DbObject Table { get; init; }
    public required IReadOnlyList<ImportColumn> Columns { get; init; }

    /// <summary>A new table is created first from this design (the import's columns are its columns).</summary>
    public TableDesign? Create { get; init; }

    /// <summary>The CREATE script for <see cref="Create"/>, written by <see cref="TableCreator.Script"/>.</summary>
    public string? CreateScript { get; init; }

    /// <summary>Existing rows are deleted (DELETE, inside the same transaction) before the import.</summary>
    public bool DeleteExisting { get; init; }

    /// <summary>Rows with a value that does not fit its column are left out instead of stopping the import.</summary>
    public bool SkipInvalidRows { get; init; }

    public ImportValueOptions Values { get; init; } = new();

    /// <summary>Rows per INSERT statement (SQL Server takes at most 1,000).</summary>
    public int BatchSize { get; init; } = 500;
}

/// <summary>A value in a file row that does not fit its column.</summary>
/// <param name="Row">The row's number in the file, counting the header row (as a spreadsheet numbers it).</param>
public sealed record ImportRowError(long Row, string Column, string Message)
{
    public string Text => $"Row {Row:N0}, {Column}: {Message}";
}

/// <summary>The outcome of reading every row of the file against the mapping, before anything is sent.</summary>
public sealed record ImportCheck(long Rows, long InvalidRows, IReadOnlyList<ImportRowError> Errors);

public sealed record ImportResult(long Imported, long Skipped, TimeSpan Elapsed, bool BulkLoaded);

/// <summary>The server refused the import; nothing was kept (the transaction was rolled back).</summary>
public sealed class ImportFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Imports rows from a file into a table: checks every value against its column first, then (in one transaction)
/// optionally creates the table and deletes its rows, and loads the rows with PostgreSQL's COPY or with INSERT
/// statements of <see cref="ImportPlan.BatchSize"/> rows elsewhere. Any failure rolls everything back.
/// </summary>
public static class TableImport
{
    public const int MaxListedErrors = 200;

    /// <summary>Matches file columns to table columns: by name when the file has titles (case, spaces and underscores
    /// ignored), else by position. Computed columns are never targets; unmatched file columns are left out.</summary>
    public static IReadOnlyList<ImportColumn> MapByName(IReadOnlyList<string> headers, IReadOnlyList<DbColumn> columns, bool byPosition)
    {
        var writable = columns.Where(c => !c.IsComputed).OrderBy(c => c.Ordinal).ToList();
        var map = new List<ImportColumn>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var column = byPosition
                ? (i < writable.Count ? writable[i] : null)
                : writable.FirstOrDefault(c => string.Equals(c.Name, headers[i].Trim(), StringComparison.OrdinalIgnoreCase) && !used.Contains(c.Name))
                  ?? writable.FirstOrDefault(c => ImportValues.LooseName(c.Name) == ImportValues.LooseName(headers[i]) && !used.Contains(c.Name));
            if (column is null || !used.Add(column.Name)) continue;
            map.Add(new ImportColumn(i, column.Name, column.DataType.Length > 0 ? column.DataType : column.BaseType, column.IsIdentity));
        }
        return map;
    }

    /// <summary>One row's values in mapping order, and the first problem when a value does not fit.</summary>
    public static (object?[] Values, ImportRowError? Error) Convert(string[] fields, IReadOnlyList<ImportColumn> columns, ImportValueOptions options, long rowNumber)
    {
        var values = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var text = column.Source < fields.Length ? fields[column.Source] : null;
            var parsed = ImportValues.Parse(text, column.Family, options, column.MaxLength);
            if (!parsed.IsValid) return (values, new ImportRowError(rowNumber, column.Column, parsed.Error!));
            values[i] = parsed.Value;
        }
        return (values, null);
    }

    /// <summary>Reads every data row against the mapping (on the caller's thread; run it in the background).</summary>
    /// <param name="firstRowNumber">The file row number of the first data row: 2 with a header, else 1.</param>
    public static ImportCheck Check(
        IEnumerable<string[]> rows, IReadOnlyList<ImportColumn> columns, ImportValueOptions options, long firstRowNumber,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long count = 0, invalid = 0;
        var errors = new List<ImportRowError>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var (_, error) = Convert(row, columns, options, firstRowNumber + count);
            count++;
            if (error is not null)
            {
                invalid++;
                if (errors.Count < MaxListedErrors) errors.Add(error);
            }
            if (count % 10_000 == 0) progress?.Report(count);
        }
        progress?.Report(count);
        return new ImportCheck(count, invalid, errors);
    }

    /// <summary>schema.table; the import's session is opened in the table's database.</summary>
    public static string TableName(DbObject table, Func<string, string> quote) => quote(table.Schema) + "." + quote(table.Name);

    /// <summary>One multi-row INSERT. PostgreSQL needs OVERRIDING SYSTEM VALUE to write into an identity column; SQL
    /// Server needs IDENTITY_INSERT, which <see cref="Script"/> and <see cref="RunAsync"/> switch on around the batches.</summary>
    public static string InsertStatement(
        ImportPlan plan, IReadOnlyList<object?[]> rows, SqlDialect dialect, Func<string, string> quote)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(TableName(plan.Table, quote)).Append(" (")
            .AppendJoin(", ", plan.Columns.Select(c => quote(c.Column))).Append(')');
        if (dialect == SqlDialect.Postgres && plan.Columns.Any(c => c.IsIdentity)) sb.Append(" OVERRIDING SYSTEM VALUE");
        sb.Append("\nVALUES");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append(r == 0 ? "\n  (" : ",\n  (");
            for (var i = 0; i < plan.Columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ResultEditSql.Literal(rows[r][i], dialect, plan.Columns[i].BaseType));
            }
            sb.Append(')');
        }
        return sb.Append(';').ToString();
    }

    private static string? IdentityInsert(ImportPlan plan, SqlDialect dialect, Func<string, string> quote, bool on) =>
        dialect == SqlDialect.SqlServer && plan.Columns.Any(c => c.IsIdentity)
            ? $"SET IDENTITY_INSERT {TableName(plan.Table, quote)} {(on ? "ON" : "OFF")};"
            : null;

    /// <summary>The statements before the rows: CREATE TABLE, DELETE, IDENTITY_INSERT ON.</summary>
    public static IReadOnlyList<string> Preamble(ImportPlan plan, SqlDialect dialect, Func<string, string> quote)
    {
        var statements = new List<string>();
        if (plan.CreateScript is { Length: > 0 } create) statements.Add(create.TrimEnd());
        else if (plan.DeleteExisting) statements.Add($"DELETE FROM {TableName(plan.Table, quote)};");
        if (IdentityInsert(plan, dialect, quote, on: true) is { } on) statements.Add(on);
        return statements;
    }

    /// <summary>The whole import as a script to read or run by hand (CREATE / DELETE, the INSERTs), for files small enough
    /// to show; rows that do not fit are left out with a comment.</summary>
    public static string Script(
        ImportPlan plan, IEnumerable<string[]> rows, long firstRowNumber, SqlDialect dialect, Func<string, string> quote, int maxRows = 5_000)
    {
        var sb = new StringBuilder();
        foreach (var statement in Preamble(plan, dialect, quote)) sb.AppendLine(statement).AppendLine();
        var batch = new List<object?[]>();
        long number = firstRowNumber, written = 0;
        var batchSize = Math.Clamp(plan.BatchSize, 1, 1000);
        foreach (var row in rows)
        {
            var (values, error) = Convert(row, plan.Columns, plan.Values, number++);
            if (error is not null)
            {
                sb.AppendLine($"-- left out: {error.Text.Replace('\n', ' ')}");
                continue;
            }
            if (written++ == maxRows)
            {
                sb.AppendLine($"-- stopped after {maxRows:N0} rows; Import loads the whole file");
                break;
            }
            batch.Add(values);
            if (batch.Count == batchSize)
            {
                sb.AppendLine(InsertStatement(plan, batch, dialect, quote)).AppendLine();
                batch.Clear();
            }
        }
        if (batch.Count > 0) sb.AppendLine(InsertStatement(plan, batch, dialect, quote)).AppendLine();
        if (IdentityInsert(plan, dialect, quote, on: false) is { } off) sb.AppendLine(off);
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>A value as PostgreSQL's COPY reads it.</summary>
    public static string? CopyText(object? value) => value switch
    {
        null or DBNull => null,
        bool b => b ? "t" : "f",
        byte[] bytes => "\\x" + System.Convert.ToHexString(bytes),
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.Days != 0
            ? $"{ts.Days} days {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Ticks % TimeSpan.TicksPerSecond:0000000}"
            : ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
        _ => ResultExporter.FormatInvariant(value)
    };

    /// <summary>
    /// Runs the import in one transaction and commits it. A row with a value that does not fit stops it (or is left out
    /// with <see cref="ImportPlan.SkipInvalidRows"/>); a row the server refuses stops it. Either way the transaction is
    /// rolled back. MySQL commits a CREATE TABLE by itself, so there a table created for a failed import is dropped again.
    /// </summary>
    /// <param name="progress">Rows sent so far.</param>
    /// <exception cref="ImportFailedException">Nothing was imported; the message says which rows and why.</exception>
    public static async Task<ImportResult> RunAsync(
        IDatabaseProvider provider, ImportPlan plan, IEnumerable<string[]> rows, long firstRowNumber, int timeoutSeconds,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var dialect = ResultExporter.DialectFor(provider.ProviderKey);
        var quote = provider.QuoteIdentifier;
        var sw = Stopwatch.StartNew();
        var database = string.IsNullOrEmpty(plan.Table.Database) ? null : plan.Table.Database;
        long sent = 0, skipped = 0;
        var skippedRows = new List<long>();
        var created = false;
        var bulk = false;

        // Converted rows, in file order, leaving out (or stopping at) rows that do not fit.
        IEnumerable<(long Number, object?[] Values)> Converted()
        {
            var number = firstRowNumber;
            foreach (var row in rows)
            {
                var (values, error) = Convert(row, plan.Columns, plan.Values, number);
                if (error is not null)
                {
                    if (!plan.SkipInvalidRows) throw new ImportFailedException($"Nothing was imported. {error.Text}");
                    skipped++;
                    skippedRows.Add(number);
                }
                else yield return (number, values);
                number++;
            }
        }

        try
        {
            await using var session = await provider.BeginScriptSessionAsync(database, transactional: true, ct);
            var preamble = Preamble(plan, dialect, quote);
            for (var i = 0; i < preamble.Count; i++)
            {
                await session.ExecuteAsync(preamble[i], timeoutSeconds, ct);
                if (i == 0 && plan.CreateScript is { Length: > 0 }) created = true;
            }

            if (session is IBulkCopySession copier && dialect == SqlDialect.Postgres)
            {
                bulk = true;
                IEnumerable<IReadOnlyList<string?>> Texts()
                {
                    foreach (var (_, values) in Converted())
                    {
                        sent++;
                        if (sent % 5_000 == 0) progress?.Report(sent);
                        yield return values.Select(CopyText).ToArray();
                    }
                }
                try
                {
                    await copier.CopyRowsAsync(TableName(plan.Table, quote), plan.Columns.Select(c => quote(c.Column)).ToList(), Texts(), ct);
                }
                catch (BulkCopyException ex)
                {
                    var at = ex.Row is { } row ? $"Row {FileRow(firstRowNumber, row, skippedRows):N0} or near it: " : "";
                    throw new ImportFailedException($"Nothing was imported. The server refused the rows. {at}{ex.Message}", ex);
                }
            }
            else
            {
                var batch = new List<object?[]>();
                long batchStart = firstRowNumber, lastNumber = firstRowNumber;
                var batchSize = Math.Clamp(plan.BatchSize, 1, 1000);
                async Task FlushAsync()
                {
                    if (batch.Count == 0) return;
                    try
                    {
                        await session.ExecuteAsync(InsertStatement(plan, batch, dialect, quote), timeoutSeconds, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var range = batch.Count == 1 ? $"row {batchStart:N0}" : $"one of rows {batchStart:N0}–{lastNumber:N0}";
                        throw new ImportFailedException($"Nothing was imported. The server refused {range}: {ex.Message}", ex);
                    }
                    sent += batch.Count;
                    progress?.Report(sent);
                    batch.Clear();
                }
                foreach (var (number, values) in Converted())
                {
                    ct.ThrowIfCancellationRequested();
                    if (batch.Count == 0) batchStart = number;
                    lastNumber = number;
                    batch.Add(values);
                    if (batch.Count == batchSize) await FlushAsync();
                }
                await FlushAsync();
            }

            if (IdentityInsert(plan, dialect, quote, on: false) is { } off) await session.ExecuteAsync(off, timeoutSeconds, ct);
            await session.CommitAsync(ct);
            progress?.Report(sent);
            return new ImportResult(sent, skipped, sw.Elapsed, bulk);
        }
        catch (Exception ex)
        {
            if (created && dialect == SqlDialect.MySql) await DropCreatedAsync(provider, plan, database, dialect, quote, timeoutSeconds);
            if (ex is ImportFailedException or OperationCanceledException) throw;
            throw new ImportFailedException($"Nothing was imported: {ex.Message}", ex);
        }
    }

    /// <summary>The file row number of the <paramref name="sent"/>th row sent (1-based), given the rows left out before it.</summary>
    public static long FileRow(long firstRowNumber, long sent, IReadOnlyList<long> skippedRows)
    {
        var row = firstRowNumber + sent - 1;
        foreach (var s in skippedRows)
            if (s <= row) row++;
            else break;
        return row;
    }

    /// <summary>MySQL kept the CREATE TABLE of an import that failed; the empty table is dropped so nothing is left.</summary>
    private static async Task DropCreatedAsync(
        IDatabaseProvider provider, ImportPlan plan, string? database, SqlDialect dialect, Func<string, string> quote, int timeoutSeconds)
    {
        try
        {
            await using var cleanup = await provider.BeginScriptSessionAsync(database, transactional: false);
            await cleanup.ExecuteAsync($"DROP TABLE {TableName(plan.Table, quote)};", timeoutSeconds);
        }
        catch (Exception)
        {
            // Best effort: the error the user sees is the import's own.
        }
    }
}
