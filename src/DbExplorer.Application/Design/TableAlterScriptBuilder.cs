using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Design;

/// <summary>
/// Turns the difference between an existing table (<c>original</c>, as <see cref="TableDesignLoader"/> read it) and its
/// edited design into ALTER statements for an engine. Columns are matched by <see cref="ColumnDesign.OriginalName"/>,
/// foreign keys and indexes by their name on the server: one whose definition changed is dropped and added again.
/// Statements run in an order that keeps every step valid: rename/move the table, drop what goes away (keys, indexes,
/// the primary key, columns), rename and change columns, then add columns, the primary key, foreign keys and indexes.
/// SQL Server statements are separated by GO so each sees what the previous one did.
/// </summary>
public static class TableAlterScriptBuilder
{
    public const string NoChanges = "-- No changes yet. Edit the table and the ALTER script shows up here.\n";

    public static string Build(TableDesign original, TableDesign edited, string providerKey, bool createSchema = false)
    {
        var steps = Steps(original, edited, providerKey, createSchema);
        if (steps.Count == 0) return NoChanges;
        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        return string.Concat(steps.Select(s => s + (sqlServer ? "\nGO\n" : "\n")));
    }

    /// <summary>The statements, one per entry, without batch separators.</summary>
    public static IReadOnlyList<string> Steps(TableDesign original, TableDesign edited, string providerKey, bool createSchema = false)
    {
        var d = SqlDialect.For(providerKey);
        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        var mySql = providerKey == SqlDialect.MySqlKey;
        var steps = new List<string>();

        var oldSchema = TableScriptBuilder.SchemaOf(original, providerKey);
        var oldName = original.Name.Trim();
        var schema = TableScriptBuilder.SchemaOf(edited, providerKey);
        var name = TableScriptBuilder.NameOf(edited);
        var table = d.Table(schema, name);

        // ----- The table's own name and schema first: every later statement uses the new one -----
        if (!string.Equals(oldName, name, StringComparison.Ordinal))
            steps.Add(sqlServer
                ? $"EXEC sp_rename {d.Literal(d.Table(oldSchema, oldName))}, {d.Literal(name)};"
                : $"ALTER TABLE {d.Table(oldSchema, oldName)} RENAME TO {d.Quote(name)};");
        if (!string.Equals(oldSchema, schema, StringComparison.Ordinal))
        {
            if (createSchema) steps.Add(d.CreateSchemaIfMissing(schema));
            steps.Add(sqlServer ? $"ALTER SCHEMA {d.Quote(schema)} TRANSFER {d.Table(oldSchema, name)};"
                : mySql ? $"RENAME TABLE {d.Table(oldSchema, name)} TO {table};"
                : $"ALTER TABLE {d.Table(oldSchema, name)} SET SCHEMA {d.Quote(schema)};");
        }

        var columns = edited.Columns.Where(c => c.Name.Trim().Length > 0).ToList();
        var kept = columns.Where(c => c.OriginalName is not null && original.Column(c.OriginalName) is not null).ToList();
        var added = columns.Except(kept).ToList();
        var dropped = original.Columns.Where(o => !kept.Any(c => TableDesign.Same(c.OriginalName!, o.Name))).ToList();

        // Names in the edited design mapped back to the server's, so a renamed column's key or index counts as unchanged.
        string ToOriginal(string column) =>
            columns.FirstOrDefault(c => TableDesign.Same(c.Name.Trim(), column.Trim()))?.OriginalName ?? "\u0001new:" + column.Trim();

        // ----- Drop foreign keys and indexes that went away or changed -----
        var addKeys = new List<ForeignKeyDesign>();
        foreach (var fk in edited.ForeignKeys.Where(f => f.Column.Trim().Length > 0 && f.ReferencedTable.Trim().Length > 0))
        {
            var before = original.ForeignKeys.FirstOrDefault(o => fk.Name.Trim().Length > 0 && TableDesign.Same(o.Name, fk.Name.Trim()));
            if (before is null || !SameKey(before, fk, ToOriginal)) addKeys.Add(fk);
        }
        foreach (var fk in original.ForeignKeys)
        {
            var after = edited.ForeignKeys.FirstOrDefault(e => TableDesign.Same(e.Name.Trim(), fk.Name));
            if (after is null || addKeys.Contains(after)) steps.Add(DropForeignKey(providerKey, table, fk.Name));
        }

        var addIndexes = new List<IndexDesign>();
        foreach (var index in edited.Indexes.Where(i => i.Columns.Count > 0))
        {
            var before = original.Indexes.FirstOrDefault(o => index.Name.Trim().Length > 0 && TableDesign.Same(o.Name, index.Name.Trim()));
            if (before is null || !SameIndex(before, index, ToOriginal)) addIndexes.Add(index);
        }
        foreach (var index in original.Indexes)
        {
            var after = edited.Indexes.FirstOrDefault(e => TableDesign.Same(e.Name.Trim(), index.Name));
            if (after is null || addIndexes.Contains(after))
                // The index behind a UNIQUE constraint cannot be dropped by itself: the constraint takes it along.
                steps.Add(index.IsConstraint && !mySql ? $"ALTER TABLE {table} DROP CONSTRAINT {d.Quote(index.Name)};"
                    : sqlServer || mySql ? $"DROP INDEX {d.Quote(index.Name)} ON {table};" : $"DROP INDEX {d.Table(schema, index.Name)};");
        }

        // ----- Primary key -----
        var oldKey = original.PrimaryKey.Select(c => c.Name).ToList();
        var newKey = edited.PrimaryKey.Select(c => ToOriginal(c.Name)).ToList();
        var keyChanged = !oldKey.SequenceEqual(newKey, StringComparer.OrdinalIgnoreCase);
        // MySQL refuses a primary key change that leaves an AUTO_INCREMENT column without a key, so a column losing
        // AUTO_INCREMENT is changed before the key is dropped, and one gaining it after the new key is added.
        var mySqlDone = new HashSet<ColumnDesign>();
        var mySqlLater = new List<string>();
        if (mySql && keyChanged)
            foreach (var column in kept)
            {
                var before = original.Column(column.OriginalName!)!;
                if (before.IsIdentity && !column.IsIdentity)
                {
                    steps.Add(MySqlModify(d, table, d.Quote(column.OriginalName!), column, before));
                    mySqlDone.Add(column);
                }
            }
        if (keyChanged && oldKey.Count > 0)
            steps.Add(mySql
                ? $"ALTER TABLE {table} DROP PRIMARY KEY;"
                : $"ALTER TABLE {table} DROP CONSTRAINT {d.Quote(TableScriptBuilder.PrimaryKeyName(original, d))};");

        // ----- Columns that go away -----
        foreach (var column in dropped)
        {
            if (sqlServer) steps.Add(DropSqlServerDefault(d, table, column.Name));
            steps.Add($"ALTER TABLE {table} DROP COLUMN {d.Quote(column.Name)};");
        }

        // ----- Renames, then changes, of the columns that stay -----
        foreach (var column in kept.Where(c => !string.Equals(c.Name.Trim(), c.OriginalName, StringComparison.Ordinal)))
            steps.Add(sqlServer
                ? $"EXEC sp_rename {d.Literal($"{d.Table(schema, name)}.{d.Quote(column.OriginalName!)}")}, {d.Literal(column.Name.Trim())}, N'COLUMN';"
                : $"ALTER TABLE {table} RENAME COLUMN {d.Quote(column.OriginalName!)} TO {d.Quote(column.Name.Trim())};");

        foreach (var column in kept)
        {
            var before = original.Column(column.OriginalName!)!;
            var q = d.Quote(column.Name.Trim());
            var typeChanged = column.FullType.Length > 0 &&
                              ColumnTypes.Canonical(providerKey, before.FullType) != ColumnTypes.Canonical(providerKey, column.FullType);
            var notNull = column.WritesNotNull || column.IsIdentity;
            var nullChanged = (before.WritesNotNull || before.IsIdentity) != notNull;
            var defaultChanged = !SameDefault(before.Default, column.Default) || before.IsIdentity != column.IsIdentity;
            var fillNulls = !before.WritesNotNull && notNull && !string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity;

            if (sqlServer)
            {
                // A default constraint blocks ALTER COLUMN, so it is dropped first and added back after.
                var hadDefault = !string.IsNullOrWhiteSpace(before.Default);
                if (hadDefault && (defaultChanged || typeChanged || nullChanged)) steps.Add(DropSqlServerDefault(d, table, column.Name.Trim()));
                if (fillNulls) steps.Add($"UPDATE {table} SET {q} = {column.Default!.Trim()} WHERE {q} IS NULL;");
                if (typeChanged || nullChanged)
                    steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} {column.FullType}{(notNull ? " NOT NULL" : " NULL")};");
                if (!string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity && (defaultChanged || (hadDefault && (typeChanged || nullChanged))))
                    steps.Add($"ALTER TABLE {table} ADD DEFAULT {column.Default.Trim()} FOR {q};");
                // IDENTITY cannot be added to or removed from a column; the designer reports it as an error.
                continue;
            }

            if (mySql)
            {
                // MySQL restates the whole column (type, AUTO_INCREMENT, NULL, DEFAULT) in one MODIFY.
                if (fillNulls) steps.Add($"UPDATE {table} SET {q} = {column.Default!.Trim()} WHERE {q} IS NULL;");
                if (mySqlDone.Contains(column) || !(typeChanged || nullChanged || defaultChanged)) continue;
                if (keyChanged && column.IsIdentity && !before.IsIdentity) mySqlLater.Add(MySqlModify(d, table, q, column, before));
                else steps.Add(MySqlModify(d, table, q, column, before));
                continue;
            }

            if (defaultChanged && !string.IsNullOrWhiteSpace(before.Default) && (string.IsNullOrWhiteSpace(column.Default) || column.IsIdentity))
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} DROP DEFAULT;");
            if (before.IsIdentity && !column.IsIdentity)
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} DROP IDENTITY IF EXISTS;");
            if (typeChanged)
            {
                // USING only where the server has no cast of its own: an explicit cast would cut a value that does not fit.
                var convert = !ColumnTypes.PostgresConvertsByItself(ColumnTypes.Family(before), ColumnTypes.Family(column)) ? $" USING {q}::{column.FullType}" : "";
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} TYPE {column.FullType}{convert};");
            }
            if (defaultChanged && !string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity)
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} SET DEFAULT {column.Default.Trim()};");
            if (fillNulls) steps.Add($"UPDATE {table} SET {q} = {column.Default!.Trim()} WHERE {q} IS NULL;");
            // An identity needs NOT NULL first; a column of a new primary key gets it from ADD PRIMARY KEY.
            if (nullChanged && !(column.IsPrimaryKey && keyChanged && !column.IsIdentity))
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} {(notNull ? "SET" : "DROP")} NOT NULL;");
            if (!before.IsIdentity && column.IsIdentity)
                steps.Add($"ALTER TABLE {table} ALTER COLUMN {q} ADD{d.IdentityClause};");
        }

        // ----- What is new -----
        foreach (var column in added)
        {
            var line = $"ALTER TABLE {table} ADD {(sqlServer ? "" : "COLUMN ")}{d.Quote(column.Name.Trim())} {(column.FullType.Length > 0 ? column.FullType : "?")}";
            if (column.IsIdentity) line += d.IdentityClause;
            line += column.WritesNotNull || column.IsIdentity ? " NOT NULL" : " NULL";
            if (!string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity) line += " DEFAULT " + column.Default.Trim();
            steps.Add(line + ";");
        }

        if (keyChanged && newKey.Count > 0)
        {
            var renamed = edited with { PrimaryKeyName = oldKey.Count > 0 && original.PrimaryKeyName.Length > 0 ? original.PrimaryKeyName : "" };
            steps.Add($"ALTER TABLE {table} ADD CONSTRAINT {d.Quote(TableScriptBuilder.PrimaryKeyName(renamed, d))} " +
                      $"PRIMARY KEY ({string.Join(", ", edited.PrimaryKey.Select(c => d.Quote(c.Name.Trim())))});");
        }

        steps.AddRange(mySqlLater);

        foreach (var fk in addKeys)
            steps.Add($"ALTER TABLE {table} ADD CONSTRAINT {d.Quote(TableScriptBuilder.ForeignKeyName(edited, fk, d))} FOREIGN KEY ({d.Quote(fk.Column.Trim())})\n" +
                      $"    REFERENCES {d.Table(fk.ReferencedSchema, fk.ReferencedTable)} ({d.Quote(fk.ReferencedColumn.Trim())});");

        foreach (var index in addIndexes)
            steps.Add($"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX {d.Quote(TableScriptBuilder.IndexName(edited, index, d))} ON {table} " +
                      $"({string.Join(", ", index.Columns.Select(c => d.Quote(c.Trim())))});");

        return steps;
    }

    /// <summary>True when nothing would change on the server.</summary>
    public static bool IsUnchanged(TableDesign original, TableDesign edited, string providerKey) =>
        Steps(original, edited, providerKey).Count == 0;

    /// <summary>The statement that drops a foreign key of <paramref name="table"/> (already quoted).</summary>
    public static string DropForeignKey(string providerKey, string table, string name)
    {
        var d = SqlDialect.For(providerKey);
        return providerKey == SqlDialect.MySqlKey
            ? $"ALTER TABLE {table} DROP FOREIGN KEY {d.Quote(name)};"
            : $"ALTER TABLE {table} DROP CONSTRAINT {d.Quote(name)};";
    }

    private static string MySqlModify(SqlDialect d, string table, string quotedName, ColumnDesign column, ColumnDesign before)
    {
        var line = $"ALTER TABLE {table} MODIFY COLUMN {quotedName} {(column.FullType.Length > 0 ? column.FullType : before.FullType)}";
        if (column.IsIdentity) line += d.IdentityClause;
        line += column.WritesNotNull || column.IsIdentity ? " NOT NULL" : " NULL";
        if (!string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity) line += " DEFAULT " + column.Default.Trim();
        return line + ";";
    }

    /// <summary>SQL Server names a column's default constraint itself (DF__Orders__Statu__3B75D760), so it is looked up.</summary>
    private static string DropSqlServerDefault(SqlDialect d, string table, string column) =>
        "DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc\n" +
        "    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id\n" +
        $"    WHERE dc.parent_object_id = OBJECT_ID({d.Literal(table)}) AND c.name = {d.Literal(column)});\n" +
        $"IF @df IS NOT NULL EXEC(N'ALTER TABLE {table.Replace("'", "''")} DROP CONSTRAINT ' + QUOTENAME(@df));";

    private static bool SameKey(ForeignKeyDesign before, ForeignKeyDesign after, Func<string, string> toOriginal) =>
        TableDesign.Same(before.Column, toOriginal(after.Column)) &&
        TableDesign.Same(before.ReferencedSchema, after.ReferencedSchema) &&
        TableDesign.Same(before.ReferencedTable, after.ReferencedTable) &&
        TableDesign.Same(before.ReferencedColumn, after.ReferencedColumn.Trim());

    private static bool SameIndex(IndexDesign before, IndexDesign after, Func<string, string> toOriginal) =>
        before.IsUnique == after.IsUnique &&
        before.Columns.SequenceEqual(after.Columns.Select(toOriginal), StringComparer.OrdinalIgnoreCase);

    public static bool SameDefault(string? a, string? b) => Normalize(a) == Normalize(b);

    private static string Normalize(string? expression) =>
        new string((expression ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}
