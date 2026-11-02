namespace DbExplorer.Core.Models;

/// <summary>
/// Receives one result set of a script row by row instead of keeping it in memory (a full export of a result that is
/// larger than the row limit). Every other result set of the script is read and discarded, so the script still runs to
/// its end. The callbacks run on whatever thread the driver continues on.
/// </summary>
/// <param name="resultSetIndex">Which result set to receive, counted from 0 among the statements that return rows,
/// the same way <see cref="QueryExecutionResult.ResultSets"/> is numbered.</param>
public sealed class ResultSetStream(int resultSetIndex, Action<IReadOnlyList<string>> begin, Action<object?[]> row)
{
    public int ResultSetIndex { get; } = resultSetIndex;

    /// <summary>The script produced the wanted result set (its columns were handed over).</summary>
    public bool Started { get; private set; }

    /// <summary>Rows handed over so far.</summary>
    public long RowCount { get; private set; }

    /// <summary>The result set with this index (counted like <see cref="ResultSetIndex"/>) is the one to stream.</summary>
    public bool Takes(int index) => index == ResultSetIndex && !Started;

    public void Begin(IReadOnlyList<string> columns)
    {
        Started = true;
        begin(columns);
    }

    /// <summary>One row; NULLs are <c>null</c> (never <see cref="DBNull"/>). The array is not reused by the caller.</summary>
    public void Write(object?[] values)
    {
        RowCount++;
        row(values);
    }

    /// <summary>Feeds a result that was read into memory (providers without a streaming path).</summary>
    public void Feed(QueryExecutionResult result)
    {
        if (ResultSetIndex >= result.ResultSets.Count) return;
        var set = result.ResultSets[ResultSetIndex];
        Begin(set.Columns);
        foreach (var r in set.Rows) Write(r.Select(v => v is DBNull ? null : v).ToArray());
    }

    /// <summary>Throws when the script ended without producing the wanted result set (it returns other data now).</summary>
    public void EnsureStarted()
    {
        if (!Started)
            throw new InvalidOperationException(
                $"The query returned fewer than {ResultSetIndex + 1} result set(s) this time, so there was nothing to export.");
    }
}
