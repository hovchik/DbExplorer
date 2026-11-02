using DbExplorer.Application.Diagram;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Documentation;

/// <summary>What goes into an exported database documentation file.</summary>
public sealed record DocumentationOptions
{
    /// <summary>Only this schema (null: every schema of the database).</summary>
    public string? Schema { get; init; }

    /// <summary>An entity-relationship diagram of the tables (SVG in HTML, Mermaid in Markdown).</summary>
    public bool IncludeDiagram { get; init; } = true;

    /// <summary>Source code of views, routines and triggers.</summary>
    public bool IncludeSource { get; init; } = true;

    /// <summary>Column defaults and check constraints, read from the server one table at a time.</summary>
    public bool IncludeConstraints { get; init; } = true;

    /// <summary>Approximate row counts from the catalog statistics.</summary>
    public bool IncludeRowCounts { get; init; } = true;
}

/// <summary>One column with what the catalog says about it.</summary>
public sealed record DocColumn(DbColumn Column)
{
    public string? Comment { get; init; }
    public string? Default { get; init; }

    /// <summary>The column this one points at, when it is part of a foreign key.</summary>
    public DocReference? References { get; init; }
}

/// <summary>A referenced table and column (null column: the key's column list could not be matched up).</summary>
public sealed record DocReference(string Schema, string Table, string? Column)
{
    public string Anchor => DocumentationAnchors.For("table", Schema, Table);
}

/// <summary>A table, view, materialized view or foreign table.</summary>
public sealed record DocTable(DbObject Object)
{
    public string? Comment { get; init; }
    public IReadOnlyList<DocColumn> Columns { get; init; } = [];
    public IReadOnlyList<DbIndex> Indexes { get; init; } = [];
    public IReadOnlyList<DbForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Foreign keys of other tables that point at this one.</summary>
    public IReadOnlyList<DbForeignKey> ReferencedBy { get; init; } = [];
    public IReadOnlyList<DbCheckConstraint> Checks { get; init; } = [];

    /// <summary>The view's source (views only, and only with <see cref="DocumentationOptions.IncludeSource"/>).</summary>
    public string? Definition { get; init; }

    public string Anchor => DocumentationAnchors.For(Object);
}

/// <summary>A procedure, function, trigger, sequence or synonym.</summary>
public sealed record DocObject(DbObject Object)
{
    public string? Comment { get; init; }
    public string? Definition { get; init; }
    public string Anchor => DocumentationAnchors.For(Object);
}

/// <summary>Everything the documentation writers need, already filtered and sorted.</summary>
public sealed record DatabaseDocument
{
    public required string Title { get; init; }
    public string? Database { get; init; }
    public string? Schema { get; init; }
    public string? Connection { get; init; }
    public string? Engine { get; init; }
    public string? ServerVersion { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public string? Comment { get; init; }

    /// <summary>Comments on schemas, keyed by schema name.</summary>
    public IReadOnlyDictionary<string, string> SchemaComments { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<DocTable> Tables { get; init; } = [];
    public IReadOnlyList<DocTable> Views { get; init; } = [];
    public IReadOnlyList<DocObject> Routines { get; init; } = [];
    public IReadOnlyList<DocObject> Triggers { get; init; } = [];
    public IReadOnlyList<DocObject> Sequences { get; init; } = [];
    public IReadOnlyList<DocObject> Synonyms { get; init; } = [];

    public ErDiagram? Diagram { get; init; }

    /// <summary>Things the reader should know that the file leaves out (a diagram capped at N tables, comments that
    /// could not be read).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool ShowRowCounts { get; init; } = true;

    /// <summary>Several schemas are documented, so names carry their schema.</summary>
    public bool MultipleSchemas => Tables.Concat(Views).Select(t => t.Object.Schema)
        .Concat(Routines.Concat(Triggers).Concat(Sequences).Concat(Synonyms).Select(o => o.Object.Schema))
        .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
}

/// <summary>Stable link targets ("table-dbo-orders") that survive in HTML ids and Markdown heading links.</summary>
public static class DocumentationAnchors
{
    public static string For(DbObject obj) => For(Prefix(obj.Type), obj.Schema, obj.Name);

    public static string For(string prefix, string schema, string name)
    {
        var text = $"{prefix}-{schema}-{name}".ToLowerInvariant();
        var chars = text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }

    private static string Prefix(DbObjectType type) => type switch
    {
        DbObjectType.Table or DbObjectType.ForeignTable => "table",
        DbObjectType.View or DbObjectType.MaterializedView => "view",
        DbObjectType.Trigger => "trigger",
        DbObjectType.Sequence => "sequence",
        DbObjectType.Synonym => "synonym",
        _ => "routine"
    };
}
