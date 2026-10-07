using DbExplorer.Application.Connections;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Writes the changes made in a result grid back to the database: edited values, new rows and deleted rows.</summary>
public static class ResultEditing
{
    private const int TimeoutSeconds = 60;

    /// <summary>The changes of <paramref name="rows"/> as statements: DELETEs, then UPDATEs, then INSERTs.</summary>
    public static IReadOnlyList<ResultUpdate> BuildChanges(DatabaseSession session, ResultSource source, IReadOnlyList<ResultRow> rows) =>
        ResultEditSql.BuildChanges(
            source,
            rows.Where(r => r.IsDeleted).Select(r => r.OriginalValues),
            rows.Where(r => r.State == ResultRowState.Unchanged && r.IsModified)
                .Select(r => new ResultRowEdit(r.OriginalValues, r.ModifiedColumns.ToDictionary(c => c, c => r.Values[c]))),
            rows.Where(r => r.IsNew).Select(r => (IReadOnlyDictionary<int, object?>)r.ModifiedColumns.ToDictionary(c => c, c => r.Values[c])),
            ResultExporter.DialectFor(session.Provider.ProviderKey),
            session.Provider.QuoteIdentifier);

    /// <summary>
    /// Runs the statements for the changed rows — inside <paramref name="openTransaction"/> when the caller has one (they stay
    /// uncommitted with it), otherwise in a transaction of their own that is committed only when every row was found.
    /// New or deleted rows show the exact SQL for review first; on production connections every change does, and must be
    /// confirmed by typing PRODUCTION. On failure the rows keep their changes, except in the open transaction: there the rows
    /// whose statements already ran are marked saved, as those statements stay applied. Deleted rows are
    /// <see cref="ResultRowState.Removed"/> afterwards, for the caller to take out of the result.
    /// </summary>
    /// <returns>A status line, or null when the user cancelled.</returns>
    /// <exception cref="InvalidOperationException">A row was not found (changed or deleted since it was read).</exception>
    public static async Task<string?> CommitAsync(
        DatabaseSession session, ResultSource source, IReadOnlyList<ResultRow> rows, IScriptSession? openTransaction, IDialogService? dialogs)
    {
        var changed = rows.Where(r => r.HasChanges).ToList();
        if (changed.Count == 0) return "Nothing to commit.";
        if (session.Profile.ReadOnly) return ReadOnlyGuard.Refusal(session.Profile, "Commit") + " Your changes are kept.";
        // Each row with its statements, in the order they run: DELETEs, then UPDATEs, then INSERTs.
        var perRow = changed.Where(r => r.IsDeleted)
            .Concat(changed.Where(r => r.State == ResultRowState.Unchanged && r.IsModified))
            .Concat(changed.Where(r => r.IsNew))
            .Select(r => (Row: r, Statements: BuildChanges(session, source, [r])))
            .ToList();
        var statements = perRow.SelectMany(p => p.Statements).ToList();
        var summary = Summary(statements);

        var production = session.Profile.IsProduction;
        if (production || statements.Any(s => s.Kind != ResultChangeKind.Update))
        {
            if (dialogs is null || !await dialogs.ConfirmAsync(
                    $"{(production ? "On a PRODUCTION environment: save" : "Save")} {summary}?\n" +
                    (openTransaction is null
                        ? "These statements run in one transaction: if any of them fails, nothing is saved."
                        : "These statements run in the open transaction: Commit or Rollback it afterwards."),
                    "Save", requiredText: production ? "PRODUCTION" : null,
                    banner: production ? $"PRODUCTION · {session.Profile.DisplayName}" : null,
                    details: ResultEditSql.Script(statements)))
                return null;
        }

        var stored = new Dictionary<ResultRow, IReadOnlyList<object?>>(ReferenceEqualityComparer.Instance);
        async Task RunAllAsync(IScriptSession tx, bool inOpenTransaction)
        {
            foreach (var (row, rowStatements) in perRow)
            {
                IReadOnlyList<object?>? values = null;
                foreach (var statement in rowStatements)
                {
                    if (statement.Kind == ResultChangeKind.Insert) values = await InsertAsync(tx, statement, row);
                    else await RunAsync(tx, statement, inOpenTransaction);
                }
                // In the open transaction the row's statements stay applied even when a later row fails: mark it saved
                // now, so saving again does not insert it twice or look for a row it already deleted.
                if (inOpenTransaction) row.AcceptChanges(values);
                else if (values is not null) stored[row] = values;
            }
        }

        if (openTransaction is not null)
        {
            // Each statement is checked; a row that is gone stops the rest, and the open transaction can still be rolled back.
            await RunAllAsync(openTransaction, inOpenTransaction: true);
        }
        else
        {
            await using var tx = await session.Provider.BeginScriptSessionAsync(source.Database, transactional: true);
            await RunAllAsync(tx, inOpenTransaction: false); // disposing without commit rolls back
            await tx.CommitAsync();
        }

        foreach (var row in changed) row.AcceptChanges(stored.GetValueOrDefault(row));
        return openTransaction is null
            ? $"Saved {summary}."
            : $"Ran {summary} in the open transaction — Commit or Rollback the transaction to finish.";
    }

    /// <summary>"2 UPDATE, 1 INSERT and 3 DELETE statement(s)".</summary>
    private static string Summary(IReadOnlyList<ResultUpdate> statements)
    {
        var parts = new[] { ResultChangeKind.Update, ResultChangeKind.Insert, ResultChangeKind.Delete }
            .Select(k => (Kind: k, Count: statements.Count(s => s.Kind == k)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Kind.ToString().ToUpperInvariant()}")
            .ToList();
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return list + " statement(s)";
    }

    /// <summary>Inserts a new row and returns its values as stored (defaults and generated keys filled in), or null when the
    /// statement does not read the row back.</summary>
    private static async Task<IReadOnlyList<object?>?> InsertAsync(IScriptSession session, ResultUpdate insert, ResultRow row)
    {
        var result = await session.QueryAsync(insert.Sql, TimeoutSeconds);
        if (insert.ReturnedColumns.Count == 0 || result.ResultSets.LastOrDefault() is not { Rows: [var stored, ..] }) return null;
        var values = row.Values.ToArray();
        for (var i = 0; i < insert.ReturnedColumns.Count && i < stored.Count; i++)
            foreach (var column in insert.ReturnedColumns[i])
                if (column < values.Length) values[column] = stored[i];
        return values;
    }

    private static async Task RunAsync(IScriptSession session, ResultUpdate update, bool inOpenTransaction)
    {
        var affected = await session.ExecuteAsync(update.Sql, TimeoutSeconds);
        if (affected == 0)
            throw new InvalidOperationException(
                $"No row of {update.Table.FullName} matched — it was changed or deleted since it was read. " +
                (inOpenTransaction ? "The rows before it were saved in the open transaction; Rollback undoes them." : "Nothing was saved.") +
                $"\n{update.Sql}");
    }

    /// <summary>Discards every uncommitted change: edits are reverted, deletions undone and new rows removed.</summary>
    /// <returns>How many rows had changes.</returns>
    public static int Revert(ResultSetView results)
    {
        var count = results.RemoveRows(r => r.IsNew);
        foreach (var row in results.Rows.Where(r => r.HasChanges))
        {
            row.Revert();
            count++;
        }
        return count;
    }

    public static bool HasEdits(IEnumerable<ResultSetView> results) => results.Any(rs => rs.Rows.Any(r => r.HasChanges));
}
