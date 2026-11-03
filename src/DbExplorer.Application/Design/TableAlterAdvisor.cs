using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Design;

/// <summary>
/// What changing an existing table risks, next to <see cref="TableDesignAdvisor"/>'s rules: dropped columns and their
/// data, types that existing values may not fit, NOT NULL on a column that has empty rows, keys other tables depend on,
/// and renames that break code. Each risky change offers to undo just that change.
/// </summary>
public static class TableAlterAdvisor
{
    public static IReadOnlyList<DesignSuggestion> Review(TableDesign original, TableDesign design, DesignContext context)
    {
        var provider = context.ProviderKey;
        var sqlServer = provider == SqlDialect.SqlServerKey;
        var result = new List<DesignSuggestion>();
        void Add(string key, DesignSeverity severity, string title, string detail, string? fixLabel = null, Func<TableDesign, TableDesign>? fix = null) =>
            result.Add(new DesignSuggestion(key, severity, title, detail, fixLabel, fix));

        var columns = design.Columns.Where(c => c.Name.Trim().Length > 0).ToList();
        var incoming = context.ForeignKeys
            .Where(f => TableDesign.Same(f.ReferencedSchema, original.Schema) && TableDesign.Same(f.ReferencedTable, original.Name) &&
                        !(TableDesign.Same(f.Schema, original.Schema) && TableDesign.Same(f.Table, original.Name)))
            .ToList();

        // ----- Columns that go away -----
        foreach (var gone in original.Columns.Where(o => !columns.Any(c => c.OriginalName is { } n && TableDesign.Same(n, o.Name))))
        {
            var users = incoming.Where(f => (f.ReferencedColumns ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(gone.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var at = original.Columns.ToList().IndexOf(gone);
            Func<TableDesign, TableDesign> keep = d => d with { Columns = [.. d.Columns.Take(at), gone, .. d.Columns.Skip(at)] };
            if (users.Count > 0)
                Add($"alter-drop:{gone.Name}", DesignSeverity.Error, $"Other tables reference {gone.Name}",
                    $"{string.Join(", ", users.Select(f => $"{f.Schema}.{f.Table} ({f.Name})"))} point at it. Drop those foreign keys first.",
                    $"Keep {gone.Name}", keep);
            else
                Add($"alter-drop:{gone.Name}", DesignSeverity.Warning, $"Dropping {gone.Name} deletes its data",
                    "Every value in the column is lost for good, and views or procedures that use it stop working.",
                    $"Keep {gone.Name}", keep);
        }

        // ----- Columns that change -----
        foreach (var column in columns.Where(c => c.OriginalName is not null))
        {
            if (original.Column(column.OriginalName!) is not { } before) continue;
            var name = column.Name.Trim();

            if (column.FullType.Length > 0 && ColumnTypes.RiskOfChange(provider, before, column) is { } risk)
                Add($"alter-type:{column.OriginalName}", DesignSeverity.Warning, $"{name}: {before.FullType} → {column.FullType} may not fit existing values",
                    risk, $"Keep {before.FullType}", d => d.WithColumn(name, c => c with { Type = before.Type, Size = before.Size }));

            if (before.IsIdentity != column.IsIdentity && sqlServer)
                Add($"alter-identity:{column.OriginalName}", DesignSeverity.Error,
                    column.IsIdentity ? $"SQL Server cannot make {name} an identity" : $"SQL Server cannot remove the identity from {name}",
                    "IDENTITY is fixed when a column is created. Add a new column instead, or keep this one as it is.",
                    "Undo", d => d.WithColumn(name, c => c with { IsIdentity = before.IsIdentity }));

            if (!before.WritesNotNull && !before.IsIdentity && (column.WritesNotNull || column.IsIdentity))
            {
                if (!string.IsNullOrWhiteSpace(column.Default) && !column.IsIdentity)
                    Add($"alter-not-null:{column.OriginalName}", DesignSeverity.Tip, $"Empty {name} values get {column.Default.Trim()}",
                        "The script fills rows where it is NULL with the default first, then makes the column NOT NULL.");
                else
                    Add($"alter-not-null:{column.OriginalName}", DesignSeverity.Warning, $"{name} becomes NOT NULL",
                        "The change fails if any row has no value. Give the column a default to fill those rows, or keep it nullable.",
                        "Allow NULL", d => d.WithColumn(name, c => c with { IsNullable = true }));
            }
        }

        // ----- New columns -----
        foreach (var column in columns.Where(c => c.OriginalName is null || original.Column(c.OriginalName) is null))
            if (column.WritesNotNull && !column.IsIdentity && string.IsNullOrWhiteSpace(column.Default) && !column.IsPrimaryKey)
                Add($"alter-add-not-null:{column.Name.Trim()}", DesignSeverity.Warning, $"New column {column.Name.Trim()} is NOT NULL with no default",
                    "Adding it fails if the table already has rows, since they would have no value. Give it a default, or allow NULL.",
                    "Allow NULL", d => d.WithColumn(column.Name, c => c with { IsNullable = true }));

        // ----- Primary key -----
        string Original(ColumnDesign c) => c.OriginalName ?? "\u0001" + c.Name;
        var oldKey = original.PrimaryKey.Select(c => c.Name).ToList();
        var newKey = design.PrimaryKey.Select(Original).ToList();
        if (oldKey.Count > 0 && !oldKey.SequenceEqual(newKey, StringComparer.OrdinalIgnoreCase) && incoming.Count > 0)
            Add("alter-key", DesignSeverity.Error, "Other tables reference this primary key",
                $"{string.Join(", ", incoming.Take(4).Select(f => $"{f.Schema}.{f.Table}"))}{(incoming.Count > 4 ? "…" : "")} depend on it, so it cannot be dropped and recreated. Drop those foreign keys first.",
                "Keep the current key", d => d with
                {
                    Columns = d.Columns.Select(c => c with
                    {
                        IsPrimaryKey = c.OriginalName is { } n && oldKey.Contains(n, StringComparer.OrdinalIgnoreCase),
                        IsNullable = c.OriginalName is { } m && oldKey.Contains(m, StringComparer.OrdinalIgnoreCase) ? false : c.IsNullable
                    }).ToList()
                });

        // ----- Renames -----
        if (!string.Equals(design.Name.Trim(), original.Name, StringComparison.Ordinal) && design.Name.Trim().Length > 0)
            Add("alter-rename-table", DesignSeverity.Tip, $"Renaming {original.Name} breaks code that uses the old name",
                "Foreign keys follow the table, but views, procedures and saved queries that name it stop working.",
                $"Keep {original.Name}", d => d with { Name = original.Name });

        var renamed = columns.Where(c => c.OriginalName is { } n && original.Column(n) is not null && !string.Equals(c.Name.Trim(), n, StringComparison.Ordinal)).ToList();
        if (renamed.Count > 0)
            Add("alter-rename-columns", DesignSeverity.Tip,
                renamed.Count == 1 ? $"Renaming {renamed[0].OriginalName} to {renamed[0].Name.Trim()}" : $"Renaming {renamed.Count} columns",
                "Keys and indexes follow the new name, but views, procedures and queries that use the old one stop working.",
                renamed.Count == 1 ? $"Keep {renamed[0].OriginalName}" : "Keep the old names",
                d => renamed.Aggregate(d, (acc, c) => acc.RenameColumn(c.Name, c.OriginalName!)));

        // ----- Column order -----
        var keptOrder = columns.Where(c => c.OriginalName is { } n && original.Column(n) is not null).Select(c => c.OriginalName!).ToList();
        var serverOrder = original.Columns.Select(c => c.Name).Where(n => keptOrder.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var firstNew = columns.FindIndex(c => c.OriginalName is null || original.Column(c.OriginalName) is null);
        var lastKept = columns.FindLastIndex(c => c.OriginalName is { } n && original.Column(n) is not null);
        if (!keptOrder.SequenceEqual(serverOrder, StringComparer.OrdinalIgnoreCase) || (firstNew >= 0 && firstNew < lastKept))
            Add("alter-order", DesignSeverity.Tip, "Column order is not changed",
                "ALTER TABLE cannot move columns: existing ones keep their place and new ones are added at the end. Select lists decide the order queries show.");

        if (provider == SqlDialect.SqliteKey)
            foreach (var (key, title, detail) in SqliteLimits(original, design))
                Add(key, DesignSeverity.Error, title, detail);

        return result;
    }

    private const string SqliteRebuild =
        " Copy the table to a new one with the shape you want (CREATE TABLE … then INSERT … SELECT), drop the old one and rename the new one.";

    /// <summary>
    /// What SQLite's ALTER TABLE cannot do in place. It renames tables and columns, adds columns and drops them; a
    /// changed type, NULL rule, default, key or foreign key needs the table rebuilt, which the designer doesn't do for you.
    /// </summary>
    public static IEnumerable<(string Key, string Title, string Detail)> SqliteLimits(TableDesign original, TableDesign design)
    {
        var columns = design.Columns.Where(c => c.Name.Trim().Length > 0).ToList();
        foreach (var column in columns.Where(c => c.OriginalName is { } n && original.Column(n) is not null))
        {
            var before = original.Column(column.OriginalName!)!;
            var changes = new List<string>();
            if (ColumnTypes.Canonical(SqlDialect.SqliteKey, before.FullType) != ColumnTypes.Canonical(SqlDialect.SqliteKey, column.FullType)) changes.Add("type");
            if (before.WritesNotNull != column.WritesNotNull) changes.Add("NULL rule");
            if (!TableAlterScriptBuilder.SameDefault(before.Default, column.Default)) changes.Add("default");
            if (before.IsIdentity != column.IsIdentity) changes.Add("identity");
            if (changes.Count > 0)
                yield return ($"sqlite-alter:{column.OriginalName}", $"SQLite can't change the {string.Join(", ", changes)} of {column.OriginalName}",
                    "SQLite's ALTER TABLE only renames, adds and drops columns." + SqliteRebuild);
        }

        foreach (var column in columns.Where(c => c.OriginalName is null || original.Column(c.OriginalName) is null))
        {
            if (column.IsPrimaryKey || column.IsIdentity)
                yield return ($"sqlite-add:{column.Name}", $"SQLite can't add {column.Name.Trim()} as a key column",
                    "ALTER TABLE ADD COLUMN can't add a PRIMARY KEY or identity column." + SqliteRebuild);
            else if (column.WritesNotNull && string.IsNullOrWhiteSpace(column.Default))
                yield return ($"sqlite-add:{column.Name}", $"{column.Name.Trim()} is NOT NULL without a default",
                    "SQLite adds a NOT NULL column only with a default for the rows already there. Give it a default or allow NULL.");
        }

        var oldKey = original.PrimaryKey.Select(c => c.Name).ToList();
        var newKey = design.PrimaryKey.Select(c => c.OriginalName ?? "\u0001" + c.Name).ToList();
        if (!oldKey.SequenceEqual(newKey, StringComparer.OrdinalIgnoreCase) && !newKey.Any(k => k.StartsWith('\u0001')))
            yield return ("sqlite-key", "SQLite can't change the primary key in place", "The key is part of the table's definition." + SqliteRebuild);

        static string Key(ForeignKeyDesign f) => $"{f.Column.Trim()}→{f.ReferencedTable.Trim()}.{f.ReferencedColumn.Trim()}".ToLowerInvariant();
        var oldKeys = original.ForeignKeys.Select(Key).ToHashSet();
        var newKeys = design.ForeignKeys.Where(f => f.Column.Trim().Length > 0 && f.ReferencedTable.Trim().Length > 0)
            .Select(f => Key(f with { Column = design.Column(f.Column)?.OriginalName ?? f.Column })).ToHashSet();
        if (!oldKeys.SetEquals(newKeys))
            yield return ("sqlite-fk", "SQLite can't add or drop a foreign key in place",
                "Foreign keys are part of the table's definition." + SqliteRebuild);

        foreach (var index in original.Indexes.Where(i => i.IsConstraint && !design.Indexes.Any(e => TableDesign.Same(e.Name.Trim(), i.Name))))
            yield return ($"sqlite-unique:{index.Name}", $"{index.Name} belongs to a UNIQUE constraint",
                "SQLite drops it only with the table." + SqliteRebuild);
    }

    /// <summary>True when the server would see no difference in the column (name, type, nullability, key, identity, default).</summary>
    public static bool SameColumn(ColumnDesign before, ColumnDesign after, string providerKey) =>
        string.Equals(before.Name, after.Name.Trim(), StringComparison.Ordinal) &&
        ColumnTypes.Canonical(providerKey, before.FullType) == ColumnTypes.Canonical(providerKey, after.FullType) &&
        before.WritesNotNull == after.WritesNotNull && before.IsPrimaryKey == after.IsPrimaryKey && before.IsIdentity == after.IsIdentity &&
        TableAlterScriptBuilder.SameDefault(before.Default, after.Default);
}
