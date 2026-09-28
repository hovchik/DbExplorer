using System.Globalization;
using System.Text;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Copy;

/// <summary>
/// Copies a schema object (and a table's rows) from one connection to another, which may be a different
/// server, database or engine. Nothing runs until the plan has been built and reviewed: <see cref="Analyze"/>
/// works on cached metadata only, <see cref="BuildPlanAsync"/> reads rows and produces the script, and
/// <see cref="ExecuteAsync"/> runs it on the right side.
/// </summary>
public sealed class ObjectCopyService(DefinitionService definitions, QueryExecutionService queries)
{
    private const int ReadTimeoutSeconds = 300;

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "int2", "int4", "int8", "integer"
    };

    // ----- Analysis (metadata only) -----

    /// <summary>
    /// Describes the left object against the right database and suggests what to do: create it when it is
    /// missing, merge or replace the rows of an existing table, or replace the definition of an existing
    /// view/routine/trigger. <paramref name="sameServer"/> guards against copying an object onto itself.
    /// </summary>
    public static CopyAnalysis Analyze(
        MetadataSnapshot sourceSnapshot, string sourceProviderKey, DbObject source,
        MetadataSnapshot targetSnapshot, string targetProviderKey, string targetDatabase,
        string targetSchema, string targetName, bool sameServer)
    {
        targetSchema = targetSchema.Trim();
        targetName = targetName.Trim();
        var crossEngine = sourceProviderKey != targetProviderKey;
        var warnings = new List<string>();

        var candidates = targetSnapshot.Objects
            .Where(o => Same(o.Database, targetDatabase) && Same(o.Schema, targetSchema) && Same(o.Name, targetName))
            .ToList();
        var target = candidates.FirstOrDefault(o => o.Type == source.Type)
                     ?? candidates.FirstOrDefault(o => (o.Type == DbObjectType.Trigger) == (source.Type == DbObjectType.Trigger));

        var analysis = new CopyAnalysis
        {
            Source = source,
            TargetDatabase = targetDatabase,
            TargetSchema = targetSchema,
            TargetName = targetName,
            Target = target,
            CrossEngine = crossEngine,
            TargetSchemaExists = targetSnapshot.Objects.Any(o => Same(o.Database, targetDatabase) && Same(o.Schema, targetSchema))
        };

        if (targetSchema.Length == 0 || targetName.Length == 0)
            return analysis with { Summary = "Enter the schema and name to create on the right." };

        if (sameServer && Same(source.Database, targetDatabase) && Same(source.Schema, targetSchema) && Same(source.Name, targetName))
            return analysis with { Summary = "The left and right objects are the same object. Pick another database, server or target name." };

        if (target is not null && target.Type != source.Type)
        {
            return analysis with
            {
                Summary = $"A {Humanize(target.Type)} named {target.FullName} already exists on the right; " +
                          $"it cannot be replaced by a {Humanize(source.Type)}. Choose another target name."
            };
        }

        return source.Type switch
        {
            DbObjectType.Table => AnalyzeTable(analysis, sourceSnapshot, targetSnapshot, sourceProviderKey, targetProviderKey, warnings),
            DbObjectType.View or DbObjectType.MaterializedView or DbObjectType.Procedure or DbObjectType.Function
                or DbObjectType.ScalarFunction or DbObjectType.TableFunction or DbObjectType.Trigger
                or DbObjectType.Sequence or DbObjectType.Synonym => AnalyzeDefinition(analysis, crossEngine, warnings),
            _ => analysis with { Summary = $"Copying a {Humanize(source.Type)} is not supported." }
        };
    }

    private static CopyAnalysis AnalyzeTable(
        CopyAnalysis a, MetadataSnapshot sourceSnapshot, MetadataSnapshot targetSnapshot,
        string sourceProviderKey, string targetProviderKey, List<string> warnings)
    {
        var source = a.Source;
        var sourceColumns = sourceSnapshot.ColumnsOf(source.Database, source.Schema, source.Name).OrderBy(c => c.Ordinal).ToList();
        if (sourceColumns.Count == 0)
            return a with { Summary = $"No columns are cached for {source.FullName}; refresh the left metadata." };

        if (a.CrossEngine)
            warnings.Add("Different engines: column types are translated, simple defaults (literals, current time, new uuid) are carried over; " +
                         "other defaults, check constraints and table triggers are not copied (see the notes after Preview).");
        else
            warnings.Add("Table triggers are not copied (copy them separately); the script can be reviewed before running it.");

        var missingParents = MissingParents(sourceSnapshot, source, targetSnapshot, a.TargetDatabase, a.TargetSchema, a.TargetName);
        if (missingParents.Count > 0)
        {
            warnings.Add($"References {missingParents.Count} table(s) missing on the right: " +
                         string.Join(", ", missingParents.Select(p => p.FullName)) +
                         ". Include missing parent tables, or foreign keys to them are skipped.");
        }

        if (a.Target is null)
        {
            foreach (var computed in sourceColumns.Where(c => c.IsComputed))
                warnings.Add($"{computed.Name} is computed on the left; it is created as a regular column holding the copied values.");

            return a with
            {
                AvailableActions = [CopyAction.CreateObject],
                RecommendedAction = CopyAction.CreateObject,
                MissingParents = missingParents,
                Warnings = warnings,
                KeyColumns = sourceColumns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
                Summary = $"{a.TargetFullName} does not exist on the right. Suggested: create it with its structure, indexes and data."
            };
        }

        var targetColumns = targetSnapshot.ColumnsOf(a.TargetDatabase, a.TargetSchema, a.TargetName).ToList();
        var targetByName = targetColumns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sourceNames = sourceColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingOnTarget = sourceColumns.Where(c => !targetByName.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        var onlyOnTarget = targetColumns.Where(c => !sourceNames.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList();
        IReadOnlyList<string> typeDifferences = a.CrossEngine
            ? []
            : sourceColumns.Where(c => targetByName.TryGetValue(c.Name, out var t) && !Same(c.DataType, t.DataType))
                .Select(c => $"{c.Name}: {c.DataType} → {targetByName[c.Name].DataType}")
                .ToList();

        foreach (var column in onlyOnTarget.Where(c => !c.IsNullable && !c.IsIdentity && !c.IsComputed))
            warnings.Add($"{column.Name} is NOT NULL on the right but missing on the left: inserts fail unless it has a default.");
        if (typeDifferences.Count > 0)
            warnings.Add($"{typeDifferences.Count} column(s) have a different type on the right; values are converted by the server on insert.");

        var keyColumns = sourceColumns.Where(c => c.IsPrimaryKey && targetByName.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        var incoming = targetSnapshot.ReferencesTo(a.TargetDatabase, a.TargetSchema, a.TargetName)
            .Where(f => !(Same(f.Schema, a.TargetSchema) && Same(f.Table, a.TargetName)))
            .Select(f => $"{f.Schema}.{f.Table} ({f.Name})")
            .ToList();
        if (incoming.Count > 0)
            warnings.Add($"Referenced on the right by {string.Join(", ", incoming)}: deleting rows or dropping the table can fail.");

        var actions = new List<CopyAction>();
        if (keyColumns.Count > 0) actions.Add(CopyAction.MergeData);
        else warnings.Add("The table has no primary key on both sides, so rows cannot be matched: merging is unavailable.");
        actions.Add(CopyAction.ReplaceData);
        actions.Add(CopyAction.DropAndRecreate);

        var recommended = keyColumns.Count > 0 ? CopyAction.MergeData : CopyAction.ReplaceData;
        var summary = recommended == CopyAction.MergeData
            ? $"{a.TargetFullName} already exists on the right. Suggested: merge the data (insert missing rows, update changed ones); " +
              "or delete the right rows and re-insert them from the left, or drop and re-create the table."
            : $"{a.TargetFullName} already exists on the right without a usable primary key. Suggested: replace its rows with the left ones; " +
              "or drop and re-create the table.";

        return a with
        {
            AvailableActions = actions,
            RecommendedAction = recommended,
            Summary = summary,
            Warnings = warnings,
            KeyColumns = keyColumns,
            ColumnsMissingOnTarget = missingOnTarget,
            ColumnsOnlyOnTarget = onlyOnTarget.Select(c => c.Name).ToList(),
            ColumnTypeDifferences = typeDifferences,
            MissingParents = missingParents,
            IncomingReferences = incoming
        };
    }

    private static CopyAnalysis AnalyzeDefinition(CopyAnalysis a, bool crossEngine, List<string> warnings)
    {
        var source = a.Source;
        if (crossEngine)
        {
            return a with
            {
                Summary = $"{Humanize(source.Type)}s can only be copied between servers of the same engine: their definition is engine-specific SQL."
            };
        }

        if (!Same(source.Schema, a.TargetSchema) || !Same(source.Name, a.TargetName))
        {
            return a with
            {
                Summary = $"A {Humanize(source.Type)} is copied under its own name, because the name is part of its definition. " +
                          $"Set the target name to {source.FullName}."
            };
        }

        if (a.Target is null)
        {
            if (source.Type == DbObjectType.Trigger)
                warnings.Add("The table the trigger belongs to must exist on the right.");
            else
                warnings.Add("Objects it depends on (tables, other views and routines) must exist on the right.");

            return a with
            {
                AvailableActions = [CopyAction.CreateObject],
                RecommendedAction = CopyAction.CreateObject,
                Warnings = warnings,
                Summary = $"{a.TargetFullName} does not exist on the right. Suggested: create it from the left definition."
            };
        }

        if (source.Type == DbObjectType.Sequence)
        {
            return a with
            {
                Summary = $"Sequence {a.TargetFullName} already exists on the right; re-creating it would reset its current value, so it is left as is."
            };
        }

        warnings.Add("Compare the definitions on the Schema tab before replacing.");
        return a with
        {
            AvailableActions = [CopyAction.ReplaceDefinition],
            RecommendedAction = CopyAction.ReplaceDefinition,
            Warnings = warnings,
            Summary = $"{a.TargetFullName} already exists on the right. Suggested: replace its definition with the left one."
        };
    }

    /// <summary>Tables referenced by <paramref name="table"/> through foreign keys, directly or indirectly,
    /// that are missing on the target; ordered so every table comes after the tables it references.</summary>
    public static IReadOnlyList<DbObject> MissingParents(
        MetadataSnapshot sourceSnapshot, DbObject table, MetadataSnapshot targetSnapshot,
        string targetDatabase, string targetSchema, string targetName)
    {
        var result = new List<DbObject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table.FullName };

        void Visit(DbObject child)
        {
            foreach (var fk in sourceSnapshot.ForeignKeysOf(child.Database, child.Schema, child.Name))
            {
                var key = $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
                if (!visited.Add(key)) continue;

                var existsOnTarget = targetSnapshot.Objects.Any(o =>
                    o.Type == DbObjectType.Table && Same(o.Database, targetDatabase) &&
                    Same(o.Schema, fk.ReferencedSchema) && Same(o.Name, fk.ReferencedTable)) ||
                    (Same(fk.ReferencedSchema, targetSchema) && Same(fk.ReferencedTable, targetName));
                if (existsOnTarget) continue;

                var parent = sourceSnapshot.Objects.FirstOrDefault(o =>
                    o.Type == DbObjectType.Table && Same(o.Database, child.Database) &&
                    Same(o.Schema, fk.ReferencedSchema) && Same(o.Name, fk.ReferencedTable));
                if (parent is null) continue;

                Visit(parent);
                result.Add(parent);
            }
        }

        Visit(table);
        return result;
    }

    // ----- Plan -----

    /// <summary>Reads what the action needs (rows of both sides for a merge) and produces the script to run on the right.</summary>
    public async Task<CopyPlan> BuildPlanAsync(
        DatabaseSession sourceSession, DatabaseSession targetSession, CopyAnalysis analysis,
        CopyAction action, CopyOptions options, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? tablesCreatedEarlier = null)
    {
        if (!analysis.AvailableActions.Contains(action))
            throw new InvalidOperationException($"{Humanize(action)} is not possible here: {analysis.Summary}");

        var context = new PlanContext(sourceSession, targetSession, analysis, action, options, progress);
        // Tables created by earlier objects of a batch are not in the right snapshot yet, but foreign keys may point at them.
        if (tablesCreatedEarlier is not null) context.CreatedTables.UnionWith(tablesCreatedEarlier);
        if (analysis.IsTable) await PlanTableAsync(context, ct);
        else await PlanDefinitionAsync(context, ct);

        return new CopyPlan
        {
            Analysis = analysis,
            Action = action,
            Options = options,
            TargetProviderKey = targetSession.Provider.ProviderKey,
            Steps = context.Steps,
            RowsToInsert = context.Inserted,
            RowsToUpdate = context.Updated,
            RowsToDelete = context.Deleted,
            RowsUnchanged = context.Unchanged,
            TargetRowsRemoved = context.Removed,
            Notes = context.Notes,
            CreatedTables = context.CreatedTables.Except(tablesCreatedEarlier ?? [], StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private sealed class PlanContext(
        DatabaseSession sourceSession, DatabaseSession targetSession, CopyAnalysis analysis,
        CopyAction action, CopyOptions options, IProgress<string>? progress)
    {
        public DatabaseSession SourceSession { get; } = sourceSession;
        public DatabaseSession TargetSession { get; } = targetSession;
        public CopyAnalysis Analysis { get; } = analysis;
        public CopyAction Action { get; } = action;
        public CopyOptions Options { get; } = options;
        public IProgress<string>? Progress { get; } = progress;
        public SqlDialect Source { get; } = SqlDialect.For(sourceSession.Provider.ProviderKey);
        public SqlDialect Target { get; } = SqlDialect.For(targetSession.Provider.ProviderKey);
        public bool SameEngine => Source.ProviderKey == Target.ProviderKey;
        public List<CopyStep> Steps { get; } = [];
        public List<string> Notes { get; } = [];
        public HashSet<string> CreatedTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public long? Removed { get; set; }

        public void Add(string title, CopyStepKind kind, string sql) => Steps.Add(new CopyStep(title, kind, sql));
    }

    private async Task PlanDefinitionAsync(PlanContext c, CancellationToken ct)
    {
        var source = c.Analysis.Source;
        var texts = c.SourceSession.Snapshot.Modules
            .Where(m => Same(m.Database, source.Database) && Same(m.Schema, source.Schema) && Same(m.Name, source.Name)
                        && m.Type == source.Type && !string.IsNullOrWhiteSpace(m.Definition))
            .Select(m => m.Definition!)
            .ToList();

        if (texts.Count == 0)
        {
            var text = await definitions.GetDefinitionAsync(c.SourceSession, source, ct);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"The definition of {source.FullName} is not available (encrypted, or no permission to read it).");
            texts.Add(text);
        }

        var replace = c.Action == CopyAction.ReplaceDefinition;
        for (var i = 0; i < texts.Count; i++)
        {
            var text = replace ? c.Target.ToReplaceDefinition(texts[i], source.Type) : texts[i];
            if (replace && source.Type == DbObjectType.Synonym)
                text = $"DROP SYNONYM {c.Target.Table(source.Schema, source.Name)};{c.Target.EndOfStep}{text}";

            var title = (replace ? "Replace " : "Create ") + Humanize(source.Type).ToLowerInvariant() + " " + source.FullName +
                        (texts.Count > 1 ? $" (overload {i + 1} of {texts.Count})" : "");
            c.Add(title, CopyStepKind.Structure, c.Target.AsStatement(text));
        }
    }

    private async Task PlanTableAsync(PlanContext c, CancellationToken ct)
    {
        var a = c.Analysis;
        var t = c.Target;
        var targetTable = t.Table(a.TargetSchema, a.TargetName);
        var sourceColumns = ColumnsOf(c.SourceSession.Snapshot, a.Source);

        IReadOnlyList<DbObject> parents = c.Options.IncludeMissingParents && c.Action is CopyAction.CreateObject or CopyAction.DropAndRecreate
            ? a.MissingParents
            : [];

        // Schemas first: CREATE SCHEMA is idempotent, so it is emitted whenever the cache has not seen the schema.
        var schemas = parents.Select(p => p.Schema).Append(a.TargetSchema).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !c.TargetSession.Snapshot.Objects.Any(o => Same(o.Database, a.TargetDatabase) && Same(o.Schema, s)));
        foreach (var schema in schemas)
            c.Add($"Create schema {schema} if missing", CopyStepKind.Schema, t.CreateSchemaIfMissing(schema));

        if (c.Options.BackupTarget && a.TargetExists)
        {
            var backupName = t.TruncateIdentifier($"{a.TargetName}_backup_{DateTime.Now:yyyyMMdd_HHmmss}");
            c.Add($"Back up {a.TargetFullName} to {a.TargetSchema}.{backupName}", CopyStepKind.Backup,
                t.CopyTableAs(targetTable, a.TargetSchema, backupName));
            c.Notes.Add($"The current right table is saved as {a.TargetSchema}.{backupName} before any change.");
        }

        switch (c.Action)
        {
            case CopyAction.CreateObject:
            case CopyAction.DropAndRecreate:
                if (c.Action == CopyAction.DropAndRecreate)
                {
                    c.Removed = a.Target?.RowCount;
                    c.Add($"Drop {a.TargetFullName}", CopyStepKind.Structure, t.DropTable(targetTable));
                }

                var created = new List<(DbObject Source, string Schema, string Name, IReadOnlyList<DbColumn> Columns, DbTableConstraints Constraints)>();
                foreach (var table in parents.Append(a.Source))
                {
                    var isMain = ReferenceEquals(table, a.Source);
                    var (schema, name) = isMain ? (a.TargetSchema, a.TargetName) : (table.Schema, table.Name);
                    var constraints = await ConstraintsOfAsync(c, table, ct);
                    created.Add((table, schema, name, CreateTable(c, table, schema, name, constraints), constraints));
                }

                if (c.Options.IncludeData)
                {
                    foreach (var (table, schema, name, targetColumns, _) in created)
                    {
                        var isMain = ReferenceEquals(table, a.Source);
                        await AddRowCopyAsync(c, table, schema, name, targetColumns, isMain ? c.Options.RowFilter : null, ct);
                    }
                    if (parents.Count > 0)
                        c.Notes.Add("Parent tables are copied with all their rows so the foreign keys can be created.");
                }

                foreach (var (table, schema, name, targetColumns, _) in created)
                {
                    if (c.Options.CopyIndexes) AddIndexes(c, table, schema, name, targetColumns);
                    if (c.Options.CopyForeignKeys) AddForeignKeys(c, table, schema, name);
                }
                break;

            case CopyAction.ReplaceData:
            {
                var targetColumns = PrepareExistingTarget(c, sourceColumns, await ConstraintsOfAsync(c, a.Source, ct));
                var filter = NullIfBlank(c.Options.RowFilter);
                c.Removed = await CountAsync(c.TargetSession, t, a.TargetDatabase, targetTable, filter, ct);
                var delete = filter is not null ? $"DELETE FROM {targetTable} WHERE {filter};"
                    : a.IncomingReferences.Count > 0 ? $"DELETE FROM {targetTable};"
                    : t.Truncate(targetTable);
                c.Add(filter is null ? $"Remove every row of {a.TargetFullName}" : $"Remove the filtered rows of {a.TargetFullName}",
                    CopyStepKind.Delete, delete);

                await AddRowCopyAsync(c, a.Source, a.TargetSchema, a.TargetName, targetColumns, filter, ct);
                break;
            }

            case CopyAction.MergeData:
                await PlanMergeAsync(c, sourceColumns, ct);
                break;
        }
    }

    private async Task PlanMergeAsync(PlanContext c, IReadOnlyList<DbColumn> sourceColumns, CancellationToken ct)
    {
        var a = c.Analysis;
        var t = c.Target;
        var targetTable = t.Table(a.TargetSchema, a.TargetName);
        var targetColumns = PrepareExistingTarget(c, sourceColumns, await ConstraintsOfAsync(c, a.Source, ct));
        var filter = NullIfBlank(c.Options.RowFilter);

        var source = await ReadSourceRowsAsync(c, a.Source, targetColumns, filter, ct);

        // Columns added by this plan do not exist on the right yet: read only the ones that do.
        var existing = c.TargetSession.Snapshot.ColumnsOf(a.TargetDatabase, a.TargetSchema, a.TargetName)
            .Select(col => col.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readable = source.TargetColumns.Where(col => existing.Contains(col.Name)).ToList();
        c.Progress?.Report($"Reading {a.TargetFullName} on the right…");
        var target = await ReadRowsAsync(c.TargetSession, t, a.TargetDatabase, a.TargetSchema, a.TargetName,
            readable, a.KeyColumns, filter, c.Options.MaxRows, ct);

        // Align the right rows on the left column order; columns being added read as NULL.
        var positions = source.TargetColumns.Select(col => readable.FindIndex(r => Same(r.Name, col.Name))).ToList();
        var alignedTarget = target.Select(row => (IReadOnlyList<object?>)positions.Select(p => p < 0 ? null : row[p]).ToList()).ToList();

        var keyIndexes = a.KeyColumns.Select(k => source.Columns.FindIndex(col => Same(col, k))).ToList();
        var diff = DiffRows(source.Rows, alignedTarget, keyIndexes);
        c.Unchanged = diff.Unchanged;

        AddInserts(c, a.TargetSchema, a.TargetName, source.Columns, source.TargetColumns, diff.Inserts);
        if (c.Options.UpdateChanged)
            AddUpdates(c, targetTable, source.Columns, source.TargetColumns, keyIndexes, diff.Updates);
        else if (diff.Updates.Count > 0)
            c.Notes.Add($"{diff.Updates.Count:N0} row(s) differ but are left as is (updating changed rows is off).");

        if (c.Options.DeleteExtra)
            AddDeletes(c, targetTable, a.KeyColumns, keyIndexes.Select(i => source.TargetColumns[i]).ToList(), diff.Deletes);
        else if (diff.Deletes.Count > 0)
            c.Notes.Add($"{diff.Deletes.Count:N0} row(s) exist only on the right and are kept (deleting extra rows is off).");

        if (diff.Inserts.Count > 0) AddSequenceReset(c, a.TargetSchema, a.TargetName, source.TargetColumns);
        if (filter is not null) c.Notes.Add($"Only rows matching \"{filter}\" were compared, on both sides.");
    }

    /// <summary>Adds the left-only columns to the existing right table when asked, and returns the right
    /// columns that receive data, in left column order.</summary>
    private static IReadOnlyList<DbColumn> PrepareExistingTarget(
        PlanContext c, IReadOnlyList<DbColumn> sourceColumns, DbTableConstraints sourceConstraints)
    {
        var a = c.Analysis;
        var targetTable = c.Target.Table(a.TargetSchema, a.TargetName);
        var targetByName = c.TargetSession.Snapshot.ColumnsOf(a.TargetDatabase, a.TargetSchema, a.TargetName)
            .ToDictionary(col => col.Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<DbColumn>();
        foreach (var column in sourceColumns)
        {
            if (targetByName.TryGetValue(column.Name, out var existing))
            {
                if (!c.Target.IsReadOnlyColumn(existing)) result.Add(existing);
                continue;
            }

            if (!c.Options.AddMissingColumns)
            {
                c.Notes.Add($"{column.Name} exists only on the left and is not copied (adding missing columns is off).");
                continue;
            }

            var mapped = MapColumn(c, column);
            var defaultExpression = DefaultFor(c, column, mapped, sourceConstraints);
            c.Add($"Add column {column.Name} to {a.TargetFullName}", CopyStepKind.Structure,
                c.Target.AddColumn(targetTable, column.Name, mapped.DataType, defaultExpression));
            if (!c.Target.IsReadOnlyColumn(mapped)) result.Add(mapped with { IsIdentity = false, IsPrimaryKey = false });
        }
        return result;
    }

    /// <summary>The right-side column created for a left column: same name, translated type.</summary>
    private static DbColumn MapColumn(PlanContext c, DbColumn column)
    {
        var mapping = ColumnTypeMapper.Map(column, c.Source.ProviderKey, c.Target.ProviderKey);
        if (mapping.Warning is not null && !c.Notes.Contains(mapping.Warning)) c.Notes.Add(mapping.Warning);
        var baseType = c.SameEngine ? column.BaseType : mapping.Type.Split('(')[0].Trim();
        return column with { DataType = mapping.Type, BaseType = baseType, IsComputed = false };
    }

    private static async Task<DbTableConstraints> ConstraintsOfAsync(PlanContext c, DbObject table, CancellationToken ct)
    {
        if (!c.Options.CopyDefaultsAndChecks) return DbTableConstraints.None;
        try
        {
            return await c.SourceSession.Provider.GetTableConstraintsAsync(table, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Notes.Add($"Defaults and check constraints of {table.FullName} could not be read ({ex.Message}); they are not copied.");
            return DbTableConstraints.None;
        }
    }

    /// <summary>The right-side default of a copied column, or null (reported in the notes when one is dropped).</summary>
    private static string? DefaultFor(PlanContext c, DbColumn source, DbColumn target, DbTableConstraints constraints)
    {
        if (!constraints.Defaults.TryGetValue(source.Name, out var expression) || target.IsIdentity) return null;
        if (DefaultTranslator.IsSequenceDefault(expression)) return null;
        var translated = DefaultTranslator.Translate(expression, c.Source.ProviderKey, c.Target.ProviderKey, target.BaseType);
        if (translated is null) c.Notes.Add($"Default of {source.Table}.{source.Name} ({expression}) has no {EngineName(c.Target)} equivalent and is not copied.");
        return translated;
    }

    private static IReadOnlyList<DbColumn> CreateTable(PlanContext c, DbObject table, string schema, string name, DbTableConstraints constraints)
    {
        var t = c.Target;
        var sourceColumns = ColumnsOf(c.SourceSession.Snapshot, table);
        var columns = sourceColumns.Select(col => MapColumn(c, col) with
        {
            // A PostgreSQL serial column (nextval default) becomes an identity column: the sequence itself is not copied.
            IsIdentity = col.IsIdentity || (constraints.Defaults.TryGetValue(col.Name, out var d) && DefaultTranslator.IsSequenceDefault(d))
        }).ToList();
        columns = columns.Select(col => col with
        {
            // PostgreSQL identity columns must be integers.
            IsIdentity = col.IsIdentity && (t.ProviderKey == SqlDialect.SqlServerKey || IntegerTypes.Contains(col.BaseType)
                                             || col.DataType is "integer" or "bigint" or "smallint")
        }).ToList();

        var lines = columns.Select((col, i) =>
        {
            var defaultExpression = DefaultFor(c, sourceColumns[i], col, constraints);
            return $"    {t.Quote(col.Name)} {col.DataType}{(col.IsIdentity ? t.IdentityClause : "")}" +
                   $"{(col.IsNullable ? " NULL" : " NOT NULL")}{(defaultExpression is null ? "" : " DEFAULT " + defaultExpression)}";
        }).ToList();

        var keys = columns.Where(col => col.IsPrimaryKey).Select(col => t.Quote(col.Name)).ToList();
        if (keys.Count > 0)
        {
            // Keep a nonclustered primary key nonclustered, so a clustered index copied later still fits.
            var pkIndex = c.SourceSession.Snapshot.IndexesOf(table.Database, table.Schema, table.Name).FirstOrDefault(i => i.IsPrimaryKey);
            var nonClustered = c.SameEngine && t.ProviderKey == SqlDialect.SqlServerKey &&
                               pkIndex?.Type.StartsWith("NONCLUSTERED", StringComparison.OrdinalIgnoreCase) == true;
            lines.Add($"    PRIMARY KEY{(nonClustered ? " NONCLUSTERED" : "")} ({string.Join(", ", keys)})");
        }

        foreach (var check in constraints.Checks)
        {
            if (!c.SameEngine)
            {
                c.Notes.Add($"Check constraint {check.Name} ({check.Expression}) is engine-specific and is not copied.");
                continue;
            }
            var checkName = t.TruncateIdentifier(RenameFor(check.Name, table, name));
            lines.Add($"    CONSTRAINT {t.Quote(checkName)} CHECK ({check.Expression})");
        }

        var sql = new StringBuilder()
            .Append("CREATE TABLE ").Append(t.Table(schema, name)).AppendLine(" (")
            .AppendLine(string.Join(",\n", lines))
            .Append(");")
            .ToString();

        c.Add($"Create table {schema}.{name}", CopyStepKind.Structure, sql);
        c.CreatedTables.Add($"{schema}.{name}");
        return columns.Where(col => !t.IsReadOnlyColumn(col)).ToList();
    }

    private static string EngineName(SqlDialect dialect) => dialect.ProviderKey == SqlDialect.SqlServerKey ? "SQL Server" : "PostgreSQL";

    /// <summary>Copies the left rows into the right table: streamed at run time when the table has a key
    /// (no row limit, flat memory), otherwise read now and written into the script.</summary>
    private async Task AddRowCopyAsync(
        PlanContext c, DbObject table, string schema, string name, IReadOnlyList<DbColumn> targetColumns, string? filter, CancellationToken ct)
    {
        var sourceByName = ColumnsOf(c.SourceSession.Snapshot, table).ToDictionary(col => col.Name, StringComparer.OrdinalIgnoreCase);
        var keys = sourceByName.Values.Where(col => col.IsPrimaryKey).OrderBy(col => col.Ordinal).Select(col => col.Name).ToList();
        var transferred = targetColumns.Where(col => sourceByName.ContainsKey(col.Name)).ToList();
        var canStream = c.Options.StreamRows && keys.Count > 0 &&
                        keys.All(k => transferred.Any(col => Same(col.Name, k)));

        if (!canStream)
        {
            if (c.Options.StreamRows && keys.Count == 0)
                c.Notes.Add($"{table.FullName} has no primary key, so its rows are loaded when planning (limit {c.Options.MaxRows:N0}).");
            var rows = await ReadSourceRowsAsync(c, table, targetColumns, filter, ct);
            AddInserts(c, schema, name, rows.Columns, rows.TargetColumns, rows.Rows);
            AddSequenceReset(c, schema, name, rows.TargetColumns);
            return;
        }

        c.Progress?.Report($"Counting the rows of {table.FullName}…");
        var count = await CountAsync(c.SourceSession, c.Source, table.Database, c.Source.Table(table.Schema, table.Name), NullIfBlank(filter), ct);
        var stream = new StreamedCopy(
            table,
            transferred.Select(col => sourceByName[col.Name]).ToList(),
            schema, name, transferred, keys, NullIfBlank(filter), count);

        var select = c.Source.SelectTop(
            string.Join(", ", stream.SourceColumns.Select(c.Source.SelectExpression)), c.Source.Table(table.Schema, table.Name),
            stream.RowFilter, string.Join(", ", keys.Select(c.Source.Quote)), c.Options.PageSize);
        var description =
            $"-- {count:N0} row(s) are streamed from the left when this runs, {c.Options.PageSize:N0} per page, in key order:\n" +
            $"--   {select.ReplaceLineEndings(" ")}\n" +
            $"-- and inserted into {c.Target.Table(schema, name)} ({string.Join(", ", transferred.Select(col => c.Target.Quote(col.Name)))}).";
        c.Steps.Add(new CopyStep($"Stream {count:N0} row(s) from {table.FullName} into {schema}.{name}", CopyStepKind.Insert, description, stream));
        c.Inserted += (int)Math.Min(count, int.MaxValue);
        if (!c.Notes.Any(n => n.StartsWith("Rows are streamed", StringComparison.Ordinal)))
            c.Notes.Add("Rows are streamed while running, so a saved script contains the structure but not the streamed rows.");
        AddSequenceReset(c, schema, name, transferred);
    }

    private sealed record SourceRows(
        List<string> Columns, IReadOnlyList<DbColumn> TargetColumns, IReadOnlyList<IReadOnlyList<object?>> Rows);

    /// <summary>Reads the left rows for the given right columns (matched by name, in the same order).</summary>
    private async Task<SourceRows> ReadSourceRowsAsync(
        PlanContext c, DbObject table, IReadOnlyList<DbColumn> targetColumns, string? filter, CancellationToken ct)
    {
        var sourceByName = ColumnsOf(c.SourceSession.Snapshot, table).ToDictionary(col => col.Name, StringComparer.OrdinalIgnoreCase);
        var pairs = targetColumns.Where(col => sourceByName.ContainsKey(col.Name)).Select(col => (Source: sourceByName[col.Name], Target: col)).ToList();
        if (pairs.Count == 0)
            throw new InvalidOperationException($"{table.FullName} has no column in common with the right table.");

        var keys = sourceByName.Values.Where(col => col.IsPrimaryKey).OrderBy(col => col.Ordinal).Select(col => col.Name).ToList();
        c.Progress?.Report($"Reading {table.FullName} on the left…");
        var rows = await ReadRowsAsync(c.SourceSession, c.Source, table.Database, table.Schema, table.Name,
            pairs.Select(p => p.Source).ToList(), keys, NullIfBlank(filter), c.Options.MaxRows, ct);
        return new SourceRows(pairs.Select(p => p.Target.Name).ToList(), pairs.Select(p => p.Target).ToList(), rows);
    }

    private async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadRowsAsync(
        DatabaseSession session, SqlDialect dialect, string database, string schema, string name,
        IReadOnlyList<DbColumn> columns, IReadOnlyList<string> orderBy, string? filter, int maxRows, CancellationToken ct)
    {
        if (columns.Count == 0) return [];
        var sql = dialect.SelectTop(
            string.Join(", ", columns.Select(dialect.SelectExpression)),
            dialect.Table(schema, name),
            filter,
            orderBy.Count == 0 ? null : string.Join(", ", orderBy.Select(dialect.Quote)),
            maxRows + 1);

        var result = await queries.ExecuteScriptAsync(session, sql, NullIfBlank(database), ReadTimeoutSeconds, ct);
        var rows = result.ResultSets.FirstOrDefault()?.Rows ?? [];
        if (rows.Count > maxRows)
        {
            throw new InvalidOperationException(
                $"{schema}.{name} has more than {maxRows:N0} rows to copy. Raise the row limit or add a row filter.");
        }
        return rows;
    }

    private static async Task<long> CountAsync(
        DatabaseSession session, SqlDialect dialect, string database, string table, string? filter, CancellationToken ct)
    {
        var result = await session.Provider.ExecuteScriptAsync(dialect.CountRows(table, filter), NullIfBlank(database), ReadTimeoutSeconds, ct);
        var value = result.ResultSets.FirstOrDefault()?.Rows.FirstOrDefault()?.FirstOrDefault();
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddInserts(
        PlanContext c, string schema, string name, IReadOnlyList<string> columns, IReadOnlyList<DbColumn> targetColumns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (rows.Count == 0) return;
        var batch = Math.Clamp(c.Options.BatchSize, 1, 1000);
        var statements = BuildInsertStatements(c.Target, schema, name, columns, targetColumns, rows, batch);
        for (var i = 0; i < statements.Count; i++)
        {
            var start = i * batch;
            var end = Math.Min(start + batch, rows.Count);
            c.Add($"Insert rows {start + 1:N0}–{end:N0} of {rows.Count:N0} into {schema}.{name}", CopyStepKind.Insert, statements[i]);
        }
        c.Inserted += rows.Count;
    }

    /// <summary>Multi-row INSERT statements of at most <paramref name="batchSize"/> rows (SQL Server allows 1000),
    /// each wrapped in IDENTITY_INSERT when an identity column receives explicit values.</summary>
    public static IReadOnlyList<string> BuildInsertStatements(
        SqlDialect t, string schema, string name, IReadOnlyList<string> columns, IReadOnlyList<DbColumn> targetColumns,
        IReadOnlyList<IReadOnlyList<object?>> rows, int batchSize)
    {
        var table = t.Table(schema, name);
        var hasIdentity = targetColumns.Any(col => col.IsIdentity);
        var (before, after) = hasIdentity ? t.IdentityInsertScope(table) : ("", "");
        var prefix = $"INSERT INTO {table} ({string.Join(", ", columns.Select(t.Quote))}){(hasIdentity ? t.InsertIdentityOverride : "")} VALUES";
        batchSize = Math.Clamp(batchSize, 1, 1000);

        var result = new List<string>();
        for (var start = 0; start < rows.Count; start += batchSize)
        {
            var sql = new StringBuilder(before).AppendLine(prefix);
            sql.AppendJoin(",\n", rows.Skip(start).Take(batchSize)
                .Select(row => "(" + string.Join(", ", row.Select((v, i) => t.Literal(v, targetColumns[i].BaseType))) + ")"));
            sql.Append(";\n").Append(after);
            result.Add(sql.ToString().TrimEnd());
        }
        return result;
    }

    private static void AddUpdates(
        PlanContext c, string table, IReadOnlyList<string> columns, IReadOnlyList<DbColumn> targetColumns,
        IReadOnlyList<int> keyIndexes, IReadOnlyList<RowUpdate> updates)
    {
        if (updates.Count == 0) return;
        var t = c.Target;
        var batch = Math.Max(1, c.Options.BatchSize);
        for (var start = 0; start < updates.Count; start += batch)
        {
            var sql = new StringBuilder();
            foreach (var update in updates.Skip(start).Take(batch))
            {
                var set = update.ChangedColumns
                    .Where(i => !keyIndexes.Contains(i) && !targetColumns[i].IsIdentity)
                    .Select(i => $"{t.Quote(columns[i])} = {t.Literal(update.Row[i], targetColumns[i].BaseType)}")
                    .ToList();
                if (set.Count == 0) continue;
                var where = string.Join(" AND ", keyIndexes.Select(k => $"{t.Quote(columns[k])} = {t.Literal(update.Row[k], targetColumns[k].BaseType)}"));
                sql.Append("UPDATE ").Append(table).Append(" SET ").AppendJoin(", ", set).Append(" WHERE ").Append(where).AppendLine(";");
            }
            if (sql.Length == 0) continue;
            var end = Math.Min(start + batch, updates.Count);
            c.Add($"Update changed rows {start + 1:N0}–{end:N0} of {updates.Count:N0}", CopyStepKind.Update, sql.ToString().TrimEnd());
        }
        c.Updated += updates.Count;
    }

    private static void AddDeletes(
        PlanContext c, string table, IReadOnlyList<string> keyColumns, IReadOnlyList<DbColumn> keyTargetColumns,
        IReadOnlyList<IReadOnlyList<object?>> keys)
    {
        if (keys.Count == 0) return;
        var t = c.Target;
        var batch = Math.Max(1, c.Options.BatchSize);
        for (var start = 0; start < keys.Count; start += batch)
        {
            var chunk = keys.Skip(start).Take(batch).ToList();
            string where;
            if (keyColumns.Count == 1)
            {
                where = $"{t.Quote(keyColumns[0])} IN ({string.Join(", ", chunk.Select(k => t.Literal(k[0], keyTargetColumns[0].BaseType)))})";
            }
            else
            {
                where = string.Join("\n   OR ", chunk.Select(k => "(" + string.Join(" AND ",
                    keyColumns.Select((col, i) => $"{t.Quote(col)} = {t.Literal(k[i], keyTargetColumns[i].BaseType)}")) + ")"));
            }
            var end = Math.Min(start + batch, keys.Count);
            c.Add($"Delete right-only rows {start + 1:N0}–{end:N0} of {keys.Count:N0}", CopyStepKind.Delete, $"DELETE FROM {table} WHERE {where};");
        }
        c.Deleted += keys.Count;
    }

    private static void AddSequenceReset(PlanContext c, string schema, string name, IReadOnlyList<DbColumn> targetColumns)
    {
        foreach (var column in targetColumns.Where(col => (col.IsIdentity || col.IsPrimaryKey) && IntegerTypes.Contains(col.BaseType)))
        {
            var sql = c.Target.ResetSequence(schema, name, column.Name);
            if (sql.Length > 0)
                c.Add($"Move the sequence of {schema}.{name}.{column.Name} past the copied values", CopyStepKind.Maintenance, sql);
        }
    }

    private static void AddIndexes(PlanContext c, DbObject table, string schema, string name, IReadOnlyList<DbColumn> columns)
    {
        var t = c.Target;
        var names = ColumnsOf(c.SourceSession.Snapshot, table).Select(col => col.Name).ToList();
        foreach (var index in c.SourceSession.Snapshot.IndexesOf(table.Database, table.Schema, table.Name).Where(i => !i.IsPrimaryKey))
        {
            var type = index.Type.ToUpperInvariant();
            if (type.Contains("COLUMNSTORE") || type.Contains("XML") || type.Contains("SPATIAL") || type == "HEAP")
            {
                c.Notes.Add($"Index {index.Name} ({index.Type}) is not copied.");
                continue;
            }

            var keyParts = ParseIndexColumns(index.Columns, names, t);
            var included = ParseIndexColumns(index.IncludedColumns, names, t);
            if (keyParts is null || keyParts.Count == 0 || included is null)
            {
                c.Notes.Add($"Index {index.Name} uses expressions and is not copied.");
                continue;
            }

            var kind = "";
            if (c.SameEngine && t.ProviderKey == SqlDialect.SqlServerKey)
                kind = type.StartsWith("CLUSTERED", StringComparison.Ordinal) ? "CLUSTERED " : "NONCLUSTERED ";
            var method = c.SameEngine && t.ProviderKey == SqlDialect.PostgresKey && !string.IsNullOrEmpty(index.Type) && !Same(index.Type, "btree")
                ? $" USING {index.Type}"
                : "";

            var indexName = t.TruncateIdentifier(RenameFor(index.Name, table, name));
            var sql = $"CREATE {(index.IsUnique ? "UNIQUE " : "")}{kind}INDEX {t.Quote(indexName)} ON {t.Table(schema, name)}{method} " +
                      $"({string.Join(", ", keyParts)})" +
                      (included.Count > 0 ? $" INCLUDE ({string.Join(", ", included)})" : "");
            if (!string.IsNullOrWhiteSpace(index.Filter))
            {
                if (c.SameEngine) sql += $" WHERE {index.Filter}";
                else c.Notes.Add($"Index {index.Name} is created without its filter ({index.Filter}), which is engine-specific.");
            }
            c.Add($"Create index {indexName} on {schema}.{name}", CopyStepKind.Constraint, sql + ";");
        }
    }

    /// <summary>"a, b DESC" (SQL Server) or "a, \"B c\"" (PostgreSQL) into quoted column references; null when a part is an expression.</summary>
    public static List<string>? ParseIndexColumns(string? list, IReadOnlyList<string> columns, SqlDialect dialect)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(list)) return result;

        foreach (var raw in list.Split(", "))
        {
            var part = raw.Trim();
            var suffix = "";
            if (part.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase)) { suffix = " DESC"; part = part[..^5].TrimEnd(); }
            else if (part.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase)) part = part[..^4].TrimEnd();
            if (part.Length > 1 && part[0] == '"' && part[^1] == '"') part = part[1..^1].Replace("\"\"", "\"");

            var column = columns.FirstOrDefault(col => Same(col, part));
            if (column is null) return null;
            result.Add(dialect.Quote(column) + suffix);
        }
        return result;
    }

    private static void AddForeignKeys(PlanContext c, DbObject table, string schema, string name)
    {
        var a = c.Analysis;
        var t = c.Target;
        foreach (var fk in c.SourceSession.Snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name))
        {
            var selfReference = Same(fk.ReferencedSchema, table.Schema) && Same(fk.ReferencedTable, table.Name);
            var (parentSchema, parentName) = selfReference ? (schema, name)
                : Same(fk.ReferencedSchema, a.Source.Schema) && Same(fk.ReferencedTable, a.Source.Name) ? (a.TargetSchema, a.TargetName)
                : (fk.ReferencedSchema, fk.ReferencedTable);

            var parentExists = c.CreatedTables.Contains($"{parentSchema}.{parentName}") ||
                               c.TargetSession.Snapshot.Objects.Any(o => o.Type == DbObjectType.Table && Same(o.Database, a.TargetDatabase) &&
                                                                          Same(o.Schema, parentSchema) && Same(o.Name, parentName));
            if (!parentExists || string.IsNullOrWhiteSpace(fk.Columns) || string.IsNullOrWhiteSpace(fk.ReferencedColumns))
            {
                c.Notes.Add($"Foreign key {fk.Name} is skipped: {parentSchema}.{parentName} does not exist on the right.");
                continue;
            }

            var columns = fk.Columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t.Quote);
            var referenced = fk.ReferencedColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t.Quote);
            var fkName = t.TruncateIdentifier(RenameFor(fk.Name, table, name));
            c.Add($"Create foreign key {fkName} on {schema}.{name}", CopyStepKind.Constraint,
                $"ALTER TABLE {t.Table(schema, name)} ADD CONSTRAINT {t.Quote(fkName)} FOREIGN KEY ({string.Join(", ", columns)}) " +
                $"REFERENCES {t.Table(parentSchema, parentName)} ({string.Join(", ", referenced)});");
        }
    }

    /// <summary>Constraint/index names follow the table when it is copied under another name, so they stay unique.</summary>
    private static string RenameFor(string constraintName, DbObject sourceTable, string targetName)
    {
        if (Same(sourceTable.Name, targetName)) return constraintName;
        var at = constraintName.IndexOf(sourceTable.Name, StringComparison.OrdinalIgnoreCase);
        return at >= 0
            ? constraintName[..at] + targetName + constraintName[(at + sourceTable.Name.Length)..]
            : $"{constraintName}_{targetName}";
    }

    // ----- Row matching (pure) -----

    public sealed record RowUpdate(IReadOnlyList<object?> Row, IReadOnlyList<int> ChangedColumns);

    public sealed record RowDiff(
        IReadOnlyList<IReadOnlyList<object?>> Inserts,
        IReadOnlyList<RowUpdate> Updates,
        IReadOnlyList<IReadOnlyList<object?>> Deletes,
        int Unchanged);

    /// <summary>Pairs the rows of both sides by key (both in the same column order). Deletes carry key values only.</summary>
    public static RowDiff DiffRows(
        IReadOnlyList<IReadOnlyList<object?>> sourceRows, IReadOnlyList<IReadOnlyList<object?>> targetRows, IReadOnlyList<int> keyIndexes)
    {
        var targetByKey = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.Ordinal);
        foreach (var row in targetRows) targetByKey.TryAdd(KeyOf(row, keyIndexes), row);

        var inserts = new List<IReadOnlyList<object?>>();
        var updates = new List<RowUpdate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;

        foreach (var row in sourceRows)
        {
            var key = KeyOf(row, keyIndexes);
            if (!seen.Add(key)) continue;
            if (!targetByKey.TryGetValue(key, out var existing))
            {
                inserts.Add(row);
                continue;
            }

            var changed = new List<int>();
            for (var i = 0; i < row.Count; i++)
                if (!ObjectComparisonService.ValuesEqual(Clean(row[i]), Clean(i < existing.Count ? existing[i] : null), DataCompareOptions.Default))
                    changed.Add(i);

            if (changed.Count == 0) unchanged++;
            else updates.Add(new RowUpdate(row, changed));
        }

        var deletes = targetByKey
            .Where(kv => !seen.Contains(kv.Key))
            .Select(kv => (IReadOnlyList<object?>)keyIndexes.Select(i => kv.Value[i]).ToList())
            .ToList();

        return new RowDiff(inserts, updates, deletes, unchanged);
    }

    private static object? Clean(object? value) => value is DBNull ? null : value;

    private static string KeyOf(IReadOnlyList<object?> row, IReadOnlyList<int> keyIndexes) =>
        string.Join('\u0001', keyIndexes.Select(i => Clean(row[i]) is { } v ? "v" + NormalizeKey(v) : "\u0000"));

    /// <summary>Key text that is equal for equal values read from different engines (int vs bigint, 1.0 vs 1, Guid case).</summary>
    private static string NormalizeKey(object value) => value switch
    {
        string s => s,
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToHexString(bytes),
        bool b => b ? "1" : "0",
        decimal d => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    // ----- Execution -----

    /// <summary>
    /// Runs the plan on the right side over one connection: inside a single transaction (all or nothing) or step
    /// by step. Streamed steps read the left rows page by page (keyset pagination on the primary key) and insert
    /// each page before reading the next, so memory stays flat whatever the table size.
    /// </summary>
    public async Task<CopyRunResult> ExecuteAsync(
        DatabaseSession sourceSession, DatabaseSession targetSession, CopyPlan plan,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var affected = 0;
        var transactional = plan.Options.SingleTransaction;

        await using var session = await targetSession.Provider.BeginScriptSessionAsync(
            NullIfBlank(plan.Analysis.TargetDatabase), transactional, ct);

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var prefix = $"Step {i + 1:N0} of {plan.Steps.Count:N0}";
            progress?.Report($"{prefix}: {step.Title}");
            try
            {
                affected += step.Stream is { } stream
                    ? await StreamRowsAsync(sourceSession, targetSession, session, stream, plan.Options,
                        copied => progress?.Report($"{prefix}: {copied:N0} of ≈{stream.EstimatedRows:N0} row(s) copied into {stream.TargetSchema}.{stream.TargetName}"), ct)
                    : await session.ExecuteAsync(step.Sql, plan.Options.TimeoutSeconds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Step {i + 1} ({step.Title}) failed" +
                    (transactional ? "; everything was rolled back. " : "; the steps before it were applied. ") + ex.Message, ex);
            }
        }

        progress?.Report("Committing…");
        await session.CommitAsync(ct);
        return new CopyRunResult(affected, DateTime.UtcNow - started, []);
    }

    private async Task<int> StreamRowsAsync(
        DatabaseSession sourceSession, DatabaseSession targetSession, IScriptSession target, StreamedCopy stream,
        CopyOptions options, Action<long> report, CancellationToken ct)
    {
        var source = SqlDialect.For(sourceSession.Provider.ProviderKey);
        var dialect = SqlDialect.For(targetSession.Provider.ProviderKey);
        var table = source.Table(stream.SourceTable.Schema, stream.SourceTable.Name);
        var columns = string.Join(", ", stream.SourceColumns.Select(source.SelectExpression));
        var keyIndexes = stream.KeyColumns.Select(k => stream.SourceColumns.ToList().FindIndex(col => Same(col.Name, k))).ToList();
        var keyColumns = keyIndexes.Select(i => stream.SourceColumns[i]).ToList();
        var orderBy = string.Join(", ", keyColumns.Select(col => source.Quote(col.Name)));
        var names = stream.TargetColumns.Select(col => col.Name).ToList();
        var pageSize = Math.Max(1, options.PageSize);

        IReadOnlyList<object?>? lastKey = null;
        long copied = 0;
        var affected = 0;
        while (true)
        {
            var seek = lastKey is null ? null : KeysetPredicate(source, keyColumns, lastKey);
            var where = (stream.RowFilter, seek) switch
            {
                (null, null) => null,
                ({ } f, null) => f,
                (null, { } k) => k,
                ({ } f, { } k) => $"({f}) AND ({k})"
            };

            var sql = source.SelectTop(columns, table, where, orderBy, pageSize);
            var result = await queries.ExecuteScriptAsync(sourceSession, sql, NullIfBlank(stream.SourceTable.Database), ReadTimeoutSeconds, ct);
            var rows = result.ResultSets.FirstOrDefault()?.Rows ?? [];
            if (rows.Count == 0) break;

            foreach (var statement in BuildInsertStatements(dialect, stream.TargetSchema, stream.TargetName, names, stream.TargetColumns, rows, options.BatchSize))
                affected += await target.ExecuteAsync(statement, options.TimeoutSeconds, ct);

            copied += rows.Count;
            report(copied);
            if (rows.Count < pageSize) break;
            var last = rows[^1];
            lastKey = keyIndexes.Select(i => last[i]).ToList();
        }
        return affected;
    }

    /// <summary>Rows after <paramref name="lastKey"/> in key order: (k1 &gt; v1) OR (k1 = v1 AND k2 &gt; v2) ...
    /// (spelled out because SQL Server has no row-value comparison).</summary>
    public static string KeysetPredicate(SqlDialect dialect, IReadOnlyList<DbColumn> keyColumns, IReadOnlyList<object?> lastKey)
    {
        var terms = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var parts = Enumerable.Range(0, i)
                .Select(j => $"{dialect.Quote(keyColumns[j].Name)} = {dialect.Literal(lastKey[j], keyColumns[j].BaseType)}")
                .Append($"{dialect.Quote(keyColumns[i].Name)} > {dialect.Literal(lastKey[i], keyColumns[i].BaseType)}");
            terms.Add(i == 0 ? parts.Single() : "(" + string.Join(" AND ", parts) + ")");
        }
        return string.Join(" OR ", terms);
    }

    // ----- Batches -----

    /// <summary>
    /// Orders objects for copying so dependencies come first: sequences and synonyms, then tables with every
    /// table after the tables it references (cycles keep their original order), then views, functions,
    /// procedures and finally triggers.
    /// </summary>
    public static IReadOnlyList<DbObject> OrderForCopy(IEnumerable<DbObject> objects, MetadataSnapshot sourceSnapshot)
    {
        var list = objects.Distinct().ToList();
        var tables = list.Where(o => o.Type == DbObjectType.Table).ToList();
        var byName = tables.ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
        var orderedTables = new List<DbObject>();
        var state = new Dictionary<DbObject, bool>(); // false = visiting, true = done

        void Visit(DbObject table)
        {
            if (state.ContainsKey(table)) return; // done, or a cycle: keep going
            state[table] = false;
            foreach (var fk in sourceSnapshot.ForeignKeysOf(table.Database, table.Schema, table.Name))
                if (byName.TryGetValue($"{fk.ReferencedSchema}.{fk.ReferencedTable}", out var parent) && parent != table)
                    Visit(parent);
            state[table] = true;
            orderedTables.Add(table);
        }

        foreach (var table in tables) Visit(table);

        static int Rank(DbObjectType type) => type switch
        {
            DbObjectType.Sequence or DbObjectType.Synonym => 0,
            DbObjectType.Table => 1,
            DbObjectType.View or DbObjectType.MaterializedView => 2,
            DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => 3,
            DbObjectType.Procedure => 4,
            DbObjectType.Trigger => 5,
            _ => 6
        };

        var tableOrder = orderedTables.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        return list
            .Select((o, i) => (Object: o, Index: i))
            .OrderBy(x => Rank(x.Object.Type))
            .ThenBy(x => x.Object.Type == DbObjectType.Table ? tableOrder[x.Object] : x.Index)
            .Select(x => x.Object)
            .ToList();
    }

    /// <summary>Counts the rows on both sides (with the row filter), to confirm a copy landed.</summary>
    public async Task<CopyVerification> VerifyAsync(
        DatabaseSession sourceSession, DatabaseSession targetSession, CopyAnalysis analysis, string? rowFilter, CancellationToken ct = default)
    {
        var source = SqlDialect.For(sourceSession.Provider.ProviderKey);
        var target = SqlDialect.For(targetSession.Provider.ProviderKey);
        var filter = NullIfBlank(rowFilter);

        var sourceTask = CountAsync(sourceSession, source, analysis.Source.Database, source.Table(analysis.Source.Schema, analysis.Source.Name), filter, ct);
        var targetTask = CountAsync(targetSession, target, analysis.TargetDatabase, target.Table(analysis.TargetSchema, analysis.TargetName), filter, ct);
        await Task.WhenAll(sourceTask, targetTask);

        var matches = sourceTask.Result == targetTask.Result;
        var summary = matches
            ? $"Verified: {sourceTask.Result:N0} row(s) on both sides{(filter is null ? "" : " for the filter")}."
            : $"Row counts differ: left {sourceTask.Result:N0}, right {targetTask.Result:N0}{(filter is null ? "" : " for the filter")}.";
        return new CopyVerification(sourceTask.Result, targetTask.Result, matches, summary);
    }

    // ----- Helpers -----

    private static IReadOnlyList<DbColumn> ColumnsOf(MetadataSnapshot snapshot, DbObject table) =>
        snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(col => col.Ordinal).ToList();

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Humanize(DbObjectType type) => type switch
    {
        DbObjectType.MaterializedView => "Materialized view",
        DbObjectType.ForeignTable => "Foreign table",
        DbObjectType.ScalarFunction => "Scalar function",
        DbObjectType.TableFunction => "Table function",
        _ => type.ToString()
    };

    public static string Humanize(CopyAction action) => action switch
    {
        CopyAction.CreateObject => "Create on the right",
        CopyAction.MergeData => "Merge data",
        CopyAction.ReplaceData => "Replace data",
        CopyAction.DropAndRecreate => "Drop and re-create",
        CopyAction.ReplaceDefinition => "Replace definition",
        _ => action.ToString()
    };
}
