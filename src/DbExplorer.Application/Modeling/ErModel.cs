using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;

namespace DbExplorer.Application.Modeling;

/// <summary>One table of an <see cref="ErModel"/>: its design (what the table should look like) and where it sits on the
/// canvas.</summary>
public sealed record ModelTable
{
    /// <summary>Stable while the table is renamed, so the canvas and the editor keep pointing at it.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TableDesign Design { get; init; } = new();

    /// <summary>The table as it was on the server when it was read into the model; null for a table drawn here. The
    /// editor reviews changes against it, and Generate DDL finds the server table by its name even after a rename.</summary>
    public TableDesign? Baseline { get; init; }

    public double X { get; init; }
    public double Y { get; init; }

    public string Schema(string providerKey) => TableScriptBuilder.SchemaOf(Design, providerKey);
    public string Name => Design.Name.Trim();
    public string FullName(string providerKey) => $"{Schema(providerKey)}.{Name}";
}

/// <summary>
/// An editable entity-relationship model: tables, their columns and the foreign keys between them, drawn on a canvas
/// and turned into CREATE or ALTER scripts against a database by <see cref="ErModelScriptBuilder"/>. Immutable: every
/// edit returns a new model, which keeps undo and the canvas simple.
/// </summary>
public sealed record ErModel
{
    public string Name { get; init; } = "Untitled model";

    /// <summary>Engine the column types are written for.</summary>
    public string ProviderKey { get; init; } = SqlDialect.SqlServerKey;

    /// <summary>The database the model was read from; Generate DDL targets it unless another is picked.</summary>
    public string Database { get; init; } = "";

    public IReadOnlyList<ModelTable> Tables { get; init; } = [];

    public static ErModel Empty(string providerKey, string database = "") => new() { ProviderKey = providerKey, Database = database };

    public ModelTable? Find(string id) => Tables.FirstOrDefault(t => t.Id == id);

    public ModelTable? FindByName(string schema, string name) =>
        Tables.FirstOrDefault(t => TableDesign.Same(t.Schema(ProviderKey), schema) && TableDesign.Same(t.Name, name));

    public ErModel Add(ModelTable table) => this with { Tables = [.. Tables, table] };

    public ErModel Move(string id, double x, double y) => Change(id, t => t with { X = Math.Max(0, x), Y = Math.Max(0, y) });

    /// <summary>Replaces a table's design. A renamed table (or moved schema) takes the other tables' foreign keys to it
    /// along, as the server does.</summary>
    public ErModel Update(string id, TableDesign design)
    {
        if (Find(id) is not { } before) return this;
        var oldSchema = before.Schema(ProviderKey);
        var oldName = before.Name;
        var newSchema = TableScriptBuilder.SchemaOf(design, ProviderKey);
        var newName = design.Name.Trim();
        var renamed = !TableDesign.Same(oldSchema, newSchema) || !string.Equals(oldName, newName, StringComparison.Ordinal);
        return this with
        {
            Tables = Tables.Select(t =>
            {
                if (t.Id == id) return t with { Design = design };
                if (!renamed || oldName.Length == 0) return t;
                return t with
                {
                    Design = t.Design with
                    {
                        ForeignKeys = t.Design.ForeignKeys.Select(f => References(f, oldSchema, oldName)
                            ? f with { ReferencedSchema = newSchema, ReferencedTable = newName } : f).ToList()
                    }
                };
            }).ToList()
        };
    }

    /// <summary>A column of a table renamed: the table's own keys and indexes follow (see <see cref="TableDesign.RenameColumn"/>),
    /// and so do the other tables' foreign keys that reference it.</summary>
    public ErModel RenameColumn(string id, string oldName, string newName)
    {
        if (Find(id) is not { } table || oldName.Trim().Length == 0) return this;
        var schema = table.Schema(ProviderKey);
        return this with
        {
            Tables = Tables.Select(t =>
            {
                var design = t.Id == id ? t.Design.RenameColumn(oldName, newName) : t.Design;
                design = design with
                {
                    ForeignKeys = design.ForeignKeys.Select(f => References(f, schema, table.Name) && TableDesign.Same(f.ReferencedColumn.Trim(), oldName.Trim())
                        ? f with { ReferencedColumn = newName.Trim() } : f).ToList()
                };
                return t with { Design = design };
            }).ToList()
        };
    }

    /// <summary>Takes a table off the model, with the other tables' foreign keys to it. Nothing is dropped on the server:
    /// Generate DDL leaves tables the model does not have alone.</summary>
    public ErModel Remove(string id)
    {
        if (Find(id) is not { } table) return this;
        var schema = table.Schema(ProviderKey);
        return this with
        {
            Tables = Tables.Where(t => t.Id != id).Select(t => t with
            {
                Design = t.Design with { ForeignKeys = t.Design.ForeignKeys.Where(f => !References(f, schema, table.Name)).ToList() }
            }).ToList()
        };
    }

    /// <summary>The foreign keys drawn between two tables of the model: child table, its key, parent table.</summary>
    public IEnumerable<(ModelTable Child, ForeignKeyDesign Key, ModelTable Parent)> Relationships()
    {
        foreach (var child in Tables)
        foreach (var fk in child.Design.ForeignKeys)
        {
            var parent = FindByName(string.IsNullOrWhiteSpace(fk.ReferencedSchema) ? TableScriptBuilder.DefaultSchema(ProviderKey, Database) : fk.ReferencedSchema, fk.ReferencedTable);
            if (parent is not null) yield return (child, fk, parent);
        }
    }

    /// <summary>
    /// Makes <paramref name="column"/> of the child table reference <paramref name="parentColumn"/> of the parent
    /// (dragging one column onto another). A column with no type yet takes the referenced column's type. Null when
    /// either column is missing or the key exists already.
    /// </summary>
    public ErModel? Link(string childId, string column, string parentId, string parentColumn)
    {
        if (Find(childId) is not { } child || Find(parentId) is not { } parent) return null;
        if (child.Design.Column(column) is not { } from || parent.Design.Column(parentColumn) is not { } to) return null;
        var schema = parent.Schema(ProviderKey);
        if (child.Design.ForeignKeys.Any(f => TableDesign.Same(f.Column.Trim(), from.Name.Trim()) && References(f, schema, parent.Name)))
            return null;

        var design = child.Design.AddForeignKey(new ForeignKeyDesign
        {
            Column = from.Name.Trim(), ReferencedSchema = schema, ReferencedTable = parent.Name, ReferencedColumn = to.Name.Trim()
        });
        if (from.Type.Trim().Length == 0)
            design = design.WithColumn(from.Name, c => c with { Type = to.Type, Size = to.Size });
        return Update(childId, design);
    }

    /// <summary>
    /// Dragging a column onto another table's title: the column references that table's single-column primary key. When
    /// the drag starts at the child's title instead, a new column named after the parent's key (CustomerId, customer_id)
    /// is added for it, not null in a new table and nullable in one that has rows already.
    /// </summary>
    public ErModel? LinkToTable(string childId, string? column, string parentId)
    {
        if (Find(childId) is not { } child || Find(parentId) is not { } parent) return null;
        var keys = parent.Design.PrimaryKey.ToList();
        if (keys.Count != 1) return null;
        var key = keys[0];
        if (column is not null) return Link(childId, column, parentId, key.Name);

        var name = ForeignKeyColumnName(child.Design, parent.Design, key);
        var design = child.Design.AddColumn(new ColumnDesign
        {
            Name = name, Type = key.Type, Size = key.Size, IsNullable = child.Baseline is not null
        });
        return (this with { Tables = Tables.Select(t => t.Id == childId ? t with { Design = design } : t).ToList() })
            .Link(childId, name, parentId, key.Name);
    }

    /// <summary>Customers.Id → CustomerId; customers.id → customer_id; Customers.CustomerId → CustomerId. A second key
    /// to the same table gets a number.</summary>
    public static string ForeignKeyColumnName(TableDesign child, TableDesign parent, ColumnDesign key)
    {
        var keyName = key.Name.Trim();
        string name;
        if (DesignContext.Simplify(keyName) == "id")
        {
            var stem = DesignContext.Singularize(parent.Name.Trim());
            var snake = DesignContext.StyleOf(keyName) == NamingStyle.Snake && keyName == keyName.ToLowerInvariant() &&
                        DesignContext.StyleOf(stem) is NamingStyle.Snake or NamingStyle.Unknown && stem == stem.ToLowerInvariant();
            name = snake ? $"{DesignContext.ToStyle(stem, NamingStyle.Snake)}_{keyName}" : DesignContext.ToStyle(stem, NamingStyle.Pascal) + "Id";
        }
        else name = keyName;

        var candidate = name;
        for (var i = 2; child.Column(candidate) is not null; i++) candidate = name + i;
        return candidate;
    }

    /// <summary>A name like NewTable, NewTable2 that no table of the schema has.</summary>
    public string UniqueTableName(string schema, string name)
    {
        var candidate = name;
        for (var i = 2; FindByName(schema, candidate) is not null; i++) candidate = name + i;
        return candidate;
    }

    private ErModel Change(string id, Func<ModelTable, ModelTable> change) =>
        this with { Tables = Tables.Select(t => t.Id == id ? change(t) : t).ToList() };

    private bool References(ForeignKeyDesign fk, string schema, string name) =>
        TableDesign.Same(string.IsNullOrWhiteSpace(fk.ReferencedSchema) ? TableScriptBuilder.DefaultSchema(ProviderKey, Database) : fk.ReferencedSchema, schema) &&
        TableDesign.Same(fk.ReferencedTable, name);
}
