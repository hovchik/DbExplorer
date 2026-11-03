using System.Text;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.TestData;

/// <summary>Turns generated rows into multi-row INSERT statements in the engine's own syntax.</summary>
public static class TestDataScript
{
    /// <summary>Rows per INSERT; SQL Server refuses more than 1,000 in one VALUES list.</summary>
    public const int RowsPerStatement = 100;

    /// <summary>One INSERT per <see cref="RowsPerStatement"/> rows, each ending with a semicolon.</summary>
    public static IReadOnlyList<string> Statements(string providerKey, DbObject table, TestDataRows rows)
    {
        var dialect = SqlDialect.For(providerKey);
        var target = dialect.Table(table.Schema, table.Name);
        var statements = new List<string>();
        if (rows.Rows.Count == 0) return statements;

        if (rows.Columns.Count == 0)
        {
            // Every column comes from the server: identity, defaults, NULLs.
            if (providerKey == SqlDialect.MySqlKey)
            {
                for (var start = 0; start < rows.Rows.Count; start += RowsPerStatement)
                {
                    var count = Math.Min(RowsPerStatement, rows.Rows.Count - start);
                    statements.Add($"INSERT INTO {target} () VALUES\n" + string.Join(",\n", Enumerable.Repeat("  ()", count)) + ";");
                }
            }
            else
            {
                statements.AddRange(rows.Rows.Select(_ => $"INSERT INTO {target} DEFAULT VALUES;"));
            }
            return statements;
        }

        var header = $"INSERT INTO {target} ({string.Join(", ", rows.Columns.Select(c => dialect.Quote(c.Name)))}) VALUES\n";
        for (var start = 0; start < rows.Rows.Count; start += RowsPerStatement)
        {
            var sb = new StringBuilder(header);
            var end = Math.Min(start + RowsPerStatement, rows.Rows.Count);
            for (var r = start; r < end; r++)
            {
                sb.Append("  (");
                var row = rows.Rows[r];
                for (var c = 0; c < rows.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(dialect.Literal(row[c], rows.Columns[c].Column.BaseType));
                }
                sb.Append(r < end - 1 ? "),\n" : ");");
            }
            statements.Add(sb.ToString());
        }
        return statements;
    }

    /// <summary>The whole script for a Query tab: a comment saying what it is, then the INSERTs, in one transaction.</summary>
    public static string Script(string providerKey, DbObject table, TestDataRows rows, int seed)
    {
        var dialect = SqlDialect.For(providerKey);
        var sb = new StringBuilder();
        sb.Append("-- ").Append(rows.Rows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" generated test row(s) for ").Append(dialect.Table(table.Schema, table.Name));
        if (!string.IsNullOrEmpty(table.Database)) sb.Append(" in ").Append(table.Database);
        sb.Append(" (seed ").Append(seed).Append(").\n");
        foreach (var note in rows.Notes) sb.Append("-- ").Append(note).Append('\n');
        sb.Append(TransactionStart(providerKey)).Append('\n');
        foreach (var statement in Statements(providerKey, table, rows)) sb.Append(statement).Append('\n');
        sb.Append(providerKey == SqlDialect.SqlServerKey ? "COMMIT TRANSACTION;\n" : "COMMIT;\n");
        return sb.ToString();
    }

    private static string TransactionStart(string providerKey) => providerKey switch
    {
        SqlDialect.SqlServerKey => "SET XACT_ABORT ON;\nBEGIN TRANSACTION;",
        SqlDialect.MySqlKey => "START TRANSACTION;",
        _ => "BEGIN;"
    };
}
