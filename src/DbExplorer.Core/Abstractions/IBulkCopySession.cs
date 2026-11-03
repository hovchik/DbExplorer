namespace DbExplorer.Core.Abstractions;

/// <summary>
/// A script session that can stream rows into a table with the engine's bulk load path (PostgreSQL's COPY) instead of
/// INSERT statements. It runs inside the session's transaction, so a failure leaves nothing behind once rolled back.
/// </summary>
public interface IBulkCopySession
{
    /// <summary>Writes every row into the table's columns and returns how many were written. Values are text in the
    /// engine's input format (null for NULL); the server converts them to the column types.</summary>
    /// <param name="table">The table's quoted name (schema.table).</param>
    /// <param name="columns">The quoted column names, in the order of each row's values.</param>
    /// <exception cref="Models.BulkCopyException">The server refused a row; carries its 1-based position in <paramref name="rows"/> when known.</exception>
    Task<long> CopyRowsAsync(string table, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string?>> rows, CancellationToken ct = default);
}
