using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Writes cell edits made in a result grid back to the database.</summary>
public static class ResultEditing
{
    private const int TimeoutSeconds = 60;

    /// <summary>The edits of <paramref name="rows"/> as UPDATE statements.</summary>
    public static IReadOnlyList<ResultUpdate> BuildUpdates(DatabaseSession session, ResultSource source, IReadOnlyList<ResultRow> rows) =>
        ResultEditSql.BuildUpdates(
            source,
            rows.Where(r => r.IsModified).Select(r => new ResultRowEdit(r.OriginalValues, r.ModifiedColumns.ToDictionary(c => c, c => r.Values[c]))),
            ResultExporter.DialectFor(session.Provider.ProviderKey),
            session.Provider.QuoteIdentifier);

    /// <summary>
    /// Runs the UPDATEs for the edited rows — inside <paramref name="openTransaction"/> when the caller has one (they stay
    /// uncommitted with it), otherwise in a transaction of their own that is committed only when every row was found.
    /// On production connections the statements are shown and must be confirmed. The rows keep their edits on failure.
    /// </summary>
    /// <returns>A status line, or null when the user cancelled.</returns>
    /// <exception cref="InvalidOperationException">A row was not found (changed or deleted since it was read).</exception>
    public static async Task<string?> CommitAsync(
        DatabaseSession session, ResultSource source, IReadOnlyList<ResultRow> rows, IScriptSession? openTransaction, IDialogService? dialogs)
    {
        var edited = rows.Where(r => r.IsModified).ToList();
        if (edited.Count == 0) return "Nothing to commit.";
        var updates = BuildUpdates(session, source, edited);

        if (session.Profile.IsProduction)
        {
            var preview = string.Join("\n", updates.Take(10).Select(u => u.Sql)) + (updates.Count > 10 ? $"\n… and {updates.Count - 10} more" : "");
            if (dialogs is null || !await dialogs.ConfirmAsync(
                    $"Save {edited.Count} edited row(s) on a PRODUCTION environment?\n\n{preview}",
                    "Commit", requiredText: "PRODUCTION", banner: $"PRODUCTION · {session.Profile.DisplayName}"))
                return null;
        }

        if (openTransaction is not null)
        {
            // Each statement is checked; a row that is gone stops the rest, and the open transaction can still be rolled back.
            foreach (var update in updates) await RunAsync(openTransaction, update, inOpenTransaction: true);
        }
        else
        {
            await using var tx = await session.Provider.BeginScriptSessionAsync(source.Database, transactional: true);
            foreach (var update in updates) await RunAsync(tx, update, inOpenTransaction: false); // disposing without commit rolls back
            await tx.CommitAsync();
        }

        foreach (var row in edited) row.AcceptChanges();
        var what = $"{updates.Count} UPDATE statement(s) for {edited.Count} row(s)";
        return openTransaction is null
            ? $"Saved {what}."
            : $"Ran {what} in the open transaction — Commit or Rollback the transaction to finish.";
    }

    private static async Task RunAsync(IScriptSession session, ResultUpdate update, bool inOpenTransaction)
    {
        var affected = await session.ExecuteAsync(update.Sql, TimeoutSeconds);
        if (affected == 0)
            throw new InvalidOperationException(
                $"No row of {update.Table.FullName} matched — it was changed or deleted since it was read. " +
                (inOpenTransaction ? "Statements before it ran in the open transaction; Rollback undoes them." : "Nothing was saved.") +
                $"\n{update.Sql}");
    }

    /// <summary>Discards the uncommitted edits of every row.</summary>
    public static int Revert(IEnumerable<ResultRow> rows)
    {
        var count = 0;
        foreach (var row in rows.Where(r => r.IsModified))
        {
            row.Revert();
            count++;
        }
        return count;
    }

    public static bool HasEdits(IEnumerable<ResultSetView> results) => results.Any(rs => rs.Rows.Any(r => r.IsModified));
}
