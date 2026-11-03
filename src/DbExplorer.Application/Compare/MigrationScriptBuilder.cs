using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

public enum MigrationAction
{
    Create,
    Alter,
    Replace,
    Drop,

    /// <summary>Differs (or is missing) but the script cannot do it: the details say what to do by hand.</summary>
    Manual,
    Unchanged
}

/// <summary>What the migration script does to one object of the target database.</summary>
/// <param name="Schema">The schema on the target side.</param>
public sealed record MigrationItem(MigrationAction Action, DbObjectType Type, string Schema, string Name, string Details)
{
    public string FullName => $"{Schema}.{Name}";

    public bool IsCreate => Action == MigrationAction.Create;
    public bool IsChange => Action is MigrationAction.Alter or MigrationAction.Replace;
    public bool IsDrop => Action == MigrationAction.Drop;
    public bool IsManual => Action == MigrationAction.Manual;
}

/// <summary>One side of a migration: the catalog of a database and what was read from the server besides it.</summary>
/// <param name="Database">The database on that side; null or empty when the connection holds a single database.</param>
/// <param name="Constraints">Column defaults of the tables (read from the server); a missing table counts as having none.</param>
public sealed record MigrationSide(
    MetadataSnapshot Snapshot, string ProviderKey, string? Database,
    IReadOnlyDictionary<DbObject, DbTableConstraints>? Constraints = null);

public sealed record MigrationOptions
{
    /// <summary>Also drop what only the target has: tables, columns, keys, indexes, views and routines. Off, those are
    /// listed in the script as comments and left alone.</summary>
    public bool DropExtras { get; init; }

    /// <summary>Only these objects (type, schema, name; the source schema, or the target schema for an object only the
    /// target has). Null: every object of the two databases.</summary>
    public IReadOnlyCollection<(DbObjectType Type, string Schema, string Name)>? Only { get; init; }

    /// <summary>Who the script is for, in its header: "dev · Shop → t01 · Shop".</summary>
    public string? Title { get; init; }
}

/// <summary>The script and what it would do.</summary>
public sealed record MigrationScript(string Script, IReadOnlyList<MigrationItem> Items, IReadOnlyList<string> Warnings)
{
    public int Count(MigrationAction action) => Items.Count(i => i.Action == action);
    public bool HasChanges => Items.Any(i => i.Action is MigrationAction.Create or MigrationAction.Alter or MigrationAction.Replace or MigrationAction.Drop);

    public string Summary => Items.Count == 0 && Warnings.Count > 0 ? Warnings[0]
        : !HasChanges && Count(MigrationAction.Manual) == 0 ? "The target already matches the source. Nothing to run."
        : $"Creates {Count(MigrationAction.Create)}, alters {Count(MigrationAction.Alter)}, replaces {Count(MigrationAction.Replace)}" +
          $", drops {Count(MigrationAction.Drop)}" +
          (Count(MigrationAction.Manual) > 0 ? $"; {Count(MigrationAction.Manual)} need(s) a hand" : "") +
          $"; {Count(MigrationAction.Unchanged)} already match." +
          (Warnings.Count > 0 ? $" {Warnings.Count} thing(s) to check before running." : "");
}

/// <summary>
/// The script that makes one database's schema match another's: a whole-database (or selected objects) version of the
/// Comparer, built from the cached catalogs of both sides plus the column defaults read from the servers. It reuses the
/// Table designer's builders, so tables are created with <see cref="TableScriptBuilder"/> and changed with
/// <see cref="TableAlterScriptBuilder"/>; views, routines and triggers are created from the source definitions, or
/// replaced (CREATE OR ALTER / CREATE OR REPLACE) when they differ. Nothing runs: the script is for review.
/// <para>Order: new schemas; drops of extra views, routines and triggers; foreign keys that go away; each table's
/// CREATE or ALTER (parents first); extra tables dropped; new foreign keys; then views (dependencies first),
/// functions, procedures and triggers.</para>
/// <para>Only the same engine on both sides: a cross-engine move goes through Copy &amp; sync, which maps types.</para>
/// </summary>
public static class MigrationScriptBuilder
{
    private static readonly Regex NextVal = new(@"nextval\('(?<name>[^']+)'(?:::regclass)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const string NothingToDo = "-- The target already matches the source. Nothing to run.\n";

    public static MigrationScript Build(MigrationSide source, MigrationSide target, MigrationOptions? options = null)
    {
        options ??= new MigrationOptions();
        var provider = target.ProviderKey;
        if (!string.Equals(source.ProviderKey, provider, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"A migration script needs the same engine on both sides (left is {SqlDialect.EngineName(source.ProviderKey)}, " +
                          $"right is {SqlDialect.EngineName(provider)}). Use Copy & sync to move objects between engines.";
            return new MigrationScript("-- " + message + "\n", [], [message]);
        }

        var d = SqlDialect.For(provider);
        var src = Scope(source);
        var dst = Scope(target);
        var items = new List<MigrationItem>();
        var warnings = new List<string>();

        string Map(string schema) => ObjectCopyService.MapSchema(schema, provider, provider, target.Database);
        bool Included(DbObjectType type, string schema, string name) =>
            options.Only is null || options.Only.Any(o => SameType(o.Type, type) && Same(o.Schema, schema) && Same(o.Name, name));

        var targetTables = dst.Objects.Where(o => o.Type == DbObjectType.Table)
            .GroupBy(o => Key(o.Schema, o.Name)).ToDictionary(g => g.Key, g => g.First());
        var sourceTables = ObjectCopyService.OrderForCopy(src.Objects.Where(o => o.Type == DbObjectType.Table), src)
            .Where(t => Included(t.Type, t.Schema, t.Name)).ToList();
        var sourceTableKeys = src.Objects.Where(o => o.Type == DbObjectType.Table).Select(o => Key(Map(o.Schema), o.Name)).ToHashSet();

        var schemaSteps = new List<string>();
        var dropModules = new List<string>();
        var dropKeys = new List<string>();
        var tableSteps = new List<string>();
        var dropTables = new List<string>();
        var addKeys = new List<string>();
        var moduleSteps = new List<string>();
        var leftAlone = new List<string>();

        // ----- Tables -----
        var knownSchemas = dst.Objects.Select(o => o.Schema).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sequenceSteps = new List<string>();
        var ownedSteps = new List<string>();
        var createdSequences = new HashSet<(string, string)>();

        // A PostgreSQL serial column's default reads its sequence (nextval('orders_id_seq'::regclass)): one the target lacks
        // is created first, and owned by its column afterwards like serial does.
        void NeedSequences(TableDesign design, string schema, string table)
        {
            if (provider != SqlDialect.PostgresKey) return;
            foreach (var column in design.Columns.Where(c => c.Default is not null))
            {
                var match = NextVal.Match(column.Default!);
                if (!match.Success) continue;
                var parts = match.Groups["name"].Value.Split('.');
                var seqSchema = parts.Length > 1 ? parts[0].Trim('"') : schema;
                var seqName = parts[^1].Trim('"');
                if (dst.Objects.Any(o => o.Type == DbObjectType.Sequence && Same(o.Schema, seqSchema) && Same(o.Name, seqName)) ||
                    !createdSequences.Add(Key(seqSchema, seqName))) continue;
                var quoted = d.Table(seqSchema, seqName);
                sequenceSteps.Add($"CREATE SEQUENCE IF NOT EXISTS {quoted};");
                var own = Same(seqName, $"{table}_{column.Name}_seq");
                if (own) ownedSteps.Add($"ALTER SEQUENCE {quoted} OWNED BY {d.Table(schema, table)}.{d.Quote(column.Name.Trim())};");
                else
                {
                    items.Add(new MigrationItem(MigrationAction.Create, DbObjectType.Sequence, seqSchema, seqName,
                        $"Used by the default of {schema}.{table}.{column.Name}; created with default options"));
                    warnings.Add($"{seqSchema}.{seqName}: created with the default start and increment; set them as on the source if they matter.");
                }
            }
        }
        var context = DesignContext.From(dst, provider);
        foreach (var table in sourceTables)
        {
            var schema = Map(table.Schema);
            var loaded = TableDesignLoader.Load(table, src, source.Constraints?.GetValueOrDefault(table) ?? DbTableConstraints.None, provider);
            var design = Retarget(loaded.Design, target.Database ?? "", Map);
            var notes = NotScripted(loaded, src, table);

            if (!targetTables.TryGetValue(Key(schema, table.Name), out var existing))
            {
                if (!knownSchemas.Contains(schema) && !Same(schema, TableScriptBuilder.DefaultSchema(provider, target.Database)))
                {
                    schemaSteps.Add(d.CreateSchemaIfMissing(schema));
                    knownSchemas.Add(schema);
                }
                NeedSequences(design, schema, table.Name);
                tableSteps.Add($"-- New table {schema}.{table.Name}\n" + TableScriptBuilder.Build(design with { ForeignKeys = [] }, provider).TrimEnd());
                var quoted = d.Table(schema, table.Name);
                foreach (var fk in design.ForeignKeys)
                {
                    addKeys.Add(AddKey(d, quoted, design, fk));
                    if (!targetTables.ContainsKey(Key(fk.ReferencedSchema, fk.ReferencedTable)) &&
                        !sourceTables.Any(t => Same(Map(t.Schema), fk.ReferencedSchema) && Same(t.Name, fk.ReferencedTable)))
                        warnings.Add($"{schema}.{table.Name}: its foreign key {fk.Name} references {fk.ReferencedSchema}.{fk.ReferencedTable}, which the target does not have and the script does not create.");
                }
                if (notes.Count > 0) warnings.Add($"{schema}.{table.Name}: {string.Join("; ", notes)}. Add those by hand.");
                items.Add(new MigrationItem(MigrationAction.Create, DbObjectType.Table, schema, table.Name,
                    Describe($"{design.Columns.Count} column(s)", notes)));
                continue;
            }

            var original = TableDesignLoader.Load(existing, dst, target.Constraints?.GetValueOrDefault(existing) ?? DbTableConstraints.None, provider).Design;
            design = Align(design, original, options.DropExtras);
            if (TableAlterScriptBuilder.IsUnchanged(original, design, provider))
            {
                items.Add(new MigrationItem(MigrationAction.Unchanged, DbObjectType.Table, schema, table.Name,
                    Describe("Same structure", notes)));
                continue;
            }

            var oldTable = d.Table(original.Schema, original.Name);
            NeedSequences(design, schema, table.Name);
            var drops = original.ForeignKeys.ToDictionary(f => TableAlterScriptBuilder.DropForeignKey(provider, oldTable, f.Name), f => f.Name);
            var steps = new List<string>();
            foreach (var step in TableAlterScriptBuilder.Steps(original, design, provider))
            {
                // Keys move out of the table's own steps, as in the ER model: dropped before any table changes and added
                // after all of them, so a key may reference a table or column the script adds further down.
                if (drops.ContainsKey(step)) dropKeys.Add(step);
                else if (step.StartsWith("ALTER TABLE ", StringComparison.Ordinal) && step.Contains(" ADD CONSTRAINT ", StringComparison.Ordinal) &&
                         step.Contains(" FOREIGN KEY (", StringComparison.Ordinal)) addKeys.Add(step);
                else steps.Add(step);
            }

            var changes = Changes(original, design, provider);
            if (steps.Count > 0)
            {
                steps[0] = $"-- Changes to {schema}.{table.Name}\n" + steps[0];
                tableSteps.AddRange(steps);
            }
            foreach (var finding in TableAlterAdvisor.Review(original, design, context)
                         .Where(f => f.Severity is DesignSeverity.Error or DesignSeverity.Warning))
                warnings.Add($"{schema}.{table.Name}: {finding.Title}. {finding.Detail}");
            if (notes.Count > 0) warnings.Add($"{schema}.{table.Name}: {string.Join("; ", notes)}. Compare those by hand.");
            items.Add(new MigrationItem(MigrationAction.Alter, DbObjectType.Table, schema, table.Name,
                Describe(changes.Count > 0 ? string.Join(", ", changes) : "Keys or indexes", notes)));
        }

        // Tables only the target has
        foreach (var extra in targetTables.Values.Where(t => !sourceTableKeys.Contains(Key(t.Schema, t.Name)) && Included(t.Type, t.Schema, t.Name))
                     .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var quoted = d.Table(extra.Schema, extra.Name);
            if (!options.DropExtras)
            {
                leftAlone.Add($"DROP TABLE {quoted};");
                items.Add(new MigrationItem(MigrationAction.Unchanged, DbObjectType.Table, extra.Schema, extra.Name, "Only on the target; left alone"));
                continue;
            }
            // Its own keys go first, so the tables can be dropped in any order.
            foreach (var fk in dst.ForeignKeysOf(extra.Database, extra.Schema, extra.Name).Where(f => !f.IsVirtual))
                dropKeys.Add(TableAlterScriptBuilder.DropForeignKey(provider, quoted, fk.Name));
            dropTables.Add(d.DropTable(quoted));
            items.Add(new MigrationItem(MigrationAction.Drop, DbObjectType.Table, extra.Schema, extra.Name,
                extra.RowCount is > 0 ? $"Only on the target; ≈ {extra.RowCount:N0} row(s) are lost" : "Only on the target"));
            if (extra.RowCount is > 0) warnings.Add($"{extra.FullName}: dropping it deletes about {extra.RowCount:N0} row(s).");
        }

        // ----- Views, routines and triggers -----
        var sourceModules = Modules(src, provider, target.Database, Map);
        var targetModules = Modules(dst, provider, target.Database, s => s);
        bool Differs(Module m) => !targetModules.TryGetValue(KeyOf(m), out var t) || Normalize(t.Definitions) != Normalize(m.Definitions);

        // PostgreSQL refuses to change a column a view reads, and replaces a view only when its columns stay the same: the
        // views on the target that read an altered table, or are replaced, are dropped before the table changes (with the
        // views that read them) and created again after.
        var rebuilt = new HashSet<(int, string, string)>();
        if (provider == SqlDialect.PostgresKey)
        {
            var altered = items.Where(i => i is { Action: MigrationAction.Alter, Type: DbObjectType.Table }).Select(i => i.Name).ToList();
            var views = targetModules.Where(m => m.Value.Type is DbObjectType.View or DbObjectType.MaterializedView && m.Value.Definitions.Count > 0).ToList();
            foreach (var (key, view) in views)
                if (altered.Any(t => Mentions(view.Text, t)) ||
                    sourceModules.TryGetValue(key, out var mine) && mine.Definitions.Count > 0 && Included(mine.Type, mine.SourceSchema, mine.Name) && Differs(mine))
                    rebuilt.Add(key);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (key, view) in views.Where(v => !rebuilt.Contains(v.Key)))
                    if (rebuilt.Any(r => Mentions(view.Text, targetModules[r].Name))) grew = rebuilt.Add(key);
            }
        }

        var create = new List<(DbObjectType Type, string Schema, string Name, string Statement)>();
        foreach (var (key, module) in sourceModules)
        {
            if (!Included(module.Type, module.SourceSchema, module.Name)) continue;
            targetModules.TryGetValue(key, out var existing);
            if (module.Definitions.Count == 0)
            {
                items.Add(existing is null
                    ? new MigrationItem(MigrationAction.Manual, module.Type, module.Schema, module.Name,
                        "The source definition is not readable (encrypted, or no permission); create it by hand")
                    : new MigrationItem(MigrationAction.Unchanged, module.Type, module.Schema, module.Name,
                        "On both sides; the source definition is not readable, so it was not compared"));
                continue;
            }
            var differs = Differs(module);
            var recreate = rebuilt.Contains(key);
            if (!differs && !recreate)
            {
                items.Add(new MigrationItem(MigrationAction.Unchanged, module.Type, module.Schema, module.Name, "Same definition"));
                continue;
            }

            var replace = existing is not null && !recreate;
            var statement = string.Join(d.EndOfStep, module.Definitions.Select(text =>
                d.AsStatement(replace ? d.ToReplaceDefinition(text, module.Type) : text)));
            var verb = recreate ? "Re-create" : replace ? "Replace" : "New";
            create.Add((module.Type, module.Schema, module.Name, $"-- {verb} {Humanize(module.Type)} {module.Schema}.{module.Name}\n" + statement));
            var overloads = module.Definitions.Count > 1 ? $" ({module.Definitions.Count} overloads)" : "";
            items.Add(new MigrationItem(existing is null ? MigrationAction.Create : MigrationAction.Replace, module.Type, module.Schema, module.Name,
                (existing is null ? "Only on the source" : differs ? "Definition differs" : "Same definition") + overloads +
                (recreate ? "; dropped and created again around the table changes" : "")));
            if (replace && provider == SqlDialect.PostgresKey && module.Type is DbObjectType.Function or DbObjectType.Procedure && module.Definitions.Count != existing!.Definitions.Count)
                warnings.Add($"{module.Schema}.{module.Name}: the overloads differ between the sides; CREATE OR REPLACE adds the source ones and leaves any other signature on the target.");
        }

        var droppedViews = new List<Module>();
        foreach (var (key, module) in targetModules.Where(m => !sourceModules.ContainsKey(m.Key) && Included(m.Value.Type, m.Value.Schema, m.Value.Name)))
        {
            var drop = DropModule(d, provider, module);
            if (!options.DropExtras || drop is null)
            {
                if (rebuilt.Contains(key))
                {
                    // Only on the target, but in the way of a table change: put back as it was.
                    create.Add((module.Type, module.Schema, module.Name, $"-- Re-create {Humanize(module.Type)} {module.Schema}.{module.Name} as it was\n" +
                                                                       string.Join(d.EndOfStep, module.Definitions.Select(d.AsStatement))));
                    items.Add(new MigrationItem(MigrationAction.Replace, module.Type, module.Schema, module.Name,
                        "Only on the target; dropped and created again as it was around the table changes"));
                    continue;
                }
                leftAlone.Add(drop ?? $"-- {Humanize(module.Type)} {module.Schema}.{module.Name} (drop it by hand)");
                items.Add(new MigrationItem(options.DropExtras ? MigrationAction.Manual : MigrationAction.Unchanged, module.Type, module.Schema, module.Name,
                    options.DropExtras ? "Only on the target; drop it by hand (the statement needs its signature or table)" : "Only on the target; left alone"));
                continue;
            }
            if (module.Type is DbObjectType.View or DbObjectType.MaterializedView) droppedViews.Add(module);
            else dropModules.Add(drop);
            items.Add(new MigrationItem(MigrationAction.Drop, module.Type, module.Schema, module.Name, "Only on the target"));
        }

        // Views go away before anything else, the ones that read others first.
        var viewsToDrop = droppedViews.Concat(rebuilt.Select(k => targetModules[k]).Where(m => !droppedViews.Contains(m))).ToList();
        dropModules.InsertRange(0, OrderViews(viewsToDrop, m => m.Name, m => m.Text).Reverse()
            .Select(m => DropModule(d, provider, m) ?? ""));
        moduleSteps.AddRange(OrderModules(create).Select(m => m.Statement));

        // ----- Everything else: sequences, synonyms and the like are listed, not scripted -----
        // A PostgreSQL identity or serial column's own sequence (orders_id_seq) comes with its table.
        var ownSequences = src.Columns.Select(c => $"{c.Schema}.{c.Table}_{c.Name}_seq").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in src.Objects.Where(o => o.Type is DbObjectType.Sequence or DbObjectType.Synonym or DbObjectType.ForeignTable or DbObjectType.Other &&
                                                    Included(o.Type, o.Schema, o.Name) &&
                                                    !(o.Type == DbObjectType.Sequence && provider == SqlDialect.PostgresKey &&
                                                      (ownSequences.Contains(o.FullName) || createdSequences.Contains(Key(Map(o.Schema), o.Name))))))
            if (!dst.Objects.Any(t => t.Type == other.Type && Same(t.Schema, Map(other.Schema)) && Same(t.Name, other.Name)))
                items.Add(new MigrationItem(MigrationAction.Manual, other.Type, Map(other.Schema), other.Name,
                    "Only on the source; not scripted here. Copy it with Copy & sync"));

        var all = schemaSteps.Concat(sequenceSteps).Concat(dropModules).Concat(dropKeys).Concat(tableSteps).Concat(dropTables).Concat(ownedSteps).Concat(addKeys).Concat(moduleSteps).ToList();
        var ordered = items
            .OrderBy(i => i.Action)
            .ThenBy(i => i.Type == DbObjectType.Table ? 0 : 1)
            .ThenBy(i => i.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new MigrationScript("", ordered, warnings);
        return result with { Script = Write(result, all, leftAlone, provider, options) };
    }

    // ----- The script -----

    private static string Write(MigrationScript result, IReadOnlyList<string> steps, IReadOnlyList<string> leftAlone, string provider, MigrationOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("-- Migration script").Append(options.Title is { Length: > 0 } title ? ": " + title : "").Append('\n');
        sb.Append("-- ").Append(result.Summary).Append('\n');
        sb.Append("-- Review it before running: nothing has been run. Generated ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")).Append(".\n");
        if (provider == SqlDialect.PostgresKey && steps.Count > 0)
            sb.Append("-- PostgreSQL can run it all or nothing: wrap it in BEGIN; … COMMIT;\n");
        if (provider == SqlDialect.MySqlKey && steps.Count > 0)
            sb.Append("-- MySQL commits each schema change as it runs: a failure halfway leaves the earlier steps applied.\n");
        foreach (var w in result.Warnings) sb.Append("-- Check: ").Append(w).Append('\n');
        foreach (var manual in result.Items.Where(i => i.Action == MigrationAction.Manual))
            sb.Append("-- By hand: ").Append(Humanize(manual.Type)).Append(' ').Append(manual.FullName).Append(": ").Append(manual.Details).Append('\n');
        sb.Append('\n');

        if (steps.Count == 0) sb.Append(NothingToDo);
        var sqlServer = provider == SqlDialect.SqlServerKey;
        foreach (var step in steps) sb.Append(step).Append(sqlServer ? "\nGO\n" : "\n").Append(sqlServer ? "" : "\n");

        if (leftAlone.Count > 0)
        {
            sb.Append("\n-- Only on the target, left as they are. Tick \"Drop what only the target has\" to drop them, or uncomment:\n");
            foreach (var line in leftAlone) sb.Append(line.StartsWith("--", StringComparison.Ordinal) ? line : "-- " + line).Append('\n');
        }
        return sb.ToString();
    }

    // ----- Tables -----

    /// <summary>The source design pointed at the target: its database, and on MySQL the target database as its schema.</summary>
    private static TableDesign Retarget(TableDesign design, string database, Func<string, string> map) => design with
    {
        Database = database,
        Schema = map(design.Schema),
        ForeignKeys = design.ForeignKeys.Select(f => f with { ReferencedSchema = map(f.ReferencedSchema) }).ToList()
    };

    /// <summary>
    /// The source design made to line up with the target table: keys and indexes with the same definition take the
    /// target's name (so a name alone is never a change), columns are matched by name, and unless extras are dropped,
    /// what only the target has (columns, keys, indexes, the primary key) is kept.
    /// </summary>
    public static TableDesign Align(TableDesign design, TableDesign original, bool dropExtras)
    {
        var keys = design.ForeignKeys.Select(f => original.ForeignKeys.FirstOrDefault(o => Same(o.Column, f.Column) &&
                Same(o.ReferencedSchema, f.ReferencedSchema) && Same(o.ReferencedTable, f.ReferencedTable) && Same(o.ReferencedColumn, f.ReferencedColumn))
            is { } same ? f with { Name = same.Name } : f).ToList();
        var indexes = design.Indexes.Select(i => original.Indexes.FirstOrDefault(o => o.IsUnique == i.IsUnique &&
                o.Columns.SequenceEqual(i.Columns, StringComparer.OrdinalIgnoreCase))
            is { } same ? i with { Name = same.Name, IsConstraint = same.IsConstraint } : i).ToList();
        var aligned = design with
        {
            Database = original.Database,
            // Columns are matched by name: the source's own server names say nothing about the target.
            Columns = design.Columns.Select(c => c with { OriginalName = original.Column(c.Name) is { } o ? o.Name : null }).ToList(),
            ForeignKeys = keys,
            Indexes = indexes,
            PrimaryKeyName = original.PrimaryKeyName.Length > 0 ? original.PrimaryKeyName : design.PrimaryKeyName
        };
        if (dropExtras) return aligned;

        var extraColumns = original.Columns.Where(o => aligned.Column(o.Name) is null).Select(o => o with { OriginalName = o.Name }).ToList();
        var extraKeys = original.ForeignKeys.Where(o => !aligned.ForeignKeys.Any(f => Same(f.Name, o.Name))).ToList();
        var extraIndexes = original.Indexes.Where(o => !aligned.Indexes.Any(i => Same(i.Name, o.Name))).ToList();
        var columns = aligned.Columns.Concat(extraColumns).ToList();
        if (!aligned.PrimaryKey.Any() && original.PrimaryKey.Any())
            columns = columns.Select(c => original.Column(c.Name) is { IsPrimaryKey: true } ? c with { IsPrimaryKey = true, IsNullable = false } : c).ToList();
        return aligned with
        {
            Columns = columns,
            ForeignKeys = [.. aligned.ForeignKeys, .. extraKeys],
            Indexes = [.. aligned.Indexes, .. extraIndexes]
        };
    }

    /// <summary>"2 column(s) added, 1 changed" for the list.</summary>
    private static List<string> Changes(TableDesign original, TableDesign design, string provider)
    {
        var result = new List<string>();
        var added = design.Columns.Count(c => c.OriginalName is null);
        var dropped = original.Columns.Count(o => design.Column(o.Name) is null);
        var changed = design.Columns.Count(c => c.OriginalName is not null && original.Column(c.OriginalName) is { } o &&
                                                !TableAlterAdvisor.SameColumn(o, c, provider));
        if (added > 0) result.Add($"{added} column(s) added");
        if (changed > 0) result.Add($"{changed} changed");
        if (dropped > 0) result.Add($"{dropped} dropped");
        return result;
    }

    /// <summary>What the Table designer's model cannot carry over: multi-column keys, special indexes, computed columns.</summary>
    private static List<string> NotScripted(LoadedTable loaded, MetadataSnapshot snapshot, DbObject table)
    {
        var notes = new List<string>();
        if (loaded.KeptForeignKeys.Count > 0) notes.Add($"multi-column foreign key(s) not scripted: {string.Join(", ", loaded.KeptForeignKeys)}");
        if (loaded.KeptIndexes.Count > 0) notes.Add($"special index(es) not scripted: {string.Join(", ", loaded.KeptIndexes)}");
        var computed = snapshot.ColumnsOf(table.Database, table.Schema, table.Name).Where(c => c.IsComputed).Select(c => c.Name).ToList();
        if (computed.Count > 0) notes.Add($"computed column(s) not scripted: {string.Join(", ", computed)}");
        return notes;
    }

    private static string Describe(string summary, IReadOnlyList<string> notes) =>
        notes.Count == 0 ? summary : summary + "; " + string.Join("; ", notes);

    private static string AddKey(SqlDialect d, string table, TableDesign design, ForeignKeyDesign fk) =>
        $"ALTER TABLE {table} ADD CONSTRAINT {d.Quote(TableScriptBuilder.ForeignKeyName(design, fk, d))} FOREIGN KEY ({d.Quote(fk.Column.Trim())})\n" +
        $"    REFERENCES {d.Table(fk.ReferencedSchema, fk.ReferencedTable)} ({d.Quote(fk.ReferencedColumn.Trim())});";

    // ----- Views, routines and triggers -----

    private sealed record Module(DbObjectType Type, string Schema, string SourceSchema, string Name, IReadOnlyList<string> Definitions)
    {
        public string Text => string.Join("\n", Definitions);
    }

    private static (int, string, string) KeyOf(Module m) => (Kind(m.Type), m.Schema.ToUpperInvariant(), m.Name.ToUpperInvariant());

    private static readonly HashSet<DbObjectType> ModuleTypes =
    [
        DbObjectType.View, DbObjectType.MaterializedView, DbObjectType.Procedure, DbObjectType.Function,
        DbObjectType.ScalarFunction, DbObjectType.TableFunction, DbObjectType.Trigger
    ];

    /// <summary>The modules of one side by (kind, target schema, name). PostgreSQL overloads are one module with several
    /// definitions. MySQL definitions name their database everywhere, so the source's is swapped for the target's.</summary>
    private static Dictionary<(int, string, string), Module> Modules(
        MetadataSnapshot snapshot, string provider, string? targetDatabase, Func<string, string> map)
    {
        var result = new Dictionary<(int, string, string), Module>();
        var objects = snapshot.Objects.Where(o => ModuleTypes.Contains(o.Type)).ToList();
        foreach (var o in objects)
        {
            var schema = map(o.Schema);
            var key = (Kind(o.Type), schema.ToUpperInvariant(), o.Name.ToUpperInvariant());
            if (result.ContainsKey(key)) continue;
            var texts = snapshot.Modules
                .Where(m => SameType(m.Type, o.Type) && Same(m.Schema, o.Schema) && Same(m.Name, o.Name) && !string.IsNullOrWhiteSpace(m.Definition))
                .Select(m => m.Definition!.Trim())
                .ToList();
            if (SqlDialect.SchemaIsDatabase(provider) && !string.IsNullOrEmpty(targetDatabase) && !Same(o.Schema, schema))
            {
                var d = SqlDialect.For(provider);
                texts = texts.Select(t => t.Replace(d.Quote(o.Schema) + ".", d.Quote(schema) + ".", StringComparison.Ordinal)).ToList();
            }
            result[key] = new Module(o.Type, schema, o.Schema, o.Name, texts);
        }
        return result;
    }

    /// <summary>Views first (each after the views it reads from), then functions, procedures and triggers.</summary>
    private static IEnumerable<(DbObjectType Type, string Schema, string Name, string Statement)> OrderModules(
        List<(DbObjectType Type, string Schema, string Name, string Statement)> modules)
    {
        static int Rank(DbObjectType type) => type switch
        {
            DbObjectType.View or DbObjectType.MaterializedView => 0,
            DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => 1,
            DbObjectType.Procedure => 2,
            _ => 3
        };

        var ordered = OrderViews(modules.Where(m => Rank(m.Type) == 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            m => m.Name, m => m.Statement);
        return ordered.Concat(modules.Where(m => Rank(m.Type) > 0)
            .OrderBy(m => Rank(m.Type)).ThenBy(m => m.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Views in an order where each comes after the views it reads (by name in its text).</summary>
    private static IEnumerable<T> OrderViews<T>(IReadOnlyList<T> views, Func<T, string> name, Func<T, string> text)
    {
        var ordered = new List<T>();
        var state = new Dictionary<int, bool>();
        void Visit(int i)
        {
            if (state.ContainsKey(i)) return;
            state[i] = false;
            for (var j = 0; j < views.Count; j++)
                if (j != i && Mentions(text(views[i]), name(views[j]))) Visit(j);
            state[i] = true;
            ordered.Add(views[i]);
        }
        for (var i = 0; i < views.Count; i++) Visit(i);
        return ordered;
    }

    /// <summary>Does the body of a view (after its header's AS, past any leading comment lines) name <paramref name="name"/>?</summary>
    private static bool Mentions(string statement, string name)
    {
        var body = string.Join('\n', statement.Split('\n').SkipWhile(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var header = Regex.Match(body, @"\bAS\b", RegexOptions.IgnoreCase);
        if (header.Success) body = body[header.Index..];
        return Regex.IsMatch(body, $@"(?<![\w$]){Regex.Escape(name)}(?![\w$])", RegexOptions.IgnoreCase);
    }

    /// <summary>The DROP for a module only the target has; null when it needs more than its name (PostgreSQL functions
    /// need their argument types, PostgreSQL triggers their table).</summary>
    private static string? DropModule(SqlDialect d, string provider, Module module)
    {
        var name = d.Table(module.Schema, module.Name);
        var pg = provider == SqlDialect.PostgresKey;
        return module.Type switch
        {
            DbObjectType.View => $"DROP VIEW {name};",
            DbObjectType.MaterializedView => $"DROP MATERIALIZED VIEW {name};",
            DbObjectType.Procedure when !pg => $"DROP PROCEDURE {name};",
            DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction when !pg => $"DROP FUNCTION {name};",
            DbObjectType.Trigger when !pg => $"DROP TRIGGER {name};",
            _ => null
        };
    }

    // ----- Helpers -----

    private static MetadataSnapshot Scope(MigrationSide side) =>
        string.IsNullOrEmpty(side.Database) || !side.Snapshot.ContainsDatabase(side.Database) ? side.Snapshot : side.Snapshot.ForDatabase(side.Database);

    private static string Normalize(IReadOnlyList<string> definitions) =>
        string.Join(' ', definitions.Order(StringComparer.Ordinal)
            .SelectMany(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>Function kinds count as one: a scalar function on one side and a function on the other are the same object.</summary>
    private static int Kind(DbObjectType type) => type switch
    {
        DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => (int)DbObjectType.Function,
        _ => (int)type
    };

    private static bool SameType(DbObjectType a, DbObjectType b) => Kind(a) == Kind(b);

    private static (string, string) Key(string schema, string name) => (schema.ToUpperInvariant(), name.ToUpperInvariant());

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    public static string Humanize(DbObjectType type) => ObjectCopyService.Humanize(type).ToLowerInvariant();
}
