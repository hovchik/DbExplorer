using System.Globalization;
using System.Text;
using DbExplorer.Application.Diagram;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Documentation;

/// <summary>Writes a <see cref="DatabaseDocument"/> as one Markdown file (GitHub-flavoured, Mermaid diagram) or one
/// self-contained HTML page (see <see cref="ToHtml"/>).</summary>
public static partial class DocumentationWriter
{
    public static string ToMarkdown(DatabaseDocument doc)
    {
        var sb = new StringBuilder();
        string Name(DbObject o) => doc.MultipleSchemas ? o.FullName : o.Name;
        string Link(DbObject o, string anchor) => $"[{Md(Name(o))}](#{anchor})";

        sb.Append("# ").Append(Md(doc.Title)).Append("\n\n");
        sb.Append(Md(Subtitle(doc))).Append("\n\n");
        if (doc.Comment is { } comment) sb.Append(Quote(comment)).Append("\n\n");
        foreach (var note in doc.Notes) sb.Append("> **Note:** ").Append(Md(note)).Append("\n\n");

        // ----- Summary and contents -----
        sb.Append("## Contents\n\n");
        if (doc.Diagram is not null) sb.Append("- [Diagram](#diagram)\n");
        void Toc<T>(string title, string anchor, IReadOnlyList<T> items, Func<T, DbObject> obj, Func<T, string> itemAnchor)
        {
            if (items.Count == 0) return;
            sb.Append("- [").Append(title).Append(" (").Append(items.Count).Append(")](#").Append(anchor).Append(")\n");
            foreach (var item in items) sb.Append("  - ").Append(Link(obj(item), itemAnchor(item))).Append('\n');
        }
        Toc("Tables", "tables", doc.Tables, t => t.Object, t => t.Anchor);
        Toc("Views", "views", doc.Views, t => t.Object, t => t.Anchor);
        Toc("Routines", "routines", doc.Routines, r => r.Object, r => r.Anchor);
        Toc("Triggers", "triggers", doc.Triggers, r => r.Object, r => r.Anchor);
        if (doc.Sequences.Count > 0) sb.Append("- [Sequences (").Append(doc.Sequences.Count).Append(")](#sequences)\n");
        if (doc.Synonyms.Count > 0) sb.Append("- [Synonyms (").Append(doc.Synonyms.Count).Append(")](#synonyms)\n");
        if (doc.Tables.Count + doc.Views.Count + doc.Routines.Count + doc.Triggers.Count + doc.Sequences.Count + doc.Synonyms.Count == 0)
            sb.Append("This database has no objects to document.\n");
        sb.Append('\n');

        if (doc.SchemaComments.Count > 0)
        {
            sb.Append("## Schemas\n\n| Schema | Description |\n|---|---|\n");
            foreach (var (schema, text) in doc.SchemaComments.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("| ").Append(Cell(schema)).Append(" | ").Append(Cell(text)).Append(" |\n");
            sb.Append('\n');
        }

        // ----- Diagram -----
        if (doc.Diagram is { } diagram)
        {
            sb.Append("<a id=\"diagram\"></a>\n\n## Diagram\n\n");
            sb.Append("Primary and foreign key columns only; see each table for every column.\n\n");
            sb.Append("```mermaid\n").Append(ErDiagramBuilder.ToMermaid(diagram)).Append("```\n\n");
        }

        // ----- Tables and views -----
        void Tables(string title, string anchor, IReadOnlyList<DocTable> tables)
        {
            if (tables.Count == 0) return;
            sb.Append("<a id=\"").Append(anchor).Append("\"></a>\n\n## ").Append(title).Append("\n\n");
            foreach (var t in tables)
            {
                sb.Append("<a id=\"").Append(t.Anchor).Append("\"></a>\n\n### ").Append(Md(Name(t.Object))).Append("\n\n");
                var facts = new List<string> { TypeName(t.Object.Type) };
                if (doc.ShowRowCounts && t.Object.RowCount is long rows && rows >= 0)
                    facts.Add($"about {rows.ToString("N0", CultureInfo.InvariantCulture)} rows");
                if (t.Object.ModifiedAt is DateTime modified) facts.Add($"changed {modified:yyyy-MM-dd}");
                sb.Append('*').Append(string.Join(" · ", facts)).Append("*\n\n");
                if (t.Comment is { } c) sb.Append(Md(c)).Append("\n\n");

                if (t.Columns.Count > 0)
                {
                    var showDefault = t.Columns.Any(col => col.Default is not null);
                    sb.Append("| # | Column | Type | Null | ").Append(showDefault ? "Default | " : "").Append("Key | Description |\n");
                    sb.Append("|--:|---|---|---|").Append(showDefault ? "---|" : "").Append("---|---|\n");
                    foreach (var col in t.Columns)
                    {
                        var c0 = col.Column;
                        sb.Append("| ").Append(c0.Ordinal).Append(" | ").Append(c0.IsPrimaryKey ? "**" + Cell(c0.Name) + "**" : Cell(c0.Name))
                          .Append(" | ").Append(Code(c0.DataType, inTable: true)).Append(" | ").Append(c0.IsNullable ? "yes" : "no").Append(" | ");
                        if (showDefault) sb.Append(col.Default is { } d ? Code(d, inTable: true) : "").Append(" | ");
                        sb.Append(KeyText(col, r => $"[{Cell(r.Schema)}.{Cell(r.Table)}](#{r.Anchor})")).Append(" | ")
                          .Append(Cell(col.Comment ?? "")).Append(" |\n");
                    }
                    sb.Append('\n');
                }

                if (t.Indexes.Count > 0)
                {
                    sb.Append("**Indexes**\n\n| Name | Columns | Unique | Type |\n|---|---|---|---|\n");
                    foreach (var i in t.Indexes)
                    {
                        var columns = i.Columns ?? "";
                        if (!string.IsNullOrEmpty(i.IncludedColumns)) columns += $" (include {i.IncludedColumns})";
                        if (!string.IsNullOrEmpty(i.Filter)) columns += $" where {i.Filter}";
                        sb.Append("| ").Append(Cell(i.Name)).Append(i.IsPrimaryKey ? " (primary key)" : "").Append(" | ").Append(Cell(columns))
                          .Append(" | ").Append(i.IsUnique ? "yes" : "no").Append(" | ").Append(Cell(i.Type)).Append(" |\n");
                    }
                    sb.Append('\n');
                }

                if (t.ForeignKeys.Count > 0)
                {
                    sb.Append("**Foreign keys**\n\n");
                    foreach (var fk in t.ForeignKeys)
                        sb.Append("- ").Append(Code(fk.Name)).Append(": (").Append(Md(fk.Columns ?? "")).Append(") → [")
                          .Append(Md($"{fk.ReferencedSchema}.{fk.ReferencedTable}")).Append("](#")
                          .Append(DocumentationAnchors.For("table", fk.ReferencedSchema, fk.ReferencedTable)).Append(") (")
                          .Append(Md(fk.ReferencedColumns ?? "")).Append(')').Append(Flags(fk)).Append('\n');
                    sb.Append('\n');
                }

                if (t.ReferencedBy.Count > 0)
                {
                    sb.Append("**Referenced by**\n\n");
                    foreach (var fk in t.ReferencedBy)
                        sb.Append("- [").Append(Md($"{fk.Schema}.{fk.Table}")).Append("](#").Append(DocumentationAnchors.For("table", fk.Schema, fk.Table))
                          .Append(") (").Append(Md(fk.Columns ?? "")).Append(") via ").Append(Code(fk.Name)).Append(Flags(fk)).Append('\n');
                    sb.Append('\n');
                }

                if (t.Checks.Count > 0)
                {
                    sb.Append("**Checks**\n\n");
                    foreach (var check in t.Checks) sb.Append("- ").Append(Code(check.Name)).Append(": ").Append(Code(check.Expression)).Append('\n');
                    sb.Append('\n');
                }

                if (t.Definition is { } definition) sb.Append(Fence(definition)).Append('\n');
            }
        }
        Tables("Tables", "tables", doc.Tables);
        Tables("Views", "views", doc.Views);

        // ----- Routines and triggers -----
        void Objects(string title, string anchor, IReadOnlyList<DocObject> items)
        {
            if (items.Count == 0) return;
            sb.Append("<a id=\"").Append(anchor).Append("\"></a>\n\n## ").Append(title).Append("\n\n");
            foreach (var r in items)
            {
                sb.Append("<a id=\"").Append(r.Anchor).Append("\"></a>\n\n### ").Append(Md(Name(r.Object))).Append("\n\n");
                sb.Append('*').Append(TypeName(r.Object.Type));
                if (r.Object.ModifiedAt is DateTime modified) sb.Append($" · changed {modified:yyyy-MM-dd}");
                sb.Append("*\n\n");
                if (r.Comment is { } c) sb.Append(Md(c)).Append("\n\n");
                if (r.Definition is { } definition) sb.Append(Fence(definition)).Append('\n');
            }
        }
        Objects("Routines", "routines", doc.Routines);
        Objects("Triggers", "triggers", doc.Triggers);

        // ----- Sequences and synonyms: one table each -----
        void List(string title, string anchor, IReadOnlyList<DocObject> items)
        {
            if (items.Count == 0) return;
            var showDefinition = items.Any(r => r.Definition is not null);
            sb.Append("<a id=\"").Append(anchor).Append("\"></a>\n\n## ").Append(title).Append("\n\n| Name | Description |")
              .Append(showDefinition ? " Definition |\n|---|---|---|\n" : "\n|---|---|\n");
            foreach (var r in items)
            {
                sb.Append("| <a id=\"").Append(r.Anchor).Append("\"></a>").Append(Cell(Name(r.Object))).Append(" | ").Append(Cell(r.Comment ?? "")).Append(" |");
                if (showDefinition) sb.Append(' ').Append(r.Definition is { } d ? Code(d, inTable: true) : "").Append(" |");
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        List("Sequences", "sequences", doc.Sequences);
        List("Synonyms", "synonyms", doc.Synonyms);

        return sb.ToString().TrimEnd() + "\n";
    }

    // ----- Shared text helpers -----

    internal static string Subtitle(DatabaseDocument doc)
    {
        var parts = new List<string>();
        var version = doc.ServerVersion is { } v ? ShortVersion(v) : null;
        if (doc.Engine is { } engine)
            parts.Add(version is null ? engine : version.Contains(engine, StringComparison.OrdinalIgnoreCase) ? version : $"{engine} {version}");
        else if (version is not null) parts.Add(version);
        if (doc.Connection is { } connection) parts.Add($"connection {connection}");
        if (doc.Schema is { } schema) parts.Add($"schema {schema}");
        parts.Add($"generated {doc.GeneratedAt:yyyy-MM-dd HH:mm} by DbExplorer");
        return string.Join(" · ", parts);
    }

    internal static string TypeName(DbObjectType type) => type switch
    {
        DbObjectType.MaterializedView => "Materialized view",
        DbObjectType.ForeignTable => "Foreign table",
        DbObjectType.ScalarFunction => "Scalar function",
        DbObjectType.TableFunction => "Table-valued function",
        _ => type.ToString()
    };

    internal static string Flags(DbForeignKey fk) =>
        (fk.IsVirtual ? " *(inferred, not in the database)*" : "") + (fk.IsDisabled ? " *(disabled)*" : "");

    internal static string KeyText(DocColumn col, Func<DocReference, string> link)
    {
        var parts = new List<string>();
        if (col.Column.IsPrimaryKey) parts.Add("PK");
        if (col.References is { } r) parts.Add("FK → " + link(r) + (r.Column is null ? "" : $" ({r.Column})"));
        if (col.Column.IsIdentity) parts.Add("identity");
        if (col.Column.IsComputed) parts.Add("computed");
        return string.Join(", ", parts);
    }

    /// <summary>"PostgreSQL 16.4 (Ubuntu …) on x86_64…, compiled by gcc…" → "PostgreSQL 16.4"; "Microsoft SQL Server 2022 (RTM-CU12)
    /// … Copyright …" → "Microsoft SQL Server 2022"; short versions ("8.0.36") stay as they are.</summary>
    internal static string ShortVersion(string version)
    {
        var line = FirstLine(version);
        foreach (var cut in new[] { " (", " on ", ", compiled", " - " })
        {
            var at = line.IndexOf(cut, StringComparison.Ordinal);
            if (at > 0) line = line[..at];
        }
        return line.Trim();
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
    private static string OneLine(string text) => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    /// <summary>Plain text that Markdown would otherwise read as formatting or HTML.</summary>
    private static string Md(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Text for a table cell: escaped, one line (breaks become &lt;br&gt;).</summary>
    private static string Cell(string text) =>
        string.Join("<br>", text.Replace("\r\n", "\n").Split('\n').Select(Md));

    private static string Quote(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => "> " + Md(l)));

    /// <summary>Inline code that survives backticks and pipes in the value (types like "character varying(50)", defaults).</summary>
    private static string Code(string text, bool inTable = false)
    {
        text = OneLine(text);
        if (inTable) text = text.Replace("|", "\\|");
        var ticks = new string('`', LongestRun(text, '`') + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return ticks + pad + text + pad + ticks;
    }

    private static string Fence(string code)
    {
        var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
        return fence + "sql\n" + code.Replace("\r\n", "\n").TrimEnd() + "\n" + fence + "\n";
    }

    private static int LongestRun(string text, char ch)
    {
        int best = 0, run = 0;
        foreach (var c in text)
        {
            run = c == ch ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }
}
