using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Modeling;

public enum ModelTableAction
{
    Create,
    Alter,
    Unchanged
}

/// <summary>What Generate DDL does with one model table.</summary>
/// <param name="Original">The table as the target database has it now (read fresh), for <see cref="ModelTableAction.Alter"/>.</param>
public sealed record ModelTablePlan(ModelTable Table, ModelTableAction Action, TableDesign? Original, IReadOnlyList<DesignSuggestion> Findings);

/// <summary>The script that makes a database match the model, and what it would do.</summary>
public sealed record ModelScript(string Script, IReadOnlyList<ModelTablePlan> Tables, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public int Created => Tables.Count(t => t.Action == ModelTableAction.Create);
    public int Altered => Tables.Count(t => t.Action == ModelTableAction.Alter);
    public int Unchanged => Tables.Count(t => t.Action == ModelTableAction.Unchanged);
    public bool HasChanges => Created + Altered > 0;
    public bool CanRun => HasChanges && Errors.Count == 0;

    public string Summary => !HasChanges ? "The database already matches the model."
        : $"Creates {Created} table(s), changes {Altered}, leaves {Unchanged} as they are." +
          (Errors.Count > 0 ? $" {Errors.Count} problem(s) to fix first." : Warnings.Count > 0 ? $" {Warnings.Count} change(s) risk existing data." : "");
}

/// <summary>
/// Turns a model into the DDL for one database. A table read from a database is found there by the name it had when it
/// was read (so renaming it in the model renames it on the server) and diffed into ALTER statements by
/// <see cref="TableAlterScriptBuilder"/>; any other table is created. Tables the database has and the model does not
/// are never touched or dropped.
/// <para>Statements are ordered so each one is valid whatever the foreign keys between tables: new schemas, foreign keys
/// that go away, then each table's CREATE or ALTER, then every new foreign key last (so a key may reference a table the
/// script creates further down, or one that references back).</para>
/// </summary>
public static class ErModelScriptBuilder
{
    public const string NothingToDo = "-- The database already matches the model. Nothing to run.\n";

    /// <summary>
    /// The model aimed at another database. Where the schema is the database (MySQL), a table read from <c>prod</c> keeps
    /// <c>prod</c> as its schema, so a script for <c>staging</c> would create and change tables in <c>prod</c>: the
    /// model's own database (and an empty schema) is replaced by <paramref name="database"/> in every table, baseline
    /// and foreign key. Other schemas, and every other engine, are left as they are.
    /// </summary>
    public static ErModel ForDatabase(ErModel model, string providerKey, string? database)
    {
        if (!SqlDialect.SchemaIsDatabase(providerKey) || string.IsNullOrEmpty(database) || TableDesign.Same(model.Database, database))
            return model;
        var source = model.Database;
        string Map(string schema) => string.IsNullOrWhiteSpace(schema) || TableDesign.Same(schema.Trim(), source) ? database : schema;
        TableDesign Retarget(TableDesign design) => design with
        {
            Database = database,
            Schema = Map(design.Schema),
            ForeignKeys = design.ForeignKeys.Select(f => f with { ReferencedSchema = Map(f.ReferencedSchema) }).ToList()
        };
        return model with
        {
            Database = database,
            Tables = model.Tables.Select(t => t with { Design = Retarget(t.Design), Baseline = t.Baseline is null ? null : Retarget(t.Baseline) }).ToList()
        };
    }

    /// <summary>The model tables that exist in <paramref name="target"/>: their current definition (with column defaults,
    /// read from the server) is what the ALTER script starts from.</summary>
    /// <param name="targetDatabase">The database the script is for; see <see cref="ForDatabase"/>.</param>
    public static IReadOnlyDictionary<string, DbObject> Matches(ErModel model, MetadataSnapshot target, string providerKey, string? targetDatabase = null)
    {
        model = ForDatabase(model, providerKey, targetDatabase);
        var result = new Dictionary<string, DbObject>();
        DbObject? Find(string schema, string name) => target.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table &&
            TableDesign.Same(o.Schema, schema) && TableDesign.Same(o.Name, name.Trim()));
        foreach (var t in model.Tables.Where(t => t.Baseline is not null))
        {
            // By the name it was read with; failing that by its own name, for a rename that was already run elsewhere
            // (the script opened in a query tab). A table drawn here is never matched: its name being taken is an error.
            var found = Find(TableScriptBuilder.SchemaOf(t.Baseline!, providerKey), t.Baseline!.Name) ?? Find(t.Schema(providerKey), t.Name);
            if (found is not null && !result.ContainsValue(found)) result[t.Id] = found;
        }
        return result;
    }

    /// <param name="target">The catalog of the database the script is for.</param>
    /// <param name="constraints">Column defaults of the <see cref="Matches"/> tables; missing ones count as none.</param>
    /// <param name="targetDatabase">The database the script is for; see <see cref="ForDatabase"/>.</param>
    public static ModelScript Build(
        ErModel model, MetadataSnapshot target, string providerKey, IReadOnlyDictionary<DbObject, DbTableConstraints>? constraints = null,
        string? targetDatabase = null)
    {
        model = ForDatabase(model, providerKey, targetDatabase);
        var d = SqlDialect.For(providerKey);
        var matches = Matches(model, target, providerKey);
        var errors = new List<string>();
        var warnings = new List<string>();
        if (!string.Equals(model.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"The model's column types were written for {Engine(model.ProviderKey)}; this database is {Engine(providerKey)}. Check the types in the script.");

        var plans = new List<ModelTablePlan>();
        foreach (var table in model.Tables)
        {
            TableDesign? original = null;
            var design = table.Design;
            if (matches.TryGetValue(table.Id, out var found))
            {
                original = TableDesignLoader.Load(found, target, constraints?.GetValueOrDefault(found) ?? DbTableConstraints.None, providerKey).Design;
                design = Rebase(design, original);
            }

            var context = ErModelContext.For(model, target, providerKey, table.Id);
            var findings = TableDesignAdvisor.Review(design, context, original);
            var label = table.Name.Length > 0 ? table.FullName(providerKey) : "A table";
            foreach (var f in findings.Where(f => f.Severity == DesignSeverity.Error)) errors.Add($"{label}: {f.Title}");
            foreach (var f in findings.Where(f => f.Severity == DesignSeverity.Warning && f.Key.StartsWith("alter-", StringComparison.Ordinal)))
                warnings.Add($"{label}: {f.Title}");

            if (original is not null) design = FollowRenamedParents(design, original, model, matches, providerKey);
            var action = original is null ? ModelTableAction.Create
                : TableAlterScriptBuilder.IsUnchanged(original, design, providerKey) ? ModelTableAction.Unchanged
                : ModelTableAction.Alter;
            plans.Add(new ModelTablePlan(table with { Design = design }, action, original, findings));
        }

        var schemaSteps = new List<string>();
        var dropKeys = new List<string>();
        var tableSteps = new List<string>();
        var addKeys = new List<string>();

        var knownSchemas = target.Objects.Select(o => o.Schema).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in plans.Where(p => p.Action != ModelTableAction.Unchanged)
                     .Select(p => p.Table.Schema(providerKey)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!knownSchemas.Contains(schema) && !TableDesign.Same(schema, TableScriptBuilder.DefaultSchema(providerKey, model.Database)))
                schemaSteps.Add(d.CreateSchemaIfMissing(schema));

        foreach (var plan in plans.Where(p => p.Action == ModelTableAction.Create))
        {
            var design = plan.Table.Design;
            var table = d.Table(TableScriptBuilder.SchemaOf(design, providerKey), TableScriptBuilder.NameOf(design));
            tableSteps.Add($"-- New table {plan.Table.FullName(providerKey)}\n" + TableScriptBuilder.Build(design with { ForeignKeys = [] }, providerKey).TrimEnd());
            foreach (var fk in design.ForeignKeys.Where(f => f.Column.Trim().Length > 0 && f.ReferencedTable.Trim().Length > 0))
                addKeys.Add(AddKey(d, table, design, fk));
        }

        foreach (var plan in plans.Where(p => p.Action == ModelTableAction.Alter))
        {
            var original = plan.Original!;
            var design = plan.Table.Design;
            var newTable = d.Table(TableScriptBuilder.SchemaOf(design, providerKey), TableScriptBuilder.NameOf(design));
            var oldTable = d.Table(TableScriptBuilder.SchemaOf(original, providerKey), original.Name.Trim());
            var drops = original.ForeignKeys.ToDictionary(f => $"ALTER TABLE {newTable} DROP CONSTRAINT {d.Quote(f.Name)};", f => f.Name);

            var steps = new List<string>();
            foreach (var step in TableAlterScriptBuilder.Steps(original, design, providerKey))
            {
                // Moved out: keys go before any table changes (a key blocks changing the column it references), and are
                // added after all of them (the column or table a key references may be added further down).
                if (drops.TryGetValue(step, out var name)) dropKeys.Add($"ALTER TABLE {oldTable} DROP CONSTRAINT {d.Quote(name)};");
                else if (step.StartsWith("ALTER TABLE ", StringComparison.Ordinal) && step.Contains(" ADD CONSTRAINT ", StringComparison.Ordinal) &&
                         step.Contains(" FOREIGN KEY (", StringComparison.Ordinal)) addKeys.Add(step);
                else steps.Add(step);
            }
            if (steps.Count > 0)
            {
                steps[0] = $"-- Changes to {plan.Original!.Schema}.{plan.Original.Name}\n" + steps[0];
                tableSteps.AddRange(steps);
            }
        }

        var all = schemaSteps.Concat(dropKeys).Concat(tableSteps).Concat(addKeys).ToList();
        if (all.Count == 0) return new ModelScript(NothingToDo, plans, errors, warnings);

        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        var created = plans.Count(p => p.Action == ModelTableAction.Create);
        var altered = plans.Count(p => p.Action == ModelTableAction.Alter);
        var header = $"-- ER model \"{model.Name}\": creates {created} table(s), changes {altered}.\n" +
                     "-- Tables of the database that are not in the model are left as they are.\n";
        foreach (var e in errors) header += "-- Fix first: " + e + "\n";
        foreach (var w in warnings) header += "-- Check: " + w + "\n";
        var script = header + string.Concat(all.Select(s => s + (sqlServer ? "\nGO\n" : "\n")));
        return new ModelScript(script, plans, errors, warnings);
    }

    /// <summary>
    /// The model's design of a table made to line up with what the server has now: columns are matched by the name they
    /// had when the model read the table (or, failing that, by their own name), and keys and indexes the script named
    /// automatically get the server's name back, so a second Generate DDL finds nothing to do.
    /// </summary>
    public static TableDesign Rebase(TableDesign design, TableDesign original)
    {
        var claimed = design.Columns.Select(c => c.OriginalName).Where(n => n is not null && original.Column(n) is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = design.Columns.Select(c =>
        {
            if (c.OriginalName is not null && original.Column(c.OriginalName) is not null) return c;
            var same = original.Column(c.Name);
            return c with { OriginalName = same is not null && claimed.Add(same.Name) ? same.Name : null };
        }).ToList();

        string ToOriginal(string column) =>
            columns.FirstOrDefault(c => TableDesign.Same(c.Name.Trim(), column.Trim()))?.OriginalName ?? "\u0001" + column;

        var keys = design.ForeignKeys.Select(f =>
        {
            if (f.Name.Trim().Length > 0) return f;
            var match = original.ForeignKeys.FirstOrDefault(o => TableDesign.Same(o.Column, ToOriginal(f.Column)) &&
                                                                 TableDesign.Same(o.ReferencedSchema, f.ReferencedSchema) &&
                                                                 TableDesign.Same(o.ReferencedTable, f.ReferencedTable) &&
                                                                 TableDesign.Same(o.ReferencedColumn, f.ReferencedColumn.Trim()));
            return match is null ? f : f with { Name = match.Name };
        }).ToList();

        var indexes = design.Indexes.Select(i =>
        {
            if (i.Name.Trim().Length > 0) return i;
            var match = original.Indexes.FirstOrDefault(o => o.IsUnique == i.IsUnique &&
                                                             o.Columns.SequenceEqual(i.Columns.Select(ToOriginal), StringComparer.OrdinalIgnoreCase));
            return match is null ? i : i with { Name = match.Name };
        }).ToList();

        return design with
        {
            Database = original.Database,
            Columns = columns,
            ForeignKeys = keys,
            Indexes = indexes,
            PrimaryKeyName = design.PrimaryKeyName.Trim().Length > 0 ? design.PrimaryKeyName : original.PrimaryKeyName
        };
    }

    /// <summary>
    /// A key whose parent table (or its key column) is renamed by the same script still references it on the server:
    /// both engines carry keys along with a rename. Such a key is written with the parent's server names, so the ALTER
    /// script leaves it alone instead of dropping and adding it again.
    /// </summary>
    private static TableDesign FollowRenamedParents(
        TableDesign design, TableDesign original, ErModel model, IReadOnlyDictionary<string, DbObject> matches, string providerKey)
    {
        var keys = design.ForeignKeys.Select(fk =>
        {
            var before = original.ForeignKeys.FirstOrDefault(o => fk.Name.Trim().Length > 0 && TableDesign.Same(o.Name, fk.Name.Trim()));
            var schema = string.IsNullOrWhiteSpace(fk.ReferencedSchema) ? TableScriptBuilder.DefaultSchema(providerKey, model.Database) : fk.ReferencedSchema;
            if (before is null || model.FindByName(schema, fk.ReferencedTable) is not { Baseline: { } baseline } parent || !matches.ContainsKey(parent.Id))
                return fk;
            var column = parent.Design.Column(fk.ReferencedColumn) is { } c ? c.OriginalName ?? c.Name.Trim() : fk.ReferencedColumn.Trim();
            var sameParent = TableDesign.Same(before.ReferencedSchema, TableScriptBuilder.SchemaOf(baseline, providerKey)) &&
                             TableDesign.Same(before.ReferencedTable, baseline.Name.Trim()) && TableDesign.Same(before.ReferencedColumn, column);
            return sameParent ? fk with { ReferencedSchema = before.ReferencedSchema, ReferencedTable = before.ReferencedTable, ReferencedColumn = before.ReferencedColumn } : fk;
        }).ToList();
        return design with { ForeignKeys = keys };
    }

    private static string AddKey(SqlDialect d, string table, TableDesign design, ForeignKeyDesign fk) =>
        $"ALTER TABLE {table} ADD CONSTRAINT {d.Quote(TableScriptBuilder.ForeignKeyName(design, fk, d))} FOREIGN KEY ({d.Quote(fk.Column.Trim())})\n" +
        $"    REFERENCES {d.Table(fk.ReferencedSchema, fk.ReferencedTable)} ({d.Quote(fk.ReferencedColumn.Trim())});";

    private static string Engine(string providerKey) => SqlDialect.EngineName(providerKey);
}
