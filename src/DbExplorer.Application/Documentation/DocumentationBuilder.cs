using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Documentation;

/// <summary>
/// Turns the cached catalog of one database (plus the comments and, optionally, the defaults and checks read from the
/// server) into a <see cref="DatabaseDocument"/> for <see cref="DocumentationWriter"/>.
/// </summary>
public static class DocumentationBuilder
{
    /// <summary>Tables drawn in the diagram; the rest are still documented, and the file says how many were left out.</summary>
    public const int MaxDiagramTables = 80;

    /// <summary>Everything except the server round-trips: the catalog, the comments and the per-table constraints
    /// already read.</summary>
    /// <param name="database">The database documented: a server-wide catalog is narrowed to it; a catalog read for
    /// one database is used whole.</param>
    public static DatabaseDocument Build(
        MetadataSnapshot snapshot, string? database, DocumentationOptions options, CatalogComments? comments = null,
        IReadOnlyDictionary<DbObject, DbTableConstraints>? constraints = null, DateTimeOffset? generatedAt = null)
    {
        comments ??= CatalogComments.None;
        // A catalog read for one database stores no database names; a server-wide one holds several to pick from.
        var filter = !string.IsNullOrEmpty(database) && snapshot.ContainsDatabase(database) ? database : null;
        bool InScope(string db, string schema) =>
            (filter is null || string.Equals(db, filter, StringComparison.OrdinalIgnoreCase)) &&
            (options.Schema is null || string.Equals(schema, options.Schema, StringComparison.OrdinalIgnoreCase));

        var objects = snapshot.Objects
            .Where(o => InScope(o.Database, o.Schema))
            .GroupBy(o => (o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant(), o.Type))
            .Select(g => g.First())
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var definitions = snapshot.Modules
            .Where(m => InScope(m.Database, m.Schema) && !string.IsNullOrWhiteSpace(m.Definition))
            .GroupBy(m => (m.Schema.ToUpperInvariant(), m.Name.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => string.Join("\n\n", g.Select(m => m.Definition!.Trim())));
        string? Source(DbObject o) =>
            options.IncludeSource ? definitions.GetValueOrDefault((o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant())) : null;

        DocTable Table(DbObject o)
        {
            var db = o.Database;
            var keys = snapshot.ForeignKeysOf(db, o.Schema, o.Name)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var referencing = new Dictionary<string, DocReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in keys)
            {
                var from = ErDiagramBuilder.SplitColumns(fk.Columns).ToList();
                var to = ErDiagramBuilder.SplitColumns(fk.ReferencedColumns).ToList();
                for (var i = 0; i < from.Count; i++)
                    referencing.TryAdd(from[i], new DocReference(fk.ReferencedSchema, fk.ReferencedTable, i < to.Count ? to[i] : null));
            }

            var tableConstraints = constraints?.GetValueOrDefault(o) ?? DbTableConstraints.None;
            var columns = snapshot.ColumnsOf(db, o.Schema, o.Name)
                .OrderBy(c => c.Ordinal)
                .Select(c => new DocColumn(c)
                {
                    Comment = comments.ForColumn(o.Schema, o.Name, c.Name),
                    Default = tableConstraints.Defaults.GetValueOrDefault(c.Name),
                    References = referencing.GetValueOrDefault(c.Name)
                })
                .ToList();

            return new DocTable(o)
            {
                Comment = comments.ForObject(o.Schema, o.Name),
                Columns = columns,
                Indexes = snapshot.IndexesOf(db, o.Schema, o.Name)
                    .OrderByDescending(i => i.IsPrimaryKey).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                ForeignKeys = keys,
                ReferencedBy = snapshot.ReferencesTo(db, o.Schema, o.Name)
                    .OrderBy(f => f.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Table, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Checks = tableConstraints.Checks.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Definition = o.Type is DbObjectType.View or DbObjectType.MaterializedView ? Source(o) : null
            };
        }

        DocObject Other(DbObject o) => new(o) { Comment = comments.ForObject(o.Schema, o.Name), Definition = Source(o) };

        var notes = new List<string>();
        ErDiagram? diagram = null;
        var tables = objects.Where(o => o.Type is DbObjectType.Table or DbObjectType.ForeignTable).ToList();
        if (options.IncludeDiagram && tables.Count > 0)
        {
            var scoped = filter is null ? snapshot : snapshot.ForDatabase(filter);
            diagram = ErDiagramBuilder.WholeSchema(scoped, filter, options.Schema, ErColumnMode.KeysOnly, MaxDiagramTables);
            if (diagram.Tables.Count == 0) diagram = null;
            else if (diagram.OmittedTables > 0)
                notes.Add($"The diagram shows the {diagram.Tables.Count} most connected tables; {diagram.OmittedTables} more are listed below but not drawn.");
        }

        var schemas = objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var schemaComments = schemas
            .Select(s => (s, comments.ForSchema(s)))
            .Where(p => p.Item2 is not null)
            .ToDictionary(p => p.s, p => p.Item2!, StringComparer.OrdinalIgnoreCase);

        var title = string.IsNullOrEmpty(database) ? "Database documentation" : database;
        if (options.Schema is not null) title += $" · {options.Schema}";

        return new DatabaseDocument
        {
            Title = title,
            Database = database,
            Schema = options.Schema,
            GeneratedAt = generatedAt ?? DateTimeOffset.Now,
            Comment = comments.Database,
            SchemaComments = schemaComments,
            Tables = tables.Select(Table).ToList(),
            Views = objects.Where(o => o.Type is DbObjectType.View or DbObjectType.MaterializedView).Select(Table).ToList(),
            Routines = objects.Where(o => o.IsRoutine).Select(Other).ToList(),
            Triggers = objects.Where(o => o.Type == DbObjectType.Trigger).Select(Other).ToList(),
            Sequences = objects.Where(o => o.Type == DbObjectType.Sequence).Select(Other).ToList(),
            Synonyms = objects.Where(o => o.Type == DbObjectType.Synonym).Select(Other).ToList(),
            Diagram = diagram,
            Notes = notes,
            ShowRowCounts = options.IncludeRowCounts
        };
    }

    /// <summary>
    /// Reads the comments and (with <see cref="DocumentationOptions.IncludeConstraints"/>) every table's defaults and
    /// checks from the server, then builds the document. A failed read leaves that part out and says so in the notes
    /// instead of failing the export.
    /// </summary>
    public static async Task<DatabaseDocument> LoadAsync(
        IDatabaseProvider provider, MetadataSnapshot snapshot, string? database, DocumentationOptions options,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var notes = new List<string>();

        progress?.Report("Reading comments…");
        CatalogComments comments;
        try
        {
            comments = await CatalogComments.LoadAsync(provider, database, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            comments = CatalogComments.None;
            notes.Add($"Comments could not be read: {FirstLine(ex.Message)}");
        }

        var constraints = new Dictionary<DbObject, DbTableConstraints>();
        if (options.IncludeConstraints)
        {
            var tables = Build(snapshot, database, options with { IncludeDiagram = false, IncludeSource = false }).Tables;
            var failed = 0;
            string? firstError = null;
            for (var i = 0; i < tables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Reading defaults and checks ({i + 1} of {tables.Count})…");
                try
                {
                    constraints[tables[i].Object] = await provider.GetTableConstraintsAsync(tables[i].Object, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    firstError ??= FirstLine(ex.Message);
                }
            }
            if (failed > 0)
                notes.Add($"Defaults and checks of {failed} table(s) could not be read: {firstError}");
        }

        progress?.Report("Writing…");
        var doc = Build(snapshot, database, options, comments, constraints);
        return doc with { Notes = [.. doc.Notes, .. notes] };
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
